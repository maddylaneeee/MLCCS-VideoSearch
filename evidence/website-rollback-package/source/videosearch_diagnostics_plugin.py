"""Secure diagnostic uploads for MLCCS VideoSearch.

Route prefix: POST /api/mlccs-videosearch/diagnostics
Subpaths: /begin, /chunk, /complete, /event
"""

from __future__ import annotations

import hashlib
import json
import os
import re
import secrets
import shutil
import subprocess
import threading
import time
import zipfile
from collections import defaultdict, deque
from datetime import datetime, timezone
from pathlib import Path, PurePosixPath
from typing import Any, Dict


DIAGNOSTICS_ROOT = Path(os.getenv("VIDEOSEARCH_DIAGNOSTICS_ROOT", r"D:\lixinchenca-videosearch\diagnostics"))
TEMP_ROOT = DIAGNOSTICS_ROOT / ".uploads"
EVENT_ROOT = DIAGNOSTICS_ROOT / "events"
MAX_CHUNK_BYTES = 512 * 1024
MAX_REPORT_BYTES = 25 * 1024 * 1024
MAX_CHUNKS = 64
MAX_EXPANDED_BYTES = 250 * 1024 * 1024
MAX_COMPRESSION_RATIO = 200
TOKEN_TTL_SECONDS = 30 * 60
TEMP_RETENTION_SECONDS = 24 * 60 * 60
REPORT_RETENTION_DAYS = 180
MAX_USER_NOTE_CHARS = 12000
_REPORT_ID_RE = re.compile(r"^vs-[0-9]{8}-[a-f0-9]{24}$")
_SHA256_RE = re.compile(r"^[a-f0-9]{64}$")
_INSTALL_HASH_RE = re.compile(r"^[a-f0-9]{32,128}$")
_ALLOWED_ZIP_SUFFIXES = {".json", ".jsonl", ".txt", ".log", ".dmp"}
_FORBIDDEN_EXECUTABLE_SUFFIXES = {".exe", ".dll", ".msi", ".com", ".bat", ".cmd", ".ps1", ".scr", ".sys"}
_EVENT_FIELDS = {
    "event", "app_version", "windows_build", "cpu_tier", "gpu_tier", "modalities",
    "model_ids", "indexed_files", "indexed_seconds", "success_count", "failure_count",
    "error_code", "update_result", "installation_hash", "occurred_at",
}
_SENSITIVE_FIELD_FRAGMENTS = ("path", "query", "search", "transcript", "ocr", "media", "thumbnail", "vector")
_RATE_LOCK = threading.Lock()
_RATE_BUCKETS: dict[str, deque[float]] = defaultdict(deque)
_CLEANUP_LOCK = threading.Lock()
_LAST_CLEANUP = 0.0


def _response(status: int, payload: Dict[str, Any]) -> Dict[str, Any]:
    return {
        "status": status,
        "headers": {"Content-Type": "application/json; charset=utf-8", "Cache-Control": "no-store"},
        "body": json.dumps(payload, ensure_ascii=False),
    }


def _json_body(request: Dict[str, Any]) -> dict[str, Any]:
    body = request.get("body") or b""
    if isinstance(body, str):
        body = body.encode("utf-8")
    if len(body) > 128 * 1024:
        raise ValueError("JSON_BODY_TOO_LARGE")
    value = json.loads(body.decode("utf-8"))
    if not isinstance(value, dict):
        raise ValueError("BODY_MUST_BE_OBJECT")
    return value


def _header(request: Dict[str, Any], name: str) -> str:
    headers = request.get("headers") or {}
    for key, value in headers.items():
        if str(key).lower() == name.lower():
            return str(value or "")
    return ""


def _query(request: Dict[str, Any], name: str) -> str:
    value = (request.get("query") or {}).get(name, "")
    if isinstance(value, list):
        value = value[0] if value else ""
    return str(value or "")


def _client_ip(request: Dict[str, Any]) -> str:
    for field in ("client_ip", "remote_addr", "ip"):
        value = str(request.get(field) or "").strip()
        if value:
            return value[:80]
    forwarded = _header(request, "X-Forwarded-For").split(",", 1)[0].strip()
    return forwarded[:80] or "unknown"


def _rate_limit(key: str, limit: int, window_seconds: int = 3600) -> None:
    now = time.time()
    with _RATE_LOCK:
        bucket = _RATE_BUCKETS[key]
        while bucket and bucket[0] < now - window_seconds:
            bucket.popleft()
        if len(bucket) >= limit:
            raise PermissionError("RATE_LIMITED")
        bucket.append(now)


def _atomic_write(path: Path, data: bytes) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    temporary = path.with_name(f".{path.name}.tmp-{os.getpid()}-{time.time_ns()}")
    try:
        temporary.write_bytes(data)
        os.replace(str(temporary), str(path))
        return
    except OSError as exc:
        # Windows Server 2016 can return ERROR_ALREADY_EXISTS through the
        # plugin sandbox even though os.replace normally permits replacement.
        if int(getattr(exc, "winerror", 0) or 0) not in {5, 183}:
            raise
        path.unlink(missing_ok=True)
        os.replace(str(temporary), str(path))
    finally:
        temporary.unlink(missing_ok=True)


def _report_id() -> str:
    return f"vs-{datetime.now(timezone.utc):%Y%m%d}-{secrets.token_hex(12)}"


def _upload_dir(report_id: str) -> Path:
    if not _REPORT_ID_RE.fullmatch(report_id):
        raise ValueError("INVALID_REPORT_ID")
    return TEMP_ROOT / report_id


def _load_transaction(report_id: str, token: str) -> tuple[Path, dict[str, Any]]:
    directory = _upload_dir(report_id)
    metadata_path = directory / "transaction.json"
    if not metadata_path.exists():
        raise FileNotFoundError("UPLOAD_NOT_FOUND")
    metadata = json.loads(metadata_path.read_text(encoding="utf-8"))
    if time.time() > float(metadata["expires_at"]):
        raise PermissionError("TOKEN_EXPIRED")
    supplied = hashlib.sha256(token.encode("utf-8")).hexdigest()
    if not secrets.compare_digest(supplied, str(metadata["token_hash"])):
        raise PermissionError("INVALID_TOKEN")
    return directory, metadata


def _cleanup_stale_uploads(now: float | None = None) -> None:
    if not TEMP_ROOT.exists():
        return
    cutoff = (time.time() if now is None else now) - TEMP_RETENTION_SECONDS
    for item in TEMP_ROOT.iterdir():
        try:
            if item.is_dir() and item.stat().st_mtime < cutoff:
                shutil.rmtree(item, ignore_errors=True)
        except OSError:
            continue


def _cleanup_expired_reports(now: float | None = None) -> None:
    """Delete completed report directories after the documented 180-day retention period."""
    if not DIAGNOSTICS_ROOT.exists():
        return
    cutoff = (time.time() if now is None else now) - REPORT_RETENTION_DAYS * 24 * 60 * 60
    for year in DIAGNOSTICS_ROOT.iterdir():
        if not year.is_dir() or not re.fullmatch(r"20\d{2}", year.name):
            continue
        for report in year.glob("[01][0-9]/[0-3][0-9]/vs-*"):
            try:
                if report.is_dir() and _REPORT_ID_RE.fullmatch(report.name) and report.stat().st_mtime < cutoff:
                    shutil.rmtree(report, ignore_errors=True)
            except OSError:
                continue


def _periodic_cleanup() -> None:
    global _LAST_CLEANUP
    now = time.time()
    with _CLEANUP_LOCK:
        if now - _LAST_CLEANUP < 60 * 60:
            return
        _cleanup_stale_uploads(now)
        _cleanup_expired_reports(now)
        _LAST_CLEANUP = now


def _begin(request: Dict[str, Any]) -> Dict[str, Any]:
    body = _json_body(request)
    expected_size = int(body.get("size") or 0)
    expected_sha256 = str(body.get("sha256") or "").lower()
    total_chunks = int(body.get("total_chunks") or 0)
    installation_hash = str(body.get("installation_hash") or "").lower()
    if not 0 < expected_size <= MAX_REPORT_BYTES:
        raise ValueError("REPORT_SIZE_OUT_OF_RANGE")
    if not _SHA256_RE.fullmatch(expected_sha256):
        raise ValueError("INVALID_SHA256")
    if not 0 < total_chunks <= MAX_CHUNKS or total_chunks != (expected_size + MAX_CHUNK_BYTES - 1) // MAX_CHUNK_BYTES:
        raise ValueError("INVALID_CHUNK_COUNT")
    if not _INSTALL_HASH_RE.fullmatch(installation_hash):
        raise ValueError("INVALID_INSTALLATION_HASH")
    _rate_limit("ip:" + _client_ip(request), 30)
    _rate_limit("install:" + installation_hash, 20)

    report_id = _report_id()
    token = secrets.token_urlsafe(32)
    directory = _upload_dir(report_id)
    directory.mkdir(parents=True, exist_ok=False)
    metadata = {
        "report_id": report_id,
        "size": expected_size,
        "sha256": expected_sha256,
        "total_chunks": total_chunks,
        "installation_hash": installation_hash,
        "token_hash": hashlib.sha256(token.encode("utf-8")).hexdigest(),
        "created_at": time.time(),
        "expires_at": time.time() + TOKEN_TTL_SECONDS,
        "client_ip_hash": hashlib.sha256(_client_ip(request).encode("utf-8")).hexdigest(),
        "completed": False,
    }
    _atomic_write(directory / "transaction.json", json.dumps(metadata, sort_keys=True).encode("utf-8"))
    return _response(200, {"ok": True, "report_id": report_id, "token": token, "chunk_size": MAX_CHUNK_BYTES, "expires_in": TOKEN_TTL_SECONDS})


def _chunk(request: Dict[str, Any]) -> Dict[str, Any]:
    report_id = _query(request, "report_id")
    token = _header(request, "X-Upload-Token") or _query(request, "token")
    index = int(_query(request, "index"))
    directory, metadata = _load_transaction(report_id, token)
    if metadata.get("completed"):
        raise FileExistsError("ALREADY_COMPLETED")
    if not 0 <= index < int(metadata["total_chunks"]):
        raise ValueError("INVALID_CHUNK_INDEX")
    body = request.get("body") or b""
    if isinstance(body, str):
        body = body.encode("utf-8")
    expected = MAX_CHUNK_BYTES
    if index == int(metadata["total_chunks"]) - 1:
        expected = int(metadata["size"]) - index * MAX_CHUNK_BYTES
    if len(body) != expected or len(body) > MAX_CHUNK_BYTES:
        raise ValueError("INVALID_CHUNK_SIZE")
    target = directory / f"{index:03d}.part"
    if target.exists():
        if hashlib.sha256(target.read_bytes()).digest() != hashlib.sha256(body).digest():
            raise FileExistsError("CHUNK_CONFLICT")
        return _response(200, {"ok": True, "report_id": report_id, "index": index, "deduplicated": True})
    _atomic_write(target, body)
    return _response(200, {"ok": True, "report_id": report_id, "index": index, "deduplicated": False})


def _validate_zip(path: Path) -> dict[str, Any]:
    expanded = 0
    files = 0
    with zipfile.ZipFile(path, "r") as archive:
        if len(archive.infolist()) > 200:
            raise ValueError("ZIP_TOO_MANY_FILES")
        for info in archive.infolist():
            pure = PurePosixPath(info.filename.replace("\\", "/"))
            if pure.is_absolute() or ".." in pure.parts or any(":" in part for part in pure.parts):
                raise ValueError("ZIP_PATH_TRAVERSAL")
            unix_mode = (info.external_attr >> 16) & 0xF000
            if unix_mode == 0xA000:
                raise ValueError("ZIP_SYMLINK_NOT_ALLOWED")
            if info.flag_bits & 0x1:
                raise ValueError("ZIP_ENCRYPTED")
            if info.is_dir():
                continue
            files += 1
            suffix = Path(pure.name).suffix.lower()
            if suffix in _FORBIDDEN_EXECUTABLE_SUFFIXES or suffix not in _ALLOWED_ZIP_SUFFIXES:
                raise ValueError("ZIP_FILE_TYPE_NOT_ALLOWED")
            expanded += info.file_size
            if expanded > MAX_EXPANDED_BYTES:
                raise ValueError("ZIP_EXPANDED_SIZE_EXCEEDED")
            compressed = max(1, info.compress_size)
            if info.file_size / compressed > MAX_COMPRESSION_RATIO:
                raise ValueError("ZIP_COMPRESSION_RATIO_EXCEEDED")
    if files == 0:
        raise ValueError("ZIP_EMPTY")
    return {"files": files, "expanded_size": expanded}


def _defender_scan(path: Path) -> dict[str, Any]:
    candidates = [
        Path(os.getenv("ProgramFiles", r"C:\Program Files")) / "Windows Defender" / "MpCmdRun.exe",
        Path(os.getenv("ProgramData", r"C:\ProgramData")) / "Microsoft" / "Windows Defender" / "Platform",
    ]
    executable = candidates[0]
    if candidates[1].is_dir():
        versions = sorted((item / "MpCmdRun.exe" for item in candidates[1].iterdir()), reverse=True)
        executable = next((item for item in versions if item.exists()), executable)
    if os.name != "nt":
        return {"status": "not-windows", "clean": True}
    if not executable.exists():
        raise RuntimeError("DEFENDER_NOT_FOUND")
    result = subprocess.run([str(executable), "-Scan", "-ScanType", "3", "-File", str(path), "-DisableRemediation"], capture_output=True, timeout=300, check=False)
    if result.returncode not in (0, 2):
        raise RuntimeError("DEFENDER_SCAN_FAILED")
    return {"status": "clean" if result.returncode == 0 else "threat", "clean": result.returncode == 0, "exit_code": result.returncode}


def _complete(request: Dict[str, Any]) -> Dict[str, Any]:
    body = _json_body(request)
    report_id = str(body.get("report_id") or "")
    token = _header(request, "X-Upload-Token") or str(body.get("token") or "")
    user_note = str(body.get("user_note") or "")[:MAX_USER_NOTE_CHARS]
    directory, metadata = _load_transaction(report_id, token)
    if metadata.get("completed"):
        raise FileExistsError("ALREADY_COMPLETED")
    part_paths = [directory / f"{index:03d}.part" for index in range(int(metadata["total_chunks"]))]
    if not all(path.exists() for path in part_paths):
        raise FileNotFoundError("MISSING_CHUNKS")
    merged = directory / "report.uploading.zip"
    digest = hashlib.sha256()
    size = 0
    with merged.open("wb") as output:
        for part in part_paths:
            data = part.read_bytes()
            output.write(data)
            digest.update(data)
            size += len(data)
    if size != int(metadata["size"]):
        merged.unlink(missing_ok=True)
        raise ValueError("SIZE_MISMATCH")
    if not secrets.compare_digest(digest.hexdigest(), str(metadata["sha256"])):
        merged.unlink(missing_ok=True)
        raise ValueError("HASH_MISMATCH")
    zip_summary = _validate_zip(merged)
    scan = _defender_scan(merged)
    if not scan.get("clean"):
        raise PermissionError("DEFENDER_REJECTED")

    now = datetime.now(timezone.utc)
    final = DIAGNOSTICS_ROOT / f"{now:%Y}" / f"{now:%m}" / f"{now:%d}" / report_id
    final.mkdir(parents=True, exist_ok=False)
    public_metadata = {
        key: value for key, value in metadata.items()
        if key not in {"token_hash", "client_ip_hash", "completed"}
    }
    public_metadata.update({"stored_at": now.isoformat(), "zip": zip_summary})
    _atomic_write(final / "metadata.json", json.dumps(public_metadata, ensure_ascii=False, indent=2).encode("utf-8"))
    _atomic_write(final / "user-note.txt", user_note.encode("utf-8"))
    _atomic_write(final / "scan-result.json", json.dumps(scan, indent=2).encode("utf-8"))
    os.replace(merged, final / "report.zip")
    metadata["completed"] = True
    _atomic_write(directory / "transaction.json", json.dumps(metadata, sort_keys=True).encode("utf-8"))
    shutil.rmtree(directory, ignore_errors=True)
    return _response(200, {"ok": True, "report_id": report_id})


def _event(request: Dict[str, Any]) -> Dict[str, Any]:
    body = _json_body(request)
    unknown = set(body) - _EVENT_FIELDS
    if unknown:
        raise ValueError("EVENT_FIELD_NOT_ALLOWED")
    for key in body:
        lowered = key.lower()
        if any(fragment in lowered for fragment in _SENSITIVE_FIELD_FRAGMENTS):
            raise ValueError("EVENT_SENSITIVE_FIELD")
    installation_hash = str(body.get("installation_hash") or "").lower()
    if not _INSTALL_HASH_RE.fullmatch(installation_hash):
        raise ValueError("INVALID_INSTALLATION_HASH")
    _rate_limit("event-ip:" + _client_ip(request), 240)
    _rate_limit("event-install:" + installation_hash, 120)
    now = datetime.now(timezone.utc)
    payload = dict(body)
    payload["received_at"] = now.isoformat()
    path = EVENT_ROOT / f"{now:%Y}" / f"{now:%m}" / f"{now:%d}.jsonl"
    path.parent.mkdir(parents=True, exist_ok=True)
    line = json.dumps(payload, ensure_ascii=False, separators=(",", ":")) + "\n"
    with path.open("a", encoding="utf-8", newline="\n") as output:
        output.write(line)
    return _response(202, {"ok": True})


def handle(request: Dict[str, Any]) -> Dict[str, Any]:
    if str(request.get("method") or "").upper() != "POST":
        return _response(405, {"ok": False, "code": "METHOD_NOT_ALLOWED"})
    _periodic_cleanup()
    path = str(request.get("path") or "").rstrip("/")
    try:
        if path.endswith("/begin"):
            return _begin(request)
        if path.endswith("/chunk"):
            return _chunk(request)
        if path.endswith("/complete"):
            return _complete(request)
        if path.endswith("/event"):
            return _event(request)
        return _response(404, {"ok": False, "code": "NOT_FOUND"})
    except json.JSONDecodeError:
        return _response(400, {"ok": False, "code": "INVALID_JSON"})
    except ValueError as exc:
        return _response(400, {"ok": False, "code": str(exc)})
    except FileNotFoundError as exc:
        return _response(409, {"ok": False, "code": str(exc)})
    except FileExistsError as exc:
        return _response(409, {"ok": False, "code": str(exc)})
    except PermissionError as exc:
        code = str(exc)
        return _response(429 if code == "RATE_LIMITED" else 403, {"ok": False, "code": code})
    except zipfile.BadZipFile:
        return _response(400, {"ok": False, "code": "INVALID_ZIP"})
    except Exception:
        return _response(500, {"ok": False, "code": "DIAGNOSTICS_INTERNAL_ERROR"})
