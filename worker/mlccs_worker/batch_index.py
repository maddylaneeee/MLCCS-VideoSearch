from __future__ import annotations

import argparse
from datetime import UTC, datetime
import hashlib
import json
import os
from pathlib import Path
import shutil
import sqlite3
import time
from collections.abc import Iterable, Iterator
from typing import Any
from uuid import NAMESPACE_URL, uuid4, uuid5

from .capabilities import detect, require_v1_hardware
from .vector_store import QdrantServer


MEDIA_EXTENSIONS = {".mp4", ".mkv", ".avi", ".mov", ".wmv", ".m4v", ".ts", ".webm"}
CLIP_MODEL_NAME = "xlm-roberta-base-ViT-B-32"
CLIP_VERSION = "openclip-standard-506d40eb"
BGE_VERSION = "bge-small-7999e1d3"
SCENE_ALGORITHM_VERSION = 1
SCENE_FPS = 2.0
SCENE_THRESHOLD = 27.0
MIN_SCENE_SECONDS = 2.0
MAX_SCENE_SECONDS = 8.0


def _utc() -> str:
    return datetime.now(UTC).isoformat()


def _stable_id(kind: str, value: str) -> str:
    return str(uuid5(NAMESPACE_URL, f"mlccs-video-search:{kind}:{value}"))


def _atomic_json(path: Path, value: dict[str, Any]) -> None:
    temporary = path.with_suffix(path.suffix + ".tmp")
    temporary.write_text(json.dumps(value, ensure_ascii=False, indent=2), encoding="utf-8")
    for attempt in range(30):
        try:
            os.replace(temporary, path)
            return
        except PermissionError:
            if attempt == 29:
                raise
            time.sleep(0.1)


def _database(path: Path) -> sqlite3.Connection:
    connection = sqlite3.connect(path)
    connection.execute("PRAGMA foreign_keys=ON")
    connection.execute("PRAGMA journal_mode=WAL")
    connection.execute("PRAGMA synchronous=FULL")
    connection.executescript("""
        CREATE TABLE IF NOT EXISTS real_index_runs(
          id TEXT PRIMARY KEY, library_root TEXT NOT NULL, started_utc TEXT NOT NULL,
          completed_utc TEXT, status TEXT NOT NULL, device TEXT NOT NULL,
          model_version TEXT NOT NULL, files_total INTEGER NOT NULL,
          files_completed INTEGER NOT NULL DEFAULT 0, segments_indexed INTEGER NOT NULL DEFAULT 0,
          error TEXT
        );
        CREATE TABLE IF NOT EXISTS libraries(
          id TEXT PRIMARY KEY, name TEXT NOT NULL, canonical_root TEXT NOT NULL UNIQUE,
          is_online INTEGER NOT NULL DEFAULT 1, created_utc TEXT NOT NULL
        );
        CREATE TABLE IF NOT EXISTS assets(
          id TEXT PRIMARY KEY, library_id TEXT NOT NULL REFERENCES libraries(id), canonical_path TEXT NOT NULL,
          size_bytes INTEGER NOT NULL, modified_utc TEXT NOT NULL, fast_fingerprint TEXT NOT NULL,
          full_sha256 TEXT, media_kind TEXT NOT NULL, duration_ms INTEGER, width INTEGER, height INTEGER,
          fps REAL, codec TEXT, status TEXT NOT NULL, error_code TEXT, UNIQUE(library_id,canonical_path)
        );
        CREATE TABLE IF NOT EXISTS media_assets(
          media_path TEXT PRIMARY KEY, asset_id TEXT NOT NULL UNIQUE, library_root TEXT NOT NULL,
          name TEXT NOT NULL, extension TEXT NOT NULL, size_bytes INTEGER NOT NULL,
          modified_utc TEXT NOT NULL, duration_ms INTEGER NOT NULL, width INTEGER, height INTEGER,
          codec TEXT, status TEXT NOT NULL, thumbnail_path TEXT, error TEXT, last_indexed_utc TEXT,
          visual_version TEXT, speech_version TEXT, ocr_version TEXT
        );
        CREATE INDEX IF NOT EXISTS ix_media_assets_library ON media_assets(library_root,name);
        CREATE TABLE IF NOT EXISTS visual_segments(
          id TEXT PRIMARY KEY, asset_id TEXT NOT NULL REFERENCES assets(id) ON DELETE CASCADE,
          start_ms INTEGER NOT NULL, end_ms INTEGER NOT NULL, representative_json TEXT NOT NULL,
          thumbnail_path TEXT NOT NULL, algorithm_version INTEGER NOT NULL, model_version TEXT NOT NULL,
          UNIQUE(asset_id,start_ms,end_ms,algorithm_version,model_version)
        );
        CREATE TABLE IF NOT EXISTS transcript_segments(
          id TEXT PRIMARY KEY, asset_id TEXT NOT NULL REFERENCES assets(id) ON DELETE CASCADE,
          start_ms INTEGER NOT NULL, end_ms INTEGER NOT NULL, original_text TEXT NOT NULL,
          normalized_text TEXT NOT NULL, words_json TEXT NOT NULL, model_version TEXT NOT NULL
        );
        CREATE TABLE IF NOT EXISTS speech_windows(
          id TEXT PRIMARY KEY, asset_id TEXT NOT NULL REFERENCES assets(id) ON DELETE CASCADE,
          start_ms INTEGER NOT NULL, end_ms INTEGER NOT NULL, original_segment_ids_json TEXT NOT NULL,
          normalized_text TEXT NOT NULL, algorithm_version INTEGER NOT NULL, model_version TEXT NOT NULL
        );
        CREATE TABLE IF NOT EXISTS ocr_observations(
          id TEXT PRIMARY KEY, asset_id TEXT NOT NULL REFERENCES assets(id) ON DELETE CASCADE,
          timestamp_ms INTEGER NOT NULL, original_text TEXT NOT NULL, normalized_text TEXT NOT NULL,
          language TEXT, confidence REAL NOT NULL, boxes_json TEXT NOT NULL,
          algorithm_version INTEGER NOT NULL, model_version TEXT NOT NULL
        );
        CREATE TABLE IF NOT EXISTS vector_outbox(
          id INTEGER PRIMARY KEY AUTOINCREMENT, operation TEXT NOT NULL, collection TEXT NOT NULL,
          point_id TEXT NOT NULL, payload_json TEXT NOT NULL, created_utc TEXT NOT NULL,
          completed_utc TEXT, UNIQUE(operation,collection,point_id)
        );
        CREATE VIRTUAL TABLE IF NOT EXISTS search_fts USING fts5(
          asset_id UNINDEXED, filename, transcript, ocr, pinyin, initials, fuzzy,
          tokenize='unicode61 remove_diacritics 2'
        );
    """)
    connection.commit()
    return connection


def _media_info(path: Path) -> tuple[float, int | None, int | None, float | None, str | None]:
    import av
    with av.open(str(path), metadata_errors="ignore") as container:
        stream = next((item for item in container.streams if item.type == "video"), None)
        if stream is None:
            raise RuntimeError("不包含视频流")
        duration = float(stream.duration * stream.time_base) if stream.duration is not None else float(container.duration or 0) / 1_000_000
        fps = float(stream.average_rate) if stream.average_rate else None
        codec = getattr(getattr(stream, "codec_context", None), "name", None)
        return duration, int(stream.width or 0) or None, int(stream.height or 0) or None, fps, codec


def _compact_rgb(rgb: Any) -> Any:
    """Bound retained frame memory while preserving aspect ratio for CLIP crops."""
    import cv2

    height, width = rgb.shape[:2]
    scale = min(1.0, 320.0 / max(1, min(height, width)))
    if scale == 1.0:
        return rgb
    return cv2.resize(rgb, (max(1, round(width * scale)), max(1, round(height * scale))),
                      interpolation=cv2.INTER_AREA)


def _window_from_samples(samples: list[tuple[int, Any, float]], end_ms: int
                         ) -> tuple[int, int, list[tuple[int, Any, float]]]:
    strongest = max(range(len(samples)), key=lambda item: samples[item][2])
    chosen = {len(samples) // 2, strongest}
    if max(item[2] for item in samples) >= SCENE_THRESHOLD * 1.5:
        chosen.add(max(0, strongest - 1))
        chosen.add(min(len(samples) - 1, strongest + 1))
    return samples[0][0], end_ms, [samples[item] for item in sorted(chosen)]


def _iter_scene_windows(samples: Iterable[tuple[int, Any, float]], duration_ms: int
                        ) -> Iterator[tuple[int, int, list[tuple[int, Any, float]]]]:
    """Split a sample stream without retaining frames from the whole video."""
    current: list[tuple[int, Any, float]] = []
    for sample in samples:
        timestamp_ms, _, motion = sample
        if current:
            elapsed = (timestamp_ms - current[0][0]) / 1000
            remaining = (duration_ms - timestamp_ms) / 1000
            if remaining >= MIN_SCENE_SECONDS and (elapsed >= MAX_SCENE_SECONDS or
                                                    (elapsed >= MIN_SCENE_SECONDS and
                                                     motion >= SCENE_THRESHOLD)):
                yield _window_from_samples(current, timestamp_ms)
                current = []
        current.append(sample)
    if current:
        end_ms = max(current[-1][0], duration_ms)
        yield _window_from_samples(current, end_ms)


def _scene_windows(path: Path, duration: float) -> Iterator[tuple[int, int, list[tuple[int, Any, float]]]]:
    """Analyze at 2 FPS and stream bounded 2-8 second representative windows."""
    import av
    import cv2
    import numpy as np

    def decoded_samples() -> Iterator[tuple[int, Any, float]]:
        previous = None
        target = 0.0
        with av.open(str(path), metadata_errors="ignore") as container:
            stream = next((item for item in container.streams if item.type == "video"), None)
            if stream is None:
                raise RuntimeError("不包含视频流")
            stream.thread_type = "AUTO"
            for frame in container.decode(stream):
                timestamp = float((frame.pts or 0) * frame.time_base)
                if timestamp + 0.001 < target:
                    continue
                rgb = frame.to_ndarray(format="rgb24")
                analysis = cv2.resize(rgb, (160, 90), interpolation=cv2.INTER_AREA)
                gray = cv2.cvtColor(analysis, cv2.COLOR_RGB2GRAY)
                motion = 0.0 if previous is None else float(np.mean(cv2.absdiff(gray, previous)))
                yield round(timestamp * 1000), _compact_rgb(rgb), motion
                previous = gray
                while target <= timestamp:
                    target += 1.0 / SCENE_FPS

    yield from _iter_scene_windows(decoded_samples(), round(duration * 1000))


def _effective_batch_size(requested: int, resource_policy: str, vram_bytes: int) -> int:
    safe_cap = 8 if vram_bytes < 6 * 1024**3 else 16 if vram_bytes < 10 * 1024**3 else 32
    policy_cap = {"efficiency": 4, "balanced": 8, "adaptive-full": safe_cap}.get(
        resource_policy, 8)
    return min(requested if requested > 0 else policy_cap, safe_cap)


def _segment_sample_ranges(samples: list[tuple[int, float]]) -> list[tuple[int, int]]:
    if not samples:
        return []
    boundaries = [0]
    final_timestamp = samples[-1][0]
    for index in range(1, len(samples)):
        elapsed = (samples[index][0] - samples[boundaries[-1]][0]) / 1000
        remaining = (final_timestamp - samples[index][0]) / 1000
        if remaining >= MIN_SCENE_SECONDS and (elapsed >= MAX_SCENE_SECONDS or
                                                (elapsed >= MIN_SCENE_SECONDS and samples[index][1] >= SCENE_THRESHOLD)):
            boundaries.append(index)
    boundaries.append(len(samples))
    return list(zip(boundaries, boundaries[1:]))


def _clip_tensor(rgb: Any) -> Any:
    import cv2
    import numpy as np
    import torch
    height, width = rgb.shape[:2]
    scale = 224.0 / max(1, min(height, width))
    resized = cv2.resize(rgb, (max(224, round(width * scale)), max(224, round(height * scale))), interpolation=cv2.INTER_AREA)
    top = (resized.shape[0] - 224) // 2
    left = (resized.shape[1] - 224) // 2
    crop = resized[top:top + 224, left:left + 224].astype(np.float32) / 255.0
    crop = (crop - np.asarray((0.48145466, 0.4578275, 0.40821073), dtype=np.float32)) / np.asarray((0.26862954, 0.26130258, 0.27577711), dtype=np.float32)
    return torch.from_numpy(np.ascontiguousarray(crop)).permute(2, 0, 1)


def _phonetic_fields(text: str) -> tuple[str, str]:
    try:
        from pypinyin import Style, lazy_pinyin
        pinyin = " ".join(lazy_pinyin(text)).casefold()
        initials = "".join(lazy_pinyin(text, style=Style.FIRST_LETTER)).casefold()
        return pinyin, initials
    except ImportError:
        return text.casefold(), text.casefold()


def _queue_vector(connection: sqlite3.Connection, collection: str, point_id: str,
                  vector: Any, payload: dict[str, Any]) -> None:
    document = {**payload, "vector": [float(value) for value in vector]}
    connection.execute(
        """INSERT INTO vector_outbox(operation,collection,point_id,payload_json,created_utc,completed_utc)
           VALUES('upsert',?,?,?,?,NULL)
           ON CONFLICT(operation,collection,point_id) DO UPDATE SET
             payload_json=excluded.payload_json,created_utc=excluded.created_utc,completed_utc=NULL""",
        (collection, point_id, json.dumps(document, ensure_ascii=False, separators=(",", ":")), _utc()),
    )


def _refresh_fts(connection: sqlite3.Connection, asset_id: str, filename: str) -> None:
    transcripts = " ".join(row[0] for row in connection.execute(
        "SELECT original_text FROM transcript_segments WHERE asset_id=? ORDER BY start_ms", (asset_id,)))
    ocr = " ".join(row[0] for row in connection.execute(
        "SELECT original_text FROM ocr_observations WHERE asset_id=? ORDER BY timestamp_ms", (asset_id,)))
    pinyin, initials = _phonetic_fields(" ".join((filename, transcripts, ocr)))
    connection.execute("DELETE FROM search_fts WHERE asset_id=?", (asset_id,))
    connection.execute("INSERT INTO search_fts VALUES(?,?,?,?,?,?,?)",
                       (asset_id, filename, transcripts, ocr, pinyin, initials, ""))


def _record_file_failure(connection: sqlite3.Connection, library_id: str, library_root: str,
                         path: Path, asset_id: str, error: Exception) -> None:
    """Persist a discoverable file-level failure even when media probing never completed."""
    # Discard partially generated segments/outbox rows for this file before recording the
    # durable failure. Earlier files and the run/library rows have already been committed.
    connection.rollback()
    try:
        stat = path.stat()
        size = stat.st_size
        modified = datetime.fromtimestamp(stat.st_mtime, UTC).isoformat()
        fingerprint = hashlib.sha256(f"{stat.st_size}|{stat.st_mtime_ns}|{path.name}".encode()).hexdigest()
    except OSError:
        size = 0
        modified = _utc()
        fingerprint = hashlib.sha256(str(path).encode()).hexdigest()
    message = str(error)[:500]
    connection.execute("""INSERT INTO assets VALUES(?,?,?,?,?,?,?,?,?,?,?,?,?,?,?)
        ON CONFLICT(id) DO UPDATE SET size_bytes=excluded.size_bytes,modified_utc=excluded.modified_utc,
        fast_fingerprint=excluded.fast_fingerprint,status='Failed',error_code='MEDIA_DECODE_FAILED'""",
        (asset_id, library_id, str(path), size, modified, fingerprint, None, "video",
         None, None, None, None, None, "Failed", "MEDIA_DECODE_FAILED"))
    connection.execute("""INSERT INTO media_assets VALUES(?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?)
        ON CONFLICT(media_path) DO UPDATE SET asset_id=excluded.asset_id,library_root=excluded.library_root,
        name=excluded.name,extension=excluded.extension,size_bytes=excluded.size_bytes,
        modified_utc=excluded.modified_utc,status='Failed',error=excluded.error""",
        (str(path), asset_id, library_root, path.name, path.suffix.casefold(), size, modified,
         0, None, None, None, "Failed", None, message, _utc(), None, None, None))
    connection.execute("DELETE FROM search_fts WHERE asset_id=?", (asset_id,))
    connection.commit()


def _is_visual_current(connection: sqlite3.Connection, asset_id: str, fingerprint: str) -> bool:
    return connection.execute(
        """SELECT 1 FROM assets AS a JOIN media_assets AS m ON m.asset_id=a.id
           WHERE a.id=? AND a.fast_fingerprint=? AND a.status='Indexed'
             AND m.status='Indexed' AND m.visual_version=? LIMIT 1""",
        (asset_id, fingerprint, CLIP_VERSION),
    ).fetchone() is not None


def _speech_window_groups(rows: list[tuple[str, int, int, str]]) -> list[tuple[int, int, list[tuple[str, int, int, str]]]]:
    """Group transcript segments into deterministic 8–30 second semantic windows."""
    groups: list[list[tuple[str, int, int, str]]] = []
    current: list[tuple[str, int, int, str]] = []
    for row in rows:
        if current and row[2] - current[0][1] > 30_000:
            groups.append(current)
            current = []
        current.append(row)
        if current[-1][2] - current[0][1] >= 8_000:
            groups.append(current)
            current = []
    if current:
        if groups and current[-1][2] - groups[-1][0][1] <= 30_000:
            groups[-1].extend(current)
        else:
            groups.append(current)
    result = []
    for group in groups:
        start_ms = group[0][1]
        end_ms = min(start_ms + 30_000, max(start_ms + 8_000, group[-1][2]))
        result.append((start_ms, end_ms, group))
    return result


def _remove_missing_assets(connection: sqlite3.Connection, library_root: str,
                           current_paths: set[str], thumbnails: Path) -> int:
    missing = connection.execute(
        "SELECT asset_id,media_path FROM media_assets WHERE library_root=?", (library_root,)).fetchall()
    removed = 0
    for asset_id, media_path in missing:
        if media_path in current_paths:
            continue
        for table, collection in (("visual_segments", "visual_v1"),
                                  ("speech_windows", "speech_v1"),
                                  ("ocr_observations", "ocr_v1")):
            for point_id, in connection.execute(f"SELECT id FROM {table} WHERE asset_id=?", (asset_id,)):
                connection.execute("""INSERT INTO vector_outbox(operation,collection,point_id,payload_json,created_utc)
                    VALUES('delete',?,?, '{}',?) ON CONFLICT(operation,collection,point_id)
                    DO UPDATE SET completed_utc=NULL,created_utc=excluded.created_utc""",
                                   (collection, point_id, _utc()))
        connection.execute("DELETE FROM search_fts WHERE asset_id=?", (asset_id,))
        connection.execute("DELETE FROM media_assets WHERE asset_id=?", (asset_id,))
        connection.execute("DELETE FROM assets WHERE id=?", (asset_id,))
        shutil.rmtree(thumbnails / asset_id, ignore_errors=True)
        removed += 1
    connection.commit()
    return removed


def run(library: Path, data_root: Path, models_root: Path, text_models_root: Path,
        interval_seconds: float, batch_size: int,
        decoder_workers: int, extra_models_root: Path, speech_enabled: bool, speech_model: str,
        ocr_models_root: Path, ocr_enabled: bool, resource_policy: str) -> None:
    del interval_seconds
    import cv2
    import numpy as np
    import open_clip
    import torch

    capabilities = detect(str(data_root))
    require_v1_hardware(capabilities)
    clip_checkpoint = models_root / "openclip-standard" / "open_clip_pytorch_model.bin"
    bge_root = text_models_root / "bge-small"
    if not clip_checkpoint.is_file() or not bge_root.is_dir():
        raise RuntimeError("必装的 OpenCLIP Standard 或 BGE Small 模型缺失")
    data_root.mkdir(parents=True, exist_ok=True)
    status_path = data_root / "real-index-status.json"
    pause_path = data_root / "real-index.pause"
    cancel_path = data_root / "real-index.cancel"
    files = sorted(path for path in library.rglob("*") if path.is_file() and path.suffix.casefold() in MEDIA_EXTENSIONS)
    run_id = str(uuid4())
    connection = _database(data_root / "catalog.db")
    qdrant = QdrantServer()
    qdrant.reconcile_outbox(connection)
    qdrant.replay_all(connection)
    library_id = _stable_id("library", str(library.resolve()).casefold())
    connection.execute("INSERT OR REPLACE INTO libraries VALUES(?,?,?,?,?)",
                       (library_id, library.name or str(library), str(library), 1, _utc()))
    thumbnails = data_root / "thumbnails"
    thumbnails.mkdir(parents=True, exist_ok=True)
    _remove_missing_assets(connection, str(library), {str(path) for path in files}, thumbnails)
    qdrant.replay_all(connection)
    connection.execute("INSERT INTO real_index_runs VALUES(?,?,?,?,?,?,?,?,?,?,?)",
                       (run_id, str(library), _utc(), None, "Running", "cuda:0", CLIP_VERSION,
                        len(files), 0, 0, None))
    connection.commit()
    completed = 0
    skipped = 0
    segments_indexed = 0
    failures: list[str] = []
    effective_batch = _effective_batch_size(batch_size, resource_policy, capabilities.vram_bytes)
    effective_decoders = 1  # PyAV currently uses one bounded decode stream per index process.

    def status(stage: str, current: Path | None = None, error: str | None = None) -> None:
        _atomic_json(status_path, {
            "truthful": True, "runId": run_id, "status": stage, "library": str(library),
            "currentFile": str(current) if current else None, "filesTotal": len(files),
            "filesCompleted": completed, "filesFailed": len(failures), "segmentsIndexed": segments_indexed,
            "filesSkipped": skipped,
            "progress": (completed + len(failures)) / max(1, len(files)), "samplingMode": "scene-2fps",
            "sceneThreshold": SCENE_THRESHOLD, "sceneMinimumSeconds": MIN_SCENE_SECONDS,
            "sceneMaximumSeconds": MAX_SCENE_SECONDS, "device": "cuda:0",
            "gpu": capabilities.gpu_name or "NVIDIA GPU", "cuda": capabilities.cuda_version,
            "cpuThreads": capabilities.logical_processors, "decoderWorkers": effective_decoders,
            "batchSize": effective_batch,
            "model": CLIP_MODEL_NAME, "modelVersion": CLIP_VERSION, "error": error, "updatedUtc": _utc(),
        })

    def wait_if_paused() -> None:
        while pause_path.exists() and not cancel_path.exists():
            time.sleep(0.25)

    model = None
    try:
        for path in files:
            if cancel_path.exists():
                raise KeyboardInterrupt
            wait_if_paused()
            status("Visual", path)
            asset_id = _stable_id("asset", str(path.resolve()).casefold())
            try:
                stat = path.stat()
                modified = datetime.fromtimestamp(stat.st_mtime, UTC).isoformat()
                fingerprint = hashlib.sha256(f"{stat.st_size}|{stat.st_mtime_ns}|{path.name}".encode()).hexdigest()
                if _is_visual_current(connection, asset_id, fingerprint):
                    completed += 1
                    skipped += 1
                    status("Visual", path)
                    continue
                if model is None:
                    status("LoadingVisualModel", path)
                    model, _, _ = open_clip.create_model_and_transforms(
                        CLIP_MODEL_NAME, pretrained=str(clip_checkpoint), device="cuda:0")
                    model.half().eval()
                duration, width, height, fps, codec = _media_info(path)
                connection.execute("""INSERT INTO assets VALUES(?,?,?,?,?,?,?,?,?,?,?,?,?,?,?)
                    ON CONFLICT(id) DO UPDATE SET size_bytes=excluded.size_bytes,modified_utc=excluded.modified_utc,
                    fast_fingerprint=excluded.fast_fingerprint,duration_ms=excluded.duration_ms,width=excluded.width,
                    height=excluded.height,fps=excluded.fps,codec=excluded.codec,status='Indexing',error_code=NULL""",
                    (asset_id, library_id, str(path), stat.st_size, modified, fingerprint, None, "video",
                     round(duration * 1000), width, height, fps, codec, "Indexing", None))
                connection.execute("""INSERT INTO media_assets VALUES(?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?)
                    ON CONFLICT(media_path) DO UPDATE SET asset_id=excluded.asset_id,library_root=excluded.library_root,
                    name=excluded.name,extension=excluded.extension,size_bytes=excluded.size_bytes,
                    modified_utc=excluded.modified_utc,duration_ms=excluded.duration_ms,width=excluded.width,
                    height=excluded.height,codec=excluded.codec,status='Indexing',error=NULL""",
                    (str(path), asset_id, str(library), path.name, path.suffix.casefold(), stat.st_size, modified,
                     round(duration * 1000), width, height, codec, "Indexing", None, None, None, None, None, None))
                old_points = [row[0] for row in connection.execute(
                    "SELECT id FROM visual_segments WHERE asset_id=?", (asset_id,))]
                connection.execute("DELETE FROM visual_segments WHERE asset_id=?", (asset_id,))
                new_points: set[str] = set()
                for start_ms, end_ms, representatives in _scene_windows(path, duration):
                    tensors = torch.stack([_clip_tensor(rgb) for _, rgb, _ in representatives])
                    chunks = []
                    for offset in range(0, len(tensors), effective_batch):
                        with torch.inference_mode(), torch.autocast("cuda", dtype=torch.float16):
                            chunks.append(model.encode_image(tensors[offset:offset + effective_batch].pin_memory().to("cuda:0"), normalize=True).float().cpu().numpy())
                    vector = np.concatenate(chunks).mean(axis=0)
                    vector /= max(1e-12, np.linalg.norm(vector))
                    representative_ms = representatives[max(range(len(representatives)), key=lambda i: representatives[i][2])][0]
                    segment_id = _stable_id("visual", f"{asset_id}|{start_ms}|{end_ms}|{CLIP_VERSION}")
                    new_points.add(segment_id)
                    thumb_dir = thumbnails / asset_id
                    thumb_dir.mkdir(parents=True, exist_ok=True)
                    thumb = thumb_dir / f"{segment_id}.jpg"
                    selected_rgb = next(rgb for timestamp, rgb, _ in representatives if timestamp == representative_ms)
                    cv2.imwrite(str(thumb), cv2.cvtColor(cv2.resize(selected_rgb, (320, 180)), cv2.COLOR_RGB2BGR), [cv2.IMWRITE_JPEG_QUALITY, 82])
                    reps_json = json.dumps([{"timestampMs": ts, "motion": round(motion, 3)} for ts, _, motion in representatives], separators=(",", ":"))
                    connection.execute("INSERT INTO visual_segments VALUES(?,?,?,?,?,?,?,?)",
                                       (segment_id, asset_id, start_ms, end_ms, reps_json, str(thumb), SCENE_ALGORITHM_VERSION, CLIP_VERSION))
                    _queue_vector(connection, "visual_v1", segment_id, vector, {
                        "assetId": asset_id, "segmentId": segment_id, "startMs": start_ms, "endMs": end_ms,
                        "representativeMs": representative_ms, "algorithmVersion": SCENE_ALGORITHM_VERSION,
                        "modelVersion": CLIP_VERSION,
                    })
                    segments_indexed += 1
                for point_id in set(old_points) - new_points:
                    connection.execute("""INSERT INTO vector_outbox(operation,collection,point_id,payload_json,created_utc)
                        VALUES('delete','visual_v1',?,'{}',?) ON CONFLICT(operation,collection,point_id)
                        DO UPDATE SET completed_utc=NULL,created_utc=excluded.created_utc""", (point_id, _utc()))
                thumbnail = connection.execute("SELECT thumbnail_path FROM visual_segments WHERE asset_id=? ORDER BY start_ms LIMIT 1", (asset_id,)).fetchone()
                connection.execute("UPDATE assets SET status='Indexed',error_code=NULL WHERE id=?", (asset_id,))
                connection.execute("""UPDATE media_assets SET status='Indexed',thumbnail_path=?,error=NULL,
                    last_indexed_utc=?,visual_version=? WHERE asset_id=?""",
                    (thumbnail[0] if thumbnail else None, _utc(), CLIP_VERSION, asset_id))
                _refresh_fts(connection, asset_id, path.name)
                connection.commit()
            except Exception as error:
                failures.append(f"{path.name}: {error}")
                _record_file_failure(connection, library_id, str(library), path, asset_id, error)
                continue
            # A Qdrant outage is an infrastructure failure, not a corrupt-media failure.
            # SQLite and its Outbox are already durable, so the outer job failure can be
            # resumed deterministically without mislabelling the source video.
            qdrant.replay_all(connection)
            completed += 1
        if model is not None:
            del model
            torch.cuda.empty_cache()

        if speech_enabled:
            status("LoadingSpeechModel")
            from faster_whisper import WhisperModel
            whisper_root = extra_models_root / speech_model
            if not whisper_root.is_dir():
                raise RuntimeError("所选 Whisper 模型尚未安装")
            whisper = WhisperModel(str(whisper_root), device="cuda", compute_type="int8_float16", local_files_only=True)
            for path in files:
                asset_row = connection.execute("SELECT asset_id,status FROM media_assets WHERE media_path=?", (str(path),)).fetchone()
                if not asset_row or asset_row[1] == "Failed":
                    continue
                asset_id = asset_row[0]
                connection.execute("SAVEPOINT speech_file")
                try:
                    connection.execute("DELETE FROM transcript_segments WHERE asset_id=?", (asset_id,))
                    segments, _ = whisper.transcribe(str(path), vad_filter=True, word_timestamps=True, beam_size=5)
                    for segment in segments:
                        text = segment.text.strip()
                        if not text:
                            continue
                        start_ms, end_ms = round(segment.start * 1000), round(segment.end * 1000)
                        segment_id = _stable_id("speech-segment", f"{asset_id}|{start_ms}|{end_ms}|{speech_model}")
                        words = [{"text": word.word, "startMs": round(word.start * 1000), "endMs": round(word.end * 1000)} for word in (segment.words or [])]
                        connection.execute("INSERT INTO transcript_segments VALUES(?,?,?,?,?,?,?,?)",
                                           (segment_id, asset_id, start_ms, end_ms, text, text.casefold(), json.dumps(words, ensure_ascii=False), speech_model))
                    connection.execute("UPDATE media_assets SET speech_version=? WHERE asset_id=?", (speech_model, asset_id))
                    connection.execute("RELEASE speech_file")
                    connection.commit()
                except Exception as error:
                    connection.execute("ROLLBACK TO speech_file")
                    connection.execute("RELEASE speech_file")
                    failures.append(f"{path.name} [speech]: {error}")
                    connection.execute("UPDATE media_assets SET error=? WHERE asset_id=?",
                                       (f"SPEECH: {str(error)[:480]}", asset_id))
                    connection.commit()
            del whisper
            torch.cuda.empty_cache()

        if ocr_enabled:
            status("LoadingOcrModel")
            from paddleocr import PaddleOCR
            detection_root = ocr_models_root / "ppocrv5-mobile-det"
            recognition_root = ocr_models_root / "ppocrv5-mobile-rec"
            if not detection_root.is_dir() or not recognition_root.is_dir():
                raise RuntimeError("选装 OCR 模型尚未安装")
            ocr_engine = PaddleOCR(text_detection_model_dir=str(detection_root), text_recognition_model_dir=str(recognition_root),
                                   use_doc_orientation_classify=False, use_doc_unwarping=False, use_textline_orientation=False, lang="ch")
            for asset_id, in connection.execute("SELECT id FROM assets WHERE library_id=? AND status='Indexed'", (library_id,)):
                connection.execute("SAVEPOINT ocr_file")
                try:
                    connection.execute("DELETE FROM ocr_observations WHERE asset_id=?", (asset_id,))
                    frames = connection.execute("SELECT representative_json,thumbnail_path FROM visual_segments WHERE asset_id=?", (asset_id,)).fetchall()
                    for representatives, thumbnail in frames:
                        timestamp_ms = json.loads(representatives)[0]["timestampMs"]
                        predictions = list(ocr_engine.predict(str(thumbnail)))
                        for prediction in predictions:
                            value = prediction.json() if callable(prediction.json) else prediction.json
                            result = value.get("res", value)
                            for index, text in enumerate(result.get("rec_texts", [])):
                                normalized = str(text).strip()
                                if not normalized:
                                    continue
                                confidence = float(result.get("rec_scores", [0.0] * (index + 1))[index])
                                ocr_id = _stable_id("ocr", f"{asset_id}|{timestamp_ms}|{index}|{normalized}")
                                boxes = result.get("rec_boxes", [])
                                box = boxes[index].tolist() if index < len(boxes) and hasattr(boxes[index], "tolist") else []
                                connection.execute("INSERT INTO ocr_observations VALUES(?,?,?,?,?,?,?,?,?,?)",
                                                   (ocr_id, asset_id, timestamp_ms, normalized, normalized.casefold(), "und", confidence,
                                                    json.dumps(box), 1, "ppocrv5-mobile"))
                    connection.execute("UPDATE media_assets SET ocr_version='ppocrv5-mobile' WHERE asset_id=?", (asset_id,))
                    connection.execute("RELEASE ocr_file")
                    connection.commit()
                except Exception as error:
                    connection.execute("ROLLBACK TO ocr_file")
                    connection.execute("RELEASE ocr_file")
                    failures.append(f"{asset_id} [ocr]: {error}")
                    connection.execute("UPDATE media_assets SET error=? WHERE asset_id=?",
                                       (f"OCR: {str(error)[:483]}", asset_id))
                    connection.commit()
            del ocr_engine
            torch.cuda.empty_cache()

        status("LoadingTextModel")
        from sentence_transformers import SentenceTransformer
        bge = SentenceTransformer(str(bge_root), device="cuda")
        for asset_id, name in connection.execute("SELECT asset_id,name FROM media_assets WHERE status='Indexed'"):
            connection.execute("DELETE FROM speech_windows WHERE asset_id=?", (asset_id,))
            speech_rows = connection.execute("SELECT id,start_ms,end_ms,original_text FROM transcript_segments WHERE asset_id=? ORDER BY start_ms", (asset_id,)).fetchall()
            for start_ms, end_ms, group in _speech_window_groups(speech_rows):
                text = " ".join(item[3] for item in group)
                window_id = _stable_id("speech-window", f"{asset_id}|{start_ms}|{end_ms}|{BGE_VERSION}")
                connection.execute("INSERT INTO speech_windows VALUES(?,?,?,?,?,?,?,?)",
                                   (window_id, asset_id, start_ms, end_ms, json.dumps([item[0] for item in group]), text.casefold(), 1, BGE_VERSION))
                vector = bge.encode(text, normalize_embeddings=True)
                _queue_vector(connection, "speech_v1", window_id, vector, {"assetId": asset_id, "semanticWindowId": window_id,
                    "startMs": start_ms, "endMs": end_ms, "algorithmVersion": 1, "modelVersion": BGE_VERSION})
            for ocr_id, timestamp_ms, text, confidence in connection.execute(
                    "SELECT id,timestamp_ms,original_text,confidence FROM ocr_observations WHERE asset_id=?", (asset_id,)):
                vector = bge.encode(text, normalize_embeddings=True)
                _queue_vector(connection, "ocr_v1", ocr_id, vector, {"assetId": asset_id, "ocrId": ocr_id,
                    "timestampMs": timestamp_ms, "algorithmVersion": 1, "modelVersion": BGE_VERSION})
            _refresh_fts(connection, asset_id, name)
            connection.commit()
            qdrant.replay_all(connection)
        del bge
        torch.cuda.empty_cache()
        connection.execute("UPDATE real_index_runs SET status='Completed',completed_utc=?,files_completed=?,segments_indexed=?,error=? WHERE id=?",
                           (_utc(), completed, segments_indexed, "; ".join(failures[:10]) or None, run_id))
        connection.commit()
        status("Completed", error=(f"{len(failures)} 个视频文件失败" if failures else None))
    except KeyboardInterrupt:
        connection.execute("UPDATE real_index_runs SET status='Cancelled',completed_utc=? WHERE id=?", (_utc(), run_id))
        connection.commit()
        status("Cancelled")
    except Exception as error:
        connection.execute("UPDATE real_index_runs SET status='Failed',completed_utc=?,error=? WHERE id=?", (_utc(), str(error)[:500], run_id))
        connection.commit()
        status("Failed", error=str(error))
        raise
    finally:
        qdrant.close()
        connection.close()


def main() -> int:
    parser = argparse.ArgumentParser(description="MLCCS Video Search v1 production indexer")
    parser.add_argument("--library", type=Path, required=True)
    parser.add_argument("--data-root", type=Path, required=True)
    parser.add_argument("--models-root", type=Path, required=True)
    parser.add_argument("--text-models-root", type=Path, required=True)
    parser.add_argument("--interval-seconds", type=float, default=0.5)
    parser.add_argument("--batch-size", type=int, default=0)
    parser.add_argument("--decoder-workers", type=int, default=0)
    parser.add_argument("--extra-models-root", type=Path)
    parser.add_argument("--speech-enabled", action="store_true")
    parser.add_argument("--speech-model", default="whisper-medium")
    parser.add_argument("--ocr-models-root", type=Path)
    parser.add_argument("--ocr-enabled", action="store_true")
    parser.add_argument("--resource-policy", default="adaptive-full")
    args = parser.parse_args()
    run(args.library, args.data_root, args.models_root, args.text_models_root,
        args.interval_seconds, args.batch_size,
        args.decoder_workers, args.extra_models_root or args.models_root, args.speech_enabled,
        args.speech_model, args.ocr_models_root or args.extra_models_root or args.models_root,
        args.ocr_enabled, args.resource_policy)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
