#!/usr/bin/env python3
"""Validate a completed release payload without trusting its publication record."""
from __future__ import annotations

import argparse
import hashlib
import json
from pathlib import Path, PurePosixPath
import zipfile


def _sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as source:
        while chunk := source.read(1024 * 1024):
            digest.update(chunk)
    return digest.hexdigest()


def _safe_member(name: str) -> bool:
    normalized = name.replace("\\", "/")
    path = PurePosixPath(normalized)
    return bool(normalized) and not path.is_absolute() and ".." not in path.parts


def validate(payload_root: Path, deep: bool) -> dict:
    package_root = payload_root / "packages"
    manifest_path = package_root / "release-manifest.unsigned.json"
    record_path = payload_root / "publication-record.json"
    manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
    record = json.loads(record_path.read_text(encoding="utf-8"))
    if manifest["productVersion"] != "1.0.0" or manifest["manifestSignature"]:
        raise ValueError("Expected an unsigned v1.0.0 manifest")
    components = manifest["components"]
    if len(components) < 5 or len({item["id"] for item in components}) != len(components):
        raise ValueError("Release component inventory is incomplete or duplicated")
    required_bytes = sum(int(item["size"]) for item in components if item["required"])
    if required_bytes != int(record["requiredDownloadBytes"]):
        raise ValueError("Required download byte total does not match")
    record_components = {item["id"]: item for item in record["components"]}
    validated_files = 0
    for component in components:
        published = record_components.get(component["id"])
        if published is None:
            raise ValueError(f"Publication record omits {component['id']}")
        archive = package_root / published["file"]
        if archive.stat().st_size != int(component["size"]) or _sha256(archive) != component["sha256"]:
            raise ValueError(f"Archive size/hash mismatch: {component['id']}")
        expected = {item["path"]: item for item in component["files"]}
        if len(expected) != len(component["files"]) or not expected:
            raise ValueError(f"Duplicate or empty file inventory: {component['id']}")
        with zipfile.ZipFile(archive) as package:
            members = {item.filename.replace("\\", "/"): item for item in package.infolist()
                       if not item.is_dir()}
            if any(not _safe_member(name) for name in members):
                raise ValueError(f"Unsafe archive path: {component['id']}")
            if set(members) != set(expected):
                raise ValueError(f"Archive file inventory mismatch: {component['id']}")
            for name, metadata in expected.items():
                member = members[name]
                if member.file_size != int(metadata["size"]):
                    raise ValueError(f"Installed file size mismatch: {component['id']}:{name}")
                if deep:
                    digest = hashlib.sha256()
                    with package.open(member) as source:
                        while chunk := source.read(1024 * 1024):
                            digest.update(chunk)
                    if digest.hexdigest() != metadata["sha256"]:
                        raise ValueError(f"Installed file hash mismatch: {component['id']}:{name}")
                validated_files += 1
    setup = package_root / record["setup"]["file"]
    if setup.stat().st_size != int(record["setup"]["size"]) or _sha256(setup) != record["setup"]["sha256"]:
        raise ValueError("Online installer size/hash mismatch")
    for required in ("MLCCS-VideoSearch-1.0.0.cdx.json", "THIRD-PARTY-LICENSES.csv"):
        if not (package_root / required).is_file():
            raise FileNotFoundError(f"Release metadata is missing: {required}")
    return {"version": "1.0.0", "componentCount": len(components),
            "fileCount": validated_files, "requiredDownloadBytes": required_bytes,
            "manifestSha256": _sha256(manifest_path), "deepFileHashes": deep}


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("payload_root", type=Path)
    parser.add_argument("--quick", action="store_true", help="Skip decompressed per-file hashes")
    parser.add_argument("--output", type=Path)
    args = parser.parse_args()
    result = validate(args.payload_root, not args.quick)
    rendered = json.dumps(result, ensure_ascii=False, indent=2) + "\n"
    print(rendered, end="")
    if args.output:
        args.output.write_text(rendered, encoding="utf-8")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
