#!/usr/bin/env python3
"""Create the small publication record from a completed unsigned manifest and setup binary."""
from __future__ import annotations

import argparse
from datetime import UTC, datetime
import hashlib
import json
import os
from pathlib import Path
from urllib.parse import urlparse


def _sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as source:
        while chunk := source.read(1024 * 1024):
            digest.update(chunk)
    return digest.hexdigest()


def generate(manifest_path: Path, setup_path: Path, output_path: Path) -> dict:
    manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
    components = manifest["components"]
    record = {
        "version": manifest["productVersion"],
        "generatedUtc": datetime.now(UTC).isoformat(),
        "setup": {"file": setup_path.name, "size": setup_path.stat().st_size,
                  "sha256": _sha256(setup_path)},
        "unsignedManifest": manifest_path.name,
        "requiredDownloadBytes": sum(int(item["size"]) for item in components if item["required"]),
        "components": [{"id": item["id"], "file": Path(urlparse(item["url"]).path).name,
                        "size": int(item["size"]), "sha256": item["sha256"]}
                       for item in components],
    }
    temporary = output_path.with_suffix(output_path.suffix + ".tmp")
    temporary.write_text(json.dumps(record, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    os.replace(temporary, output_path)
    return record


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--manifest", type=Path, required=True)
    parser.add_argument("--setup", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    record = generate(args.manifest, args.setup, args.output)
    print(f"Publication record ready; required download bytes: {record['requiredDownloadBytes']}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
