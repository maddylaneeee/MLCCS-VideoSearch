#!/usr/bin/env python3
from __future__ import annotations

import json
from pathlib import Path
import re
import sys


ROOT = Path(__file__).resolve().parents[1]
REQUIRED = {
    "READMEFIRST.md", "ARCHITECTURE.md", "BUILD_WINDOWS.md", "PORTABLE_RELEASE.md",
    "MODELS_AND_LICENSES.md", "INDEXING.md", "SEARCH.md", "UI_SPEC.md", "DIAGNOSTICS_DEPLOY.md",
    "UPDATE_PUBLISHING.md", "WINDOWS_ACCEPTANCE.md", "WINDOWS_HANDOFF.md", "STATIC_ACCEPTANCE.md",
    "handoff-manifest.json",
}
IGNORED_PARTS = {"artifacts", "bin", "obj", ".git", "__pycache__", ".pytest_cache"}


def fail(message: str) -> None:
    print(f"FAIL: {message}")
    raise SystemExit(1)


def main() -> int:
    missing = sorted(name for name in REQUIRED if not (ROOT / name).is_file())
    if missing:
        fail("missing required documents: " + ", ".join(missing))
    for path in ROOT.rglob("*.json"):
        if any(part in IGNORED_PARTS for part in path.parts):
            continue
        try:
            json.loads(path.read_text(encoding="utf-8"))
        except Exception as error:
            fail(f"invalid JSON {path.relative_to(ROOT)}: {error}")
    models = json.loads((ROOT / "worker/manifests/models.lock.json").read_text())
    if len(models.get("repositories", [])) != 15 or len(models.get("artifacts", [])) < 70:
        fail("model lock is incomplete")
    for artifact in models["artifacts"]:
        if not re.fullmatch(r"[a-f0-9]{64}", artifact["sha256"]):
            fail(f"invalid model hash: {artifact['id']}")
        if artifact["version"] in {"latest", "main", "master"} or "/resolve/main/" in artifact["primaryUrl"]:
            fail(f"mutable model reference: {artifact['id']}")
    dependencies = json.loads((ROOT / "worker/manifests/dependencies.lock.json").read_text())
    if len(dependencies.get("artifacts", [])) < 100:
        fail("dependency graph is incomplete")
    forbidden_suffixes = {".pem", ".pfx", ".key"}
    for path in ROOT.rglob("*"):
        if any(part in IGNORED_PARTS for part in path.parts):
            continue
        if path.is_file() and path.suffix.lower() in forbidden_suffixes:
            fail(f"private/signing material is forbidden in handoff: {path.relative_to(ROOT)}")
    for path in ROOT.rglob("*"):
        if any(part in IGNORED_PARTS for part in path.parts) or not path.is_file():
            continue
        if path.stat().st_size > 8 * 1024 * 1024:
            continue
        text = path.read_text(encoding="utf-8", errors="ignore")
        if "D:\\lixinchen.ca" in text:
            fail(f"retired website path referenced by {path.relative_to(ROOT)}")
        if re.search(r"(?i)(BEGIN (?:EC |RSA )?PRIVATE KEY|api[_-]?key\s*=\s*['\"][^'\"]+)", text):
            fail(f"possible secret in {path.relative_to(ROOT)}")
    print(f"PASS: {len(models['artifacts'])} model files and {len(dependencies['artifacts'])} dependency artifacts locked")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
