#!/usr/bin/env python3
"""Replace only release notes in an unsigned release manifest."""
from __future__ import annotations

import argparse
import json
import os
from pathlib import Path


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--manifest", type=Path, required=True)
    parser.add_argument("--release-notes", type=Path, required=True)
    args = parser.parse_args()

    manifest = json.loads(args.manifest.read_text(encoding="utf-8-sig"))
    if manifest.get("productVersion") != "1.0.0" or manifest.get("manifestSignature"):
        raise ValueError("Expected an unsigned v1.0.0 manifest")
    manifest["releaseNotes"] = args.release_notes.read_text(encoding="utf-8-sig")

    temporary = args.manifest.with_suffix(args.manifest.suffix + ".tmp")
    temporary.write_text(
        json.dumps(manifest, ensure_ascii=False, separators=(",", ":")) + "\n",
        encoding="utf-8",
    )
    os.replace(temporary, args.manifest)
    print(f"Updated release notes in {args.manifest}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
