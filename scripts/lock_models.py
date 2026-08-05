#!/usr/bin/env python3
"""Pin every runtime model file to an immutable Hugging Face commit, size and SHA-256."""
from __future__ import annotations

from datetime import UTC, datetime
import hashlib
import json
from pathlib import Path
import urllib.parse
import urllib.request


ROOT = Path(__file__).resolve().parents[1]
SOURCE = ROOT / "worker/manifests/model-sources.json"
OUTPUT = ROOT / "worker/manifests/models.lock.json"
EXCLUDED_SUFFIXES = {".md", ".png", ".jpg", ".jpeg", ".html", ".pdf"}
EXCLUDED_NAMES = {".gitattributes"}


def get_json(url: str) -> object:
    request = urllib.request.Request(url, headers={"User-Agent": "MLCCS-VideoSearch-locker/0.1"})
    with urllib.request.urlopen(request, timeout=60) as response:
        return json.load(response)


def sha_for(url: str) -> str:
    digest = hashlib.sha256()
    request = urllib.request.Request(url, headers={"User-Agent": "MLCCS-VideoSearch-locker/0.1"})
    with urllib.request.urlopen(request, timeout=120) as response:
        for chunk in iter(lambda: response.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest()


def main() -> int:
    source = json.loads(SOURCE.read_text(encoding="utf-8"))
    artifacts: list[dict[str, object]] = []
    repositories: list[dict[str, object]] = []
    for model in source["sources"]:
        encoded_repo = urllib.parse.quote(model["repo"], safe="/")
        info = get_json(f"https://huggingface.co/api/models/{encoded_repo}?expand[]=sha&expand[]=tags")
        commit = info["sha"]
        tree = get_json(f"https://huggingface.co/api/models/{encoded_repo}/tree/{commit}?recursive=true&expand=true")
        model_bytes = 0
        file_count = 0
        for item in tree:
            if item.get("type") != "file":
                continue
            remote_path = item["path"]
            if Path(remote_path).suffix.lower() in EXCLUDED_SUFFIXES or Path(remote_path).name in EXCLUDED_NAMES:
                continue
            resolve = f"https://huggingface.co/{encoded_repo}/resolve/{commit}/{urllib.parse.quote(remote_path)}?download=true"
            sha256 = item.get("lfs", {}).get("oid") or sha_for(resolve)
            size = int(item["size"])
            flat = f"{model['id']}--{remote_path.replace('/', '--')}"
            artifacts.append({
                "id": f"{model['id']}-{file_count:03d}", "kind": model["kind"], "version": commit,
                "filename": flat, "installPath": f"{model['id']}/{remote_path}", "size": size,
                "sha256": sha256, "primaryUrl": resolve, "fallbackUrls": [], "license": model["license"],
                "vectorDimension": model["vectorDimension"], "minimumWorkerVersion": "1.0.0",
                "recommendedVramBytes": model["recommendedVramBytes"], "diskBytes": size,
            })
            file_count += 1
            model_bytes += size
        repositories.append({"id": model["id"], "repository": model["repo"], "commit": commit,
                             "license": model["license"], "fileCount": file_count, "diskBytes": model_bytes})
        print(f"{model['id']}: {file_count} files, {model_bytes} bytes")
    document = {"schemaVersion": 1, "generatedUtc": datetime.now(UTC).isoformat(),
                "repositories": repositories, "artifacts": artifacts}
    OUTPUT.write_text(json.dumps(document, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
