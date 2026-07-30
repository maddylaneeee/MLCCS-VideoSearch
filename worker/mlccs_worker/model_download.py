from __future__ import annotations

import argparse
from datetime import UTC, datetime
import json
import os
from pathlib import Path
import time
from typing import Any

from .downloader import DownloadManager, load_manifest


def _atomic(path: Path, value: dict[str, Any]) -> None:
    temporary = path.with_suffix(path.suffix + ".tmp")
    temporary.write_text(json.dumps(value, ensure_ascii=False, indent=2), encoding="utf-8")
    os.replace(temporary, path)


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--manifest", type=Path, required=True)
    parser.add_argument("--destination", type=Path, required=True)
    parser.add_argument("--status", type=Path, required=True)
    parser.add_argument("--cancel", type=Path, required=True)
    parser.add_argument("--prefix", action="append", default=[])
    args = parser.parse_args()
    artifacts = [item for item in load_manifest(args.manifest)
                 if any(item.id.startswith(prefix) for prefix in args.prefix)]
    if not artifacts:
        raise RuntimeError("没有找到匹配的锁定模型制品")
    total = sum(item.size for item in artifacts)
    completed = 0
    started = time.monotonic()
    manager = DownloadManager(args.destination)
    args.status.parent.mkdir(parents=True, exist_ok=True)
    args.cancel.unlink(missing_ok=True)
    try:
        for index, artifact in enumerate(artifacts, 1):
            def progress(done: int, item_total: int, rate: float, artifact=artifact, index=index) -> None:
                elapsed = max(0.001, time.monotonic() - started)
                overall = completed + done
                _atomic(args.status, {
                    "status": "Downloading", "currentId": artifact.id,
                    "currentIndex": index, "fileCount": len(artifacts),
                    "currentBytes": done, "currentTotalBytes": item_total,
                    "completedBytes": overall, "totalBytes": total,
                    "progress": overall / max(1, total), "bytesPerSecond": rate,
                    "etaSeconds": (total - overall) / max(1, overall / elapsed),
                    "updatedUtc": datetime.now(UTC).isoformat()
                })
            manager.download(artifact, progress, args.cancel.exists)
            completed += artifact.size
        _atomic(args.status, {
            "status": "Completed", "currentId": None, "currentIndex": len(artifacts),
            "fileCount": len(artifacts), "completedBytes": completed, "totalBytes": total,
            "progress": 1.0, "bytesPerSecond": 0, "etaSeconds": 0,
            "updatedUtc": datetime.now(UTC).isoformat()
        })
        return 0
    except Exception as error:
        _atomic(args.status, {
            "status": "Cancelled" if args.cancel.exists() else "Failed",
            "error": str(error), "completedBytes": completed, "totalBytes": total,
            "progress": completed / max(1, total), "updatedUtc": datetime.now(UTC).isoformat()
        })
        raise


if __name__ == "__main__":
    raise SystemExit(main())
