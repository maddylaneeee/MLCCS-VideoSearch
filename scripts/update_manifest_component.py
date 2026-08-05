#!/usr/bin/env python3
"""Replace one component in an existing release manifest for a targeted rebuild."""

from __future__ import annotations

import argparse
from datetime import UTC, datetime
import hashlib
import json
import os
from pathlib import Path
import zipfile


def sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as source:
        while chunk := source.read(1024 * 1024):
            digest.update(chunk)
    return digest.hexdigest()


def inventory(stage: Path) -> list[dict[str, object]]:
    paths = sorted(path for path in stage.rglob("*") if path.is_file())
    if not paths:
        raise ValueError(f"Empty component stage: {stage}")
    return [
        {
            "path": path.relative_to(stage).as_posix(),
            "size": path.stat().st_size,
            "sha256": sha256(path),
        }
        for path in paths
    ]


def verify_archive(archive: Path, files: list[dict[str, object]]) -> None:
    expected = {str(item["path"]): int(item["size"]) for item in files}
    with zipfile.ZipFile(archive) as package:
        actual = {
            item.filename.rstrip("/"): item.file_size
            for item in package.infolist()
            if not item.is_dir()
        }
        bad_paths = [name for name in actual if name.startswith("/") or ".." in Path(name).parts]
        if bad_paths:
            raise ValueError(f"Unsafe archive path: {bad_paths[0]}")
    if actual != expected:
        missing = sorted(expected.keys() - actual.keys())
        extra = sorted(actual.keys() - expected.keys())
        raise ValueError(f"Archive/stage mismatch; missing={missing[:3]} extra={extra[:3]}")


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--base-manifest", type=Path, required=True)
    parser.add_argument("--component-id", required=True)
    parser.add_argument("--archive", type=Path, required=True)
    parser.add_argument("--stage", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()

    document = json.loads(args.base_manifest.read_text(encoding="utf-8-sig"))
    matches = [item for item in document["components"] if item["id"] == args.component_id]
    if len(matches) != 1:
        raise ValueError(f"Expected one component named {args.component_id}, found {len(matches)}")
    files = inventory(args.stage.resolve())
    verify_archive(args.archive.resolve(), files)
    component = matches[0]
    component["size"] = args.archive.stat().st_size
    component["sha256"] = sha256(args.archive)
    component["files"] = files
    document["publishedUtc"] = datetime.now(UTC).isoformat()
    document["manifestSignature"] = ""

    temporary = args.output.with_suffix(args.output.suffix + ".tmp")
    temporary.parent.mkdir(parents=True, exist_ok=True)
    temporary.write_text(
        json.dumps(document, ensure_ascii=False, separators=(",", ":")) + "\n",
        encoding="utf-8",
    )
    os.replace(temporary, args.output)
    print(f"Updated {args.component_id} with {len(files)} files at {args.output}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
