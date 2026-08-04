#!/usr/bin/env python3
from __future__ import annotations

import json
from pathlib import Path
import re
import sys


ROOT = Path(__file__).resolve().parents[1]
REQUIRED = {
    "README.md", "README_EN.md", "ARCHITECTURE.md", "BUILD_WINDOWS.md",
    "MODELS_AND_LICENSES.md", "INDEXING.md", "SEARCH.md", "UI_SPEC.md",
    "UPDATE_PUBLISHING.md", "WINDOWS_ACCEPTANCE.md", "CHANGELOG.md", "SECURITY.md",
    "CONTRIBUTING.md", "SUPPORT.md", "release-notes.md",
}
IGNORED_PARTS = {
    "artifacts", "bin", "obj", ".git", ".tools", ".dotnet", ".nuget",
    "__pycache__", ".pytest_cache", "retained", "evidence", "history",
}


def fail(message: str) -> None:
    print(f"FAIL: {message}")
    raise SystemExit(1)


def main() -> int:
    missing = sorted(name for name in REQUIRED if not (ROOT / name).is_file())
    if missing:
        fail("missing required documents: " + ", ".join(missing))
    if not (ROOT / "worker/runtime-sitecustomize.py").is_file():
        fail("private runtime worker-path bootstrap is missing")
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
    get_pip = next((item for item in dependencies["artifacts"] if item.get("id") == "get-pip"), None)
    if not get_pip or not re.fullmatch(
        r"https://raw\.githubusercontent\.com/pypa/get-pip/[0-9a-f]{40}/public/get-pip\.py",
        str(get_pip.get("url", "")),
    ):
        fail("get-pip must use an immutable PyPA Git commit URL")
    if any(item.get("package") == "qdrant-edge-py" for item in dependencies["artifacts"]):
        fail("beta qdrant-edge-py must not ship in v1")
    qdrant = json.loads((ROOT / "release/qdrant.lock.json").read_text())
    if qdrant.get("version") != "1.18.3" or not re.fullmatch(r"[a-f0-9]{64}", qdrant.get("sha256", "")):
        fail("Qdrant Server lock is missing or mutable")
    version = json.loads((ROOT / "release/version.json").read_text())["version"]
    if version != "1.0.0":
        fail(f"release/version.json must declare 1.0.0, got {version}")
    public_version_files = {
        "Directory.Build.props": r"<VersionPrefix>([^<]+)</VersionPrefix>",
        "worker/pyproject.toml": r'(?m)^version = "([^"]+)"$',
        "worker/mlccs_worker/__init__.py": r'__version__ = "([^"]+)"',
    }
    for name, pattern in public_version_files.items():
        match = re.search(pattern, (ROOT / name).read_text(encoding="utf-8"))
        if not match or match.group(1) != version:
            fail(f"public version mismatch in {name}")
    forbidden_suffixes = {".pem", ".pfx", ".key"}
    for path in ROOT.rglob("*"):
        if any(part in IGNORED_PARTS for part in path.parts):
            continue
        if path.is_file() and path.suffix.lower() in forbidden_suffixes:
            fail(f"private/signing material is forbidden in handoff: {path.relative_to(ROOT)}")
    for path in ROOT.rglob("*"):
        if any(part in IGNORED_PARTS for part in path.parts) or not path.is_file():
            continue
        if path.resolve() == Path(__file__).resolve():
            continue
        if path.stat().st_size > 8 * 1024 * 1024:
            continue
        text = path.read_text(encoding="utf-8", errors="ignore")
        if "D:\\lixinchen.ca" in text:
            fail(f"retired website path referenced by {path.relative_to(ROOT)}")
        if re.search(r"(?i)(BEGIN (?:EC |RSA )?PRIVATE KEY|api[_-]?key\s*=\s*['\"][^'\"]+)", text):
            fail(f"possible secret in {path.relative_to(ROOT)}")
        if re.search(r"(?i)qdrant[-_]edge|helpImprove|diagnostic upload|diagnostic upload", text):
            fail(f"retired v1 feature referenced by {path.relative_to(ROOT)}")
    print(f"PASS: {len(models['artifacts'])} model files and {len(dependencies['artifacts'])} dependency artifacts locked")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
