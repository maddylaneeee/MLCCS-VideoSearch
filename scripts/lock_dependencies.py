#!/usr/bin/env python3
"""Convert a pip --dry-run --report for CPython 3.12/win_amd64 into immutable artifact locks."""
from __future__ import annotations

import argparse
from datetime import UTC, datetime
import hashlib
import json
from pathlib import Path
import urllib.parse
import urllib.request


PYTHON = {
    "id": "python-3.12.10-embed-amd64", "kind": "python-runtime",
    "filename": "python-3.12.10-embed-amd64.zip", "size": 11133606,
    "sha256": "4acbed6dd1c744b0376e3b1cf57ce906f9dc9e95e68824584c8099a63025a3c3",
    "url": "https://www.python.org/ftp/python/3.12.10/python-3.12.10-embed-amd64.zip",
    "license": "Python-2.0",
}
GET_PIP = {
    "id": "get-pip", "kind": "get-pip", "filename": "get-pip.py", "size": 2226848,
    "sha256": "a341e1a43e38001c551a1508a73ff23636a11970b61d901d9a1cad2a18f57055",
    "url": "https://bootstrap.pypa.io/get-pip.py", "license": "MIT",
}


def pypi_file(name: str, version: str, sha256: str) -> dict[str, object]:
    url = f"https://pypi.org/pypi/{urllib.parse.quote(name)}/{urllib.parse.quote(version)}/json"
    with urllib.request.urlopen(url, timeout=30) as response:
        document = json.load(response)
    for item in document["urls"]:
        if item["digests"]["sha256"] == sha256:
            return {"filename": item["filename"], "size": item["size"], "url": item["url"]}
    raise RuntimeError(f"PyPI did not report the selected artifact for {name}=={version} {sha256}")


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("report", type=Path)
    parser.add_argument("--root", type=Path, default=Path(__file__).resolve().parents[1])
    args = parser.parse_args()
    report = json.loads(args.report.read_text(encoding="utf-8"))
    artifacts: list[dict[str, object]] = [PYTHON, GET_PIP]
    requirements: list[str] = []
    for item in report["install"]:
        name = item["metadata"]["name"]
        version = item["metadata"]["version"]
        hashes = item["download_info"]["archive_info"].get("hashes", {})
        sha256 = hashes.get("sha256") or item["download_info"]["archive_info"]["hash"].removeprefix("sha256=")
        source_url = item["download_info"]["url"]
        if source_url.startswith("file:"):
            path = Path(urllib.parse.unquote(urllib.parse.urlparse(source_url).path))
            selected = {"filename": path.name, "size": path.stat().st_size, "url": f"bundled:worker/vendor/{path.name}"}
        else:
            selected = pypi_file(name, version, sha256)
        artifacts.append({"id": f"pypi-{name.lower().replace('_','-')}-{version}", "kind": "python-wheel",
                          "package": name, "version": version, **selected, "sha256": sha256,
                          "license": item["metadata"].get("license_expression") or item["metadata"].get("license") or "SEE-PACKAGE-METADATA"})
        requirements.append(f"{name}=={version} --hash=sha256:{sha256}")
    output = {"schemaVersion": 1, "generatedUtc": datetime.now(UTC).isoformat(),
              "target": {"python": "3.12", "implementation": "cp", "abi": "cp312", "platform": "win_amd64"},
              "artifacts": artifacts}
    manifest_path = args.root / "worker/manifests/dependencies.lock.json"
    manifest_path.write_text(json.dumps(output, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    (args.root / "worker/requirements.hashed.txt").write_text("\n".join(sorted(requirements, key=str.casefold)) + "\n", encoding="utf-8")
    print(f"locked {len(artifacts)} artifacts")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
