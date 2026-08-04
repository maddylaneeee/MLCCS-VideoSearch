#!/usr/bin/env python3
from __future__ import annotations
from datetime import UTC, datetime
import hashlib, json
from pathlib import Path
import argparse

ROOT = Path(__file__).resolve().parents[1]
OUTPUT = ROOT / "handoff-manifest.json"
EXCLUDED = {"artifacts", "bin", "obj", ".git", ".vs", "__pycache__", ".pytest_cache"}

def sha(path: Path) -> str:
    digest=hashlib.sha256()
    with path.open("rb") as handle:
        for chunk in iter(lambda: handle.read(1024*1024), b""): digest.update(chunk)
    return digest.hexdigest()

def main() -> int:
    parser=argparse.ArgumentParser(); parser.add_argument("--payload",action="store_true"); args=parser.parse_args()
    files=[]
    for path in sorted(ROOT.rglob("*")):
        if not path.is_file() or path == OUTPUT or any(part in EXCLUDED for part in path.relative_to(ROOT).parts): continue
        if args.payload and path.name == "WINDOWS_HANDOFF.md": continue
        files.append({"path":path.relative_to(ROOT).as_posix(),"size":path.stat().st_size,"sha256":sha(path)})
    document={
      "schemaVersion":1,"handoffVersion":"0.1.0","createdUtc":datetime.now(UTC).isoformat(),
      "fileInventoryExcludes":["handoff-manifest.json (its hash is supplied in WINDOWS_HANDOFF.md)"] + (["WINDOWS_HANDOFF.md (supplied separately and included in the complete handoff archive)"] if args.payload else []),
      "files":files,
      "staticAcceptance":{"report":"STATIC_ACCEPTANCE.md","status":"passed-with-windows-only-items"},
      "websiteUpgrade":"website/videosearch-diagnostics-upgrade.zip","updateSample":"release/update-sample/",
      "windowsOnlyPending":["WinUI compile and runtime","named-pipe and tray lifecycle","private binary loading and CUDA","clean-machine first run","real-media index/search/player","30-minute load","72-hour endurance","UI/accessibility screenshots","Defender/production diagnostics","signed updater and stable hosting"],
      "forbiddenContent":["models","cache","keys","credentials","user media"]
    }
    OUTPUT.write_text(json.dumps(document,ensure_ascii=False,indent=2)+"\n",encoding="utf-8")
    print(f"manifested {len(files)} files")
    return 0
if __name__=="__main__": raise SystemExit(main())
