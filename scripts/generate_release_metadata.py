#!/usr/bin/env python3
from __future__ import annotations

import argparse
import csv
import hashlib
import json
from pathlib import Path
import shutil
import uuid
import xml.etree.ElementTree as ET


ROOT = Path(__file__).resolve().parents[1]


def sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for block in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(block)
    return digest.hexdigest()


def license_expression(value: str) -> str:
    aliases = {
        "Apache 2.0": "Apache-2.0", "Apache License 2.0": "Apache-2.0",
        "Apache Software License": "Apache-2.0", "MIT License": "MIT",
        "Modified BSD License": "BSD-3-Clause", "BSD": "BSD-3-Clause",
        "BSD 3-Clause": "BSD-3-Clause", "PSF-2.0": "Python-2.0",
    }
    normalized = aliases.get(value, value)
    if "\n" in normalized or len(normalized) > 100:
        return "LicenseRef-Package-Metadata"
    return normalized


def component(name: str, version: str, kind: str, license_value: str,
              digest: str | None = None, url: str | None = None) -> dict:
    item = {
        "type": kind, "name": name, "version": version,
        "bom-ref": f"{kind}:{name}@{version}",
        "licenses": [{"expression": license_expression(license_value)}],
    }
    if digest:
        item["hashes"] = [{"alg": "SHA-256", "content": digest}]
    if url:
        item["externalReferences"] = [{"type": "distribution", "url": url}]
    return item


def nuget_license(package_root: Path, name: str, version: str) -> str:
    package_dir = package_root / name.lower() / version.lower()
    nuspecs = list(package_dir.glob("*.nuspec"))
    if not nuspecs:
        return "LicenseRef-NuGet-Metadata"
    root = ET.parse(nuspecs[0]).getroot()
    license_node = next((node for node in root.iter() if node.tag.endswith("license")), None)
    if license_node is None or not (license_node.text or "").strip():
        return "LicenseRef-NuGet-Metadata"
    value = (license_node.text or "").strip()
    return value if license_node.get("type") == "expression" else f"LicenseRef-Package-File-{Path(value).name}"


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    output = args.output.resolve()
    output.mkdir(parents=True, exist_ok=True)

    version = json.loads((ROOT / "release/version.json").read_text())["version"]
    dependencies = json.loads((ROOT / "worker/manifests/dependencies.lock.json").read_text())
    models = json.loads((ROOT / "worker/manifests/models.lock.json").read_text())
    qdrant = json.loads((ROOT / "release/qdrant.lock.json").read_text())
    unresolved = [item["id"] for item in dependencies["artifacts"]
                  if item.get("license") in {None, "", "SEE-PACKAGE-METADATA"}]
    if unresolved:
        raise SystemExit("Unresolved licenses: " + ", ".join(unresolved))

    inventory: list[dict[str, str]] = []
    components = [component("MLCCS Video Search", version, "application", "MIT")]
    for item in dependencies["artifacts"]:
        name = item.get("package") or item["id"]
        item_version = item.get("version") or item.get("filename", "pinned")
        inventory.append({"category": "runtime", "name": name, "version": item_version,
                          "license": item["license"], "sha256": item["sha256"], "source": item["url"]})
        components.append(component(name, item_version, "library", item["license"], item["sha256"], item["url"]))
    inventory.append({"category": "runtime", "name": "Qdrant Server", "version": qdrant["version"],
                      "license": qdrant["license"], "sha256": qdrant["sha256"], "source": qdrant["url"]})
    components.append(component("Qdrant Server", qdrant["version"], "application", qdrant["license"],
                                qdrant["sha256"], qdrant["url"]))
    for repository in models["repositories"]:
        inventory.append({"category": "model", "name": repository["repository"],
                          "version": repository["commit"], "license": repository["license"],
                          "sha256": "see-model-file-inventory", "source": f"https://huggingface.co/{repository['repository']}"})
        components.append(component(repository["repository"], repository["commit"], "machine-learning-model",
                                    repository["license"], url=f"https://huggingface.co/{repository['repository']}"))
    nuget: dict[tuple[str, str], tuple[str, str]] = {}
    for assets_path in sorted(ROOT.rglob("obj/project.assets.json")):
        relative = assets_path.relative_to(ROOT)
        if relative.parts[0] not in {"src", "installer"}:
            continue
        assets = json.loads(assets_path.read_text(encoding="utf-8"))
        package_folders = [Path(path) for path in assets.get("packageFolders", {})]
        package_root = package_folders[0] if package_folders else Path.home() / ".nuget/packages"
        for key, metadata in assets.get("libraries", {}).items():
            if metadata.get("type") != "package" or "/" not in key:
                continue
            name, package_version = key.rsplit("/", 1)
            license_value = nuget_license(package_root, name, package_version)
            nuget[(name, package_version)] = (license_value,
                f"https://www.nuget.org/packages/{name}/{package_version}")
    for (name, package_version), (license_value, source) in sorted(nuget.items()):
        inventory.append({"category": "nuget", "name": name, "version": package_version,
                          "license": license_value, "sha256": "resolved-by-signed-component-files", "source": source})
        components.append(component(name, package_version, "library", license_value, url=source))

    unique = {item["bom-ref"]: item for item in components}
    sbom = {
        "bomFormat": "CycloneDX", "specVersion": "1.6", "version": 1,
        "serialNumber": f"urn:uuid:{uuid.uuid5(uuid.NAMESPACE_URL, 'mlccs-video-search-' + version)}",
        "metadata": {"component": components[0]}, "components": list(unique.values()),
    }
    (output / "MLCCS-VideoSearch-1.0.0.cdx.json").write_text(
        json.dumps(sbom, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    with (output / "THIRD-PARTY-LICENSES.csv").open("w", newline="", encoding="utf-8") as stream:
        writer = csv.DictWriter(stream, fieldnames=("category", "name", "version", "license", "sha256", "source"))
        writer.writeheader()
        writer.writerows(inventory)
    shutil.copy2(ROOT / "THIRD_PARTY_NOTICES.md", output / "THIRD_PARTY_NOTICES.md")
    shutil.copy2(ROOT / "LICENSE", output / "LICENSE")
    checksums = []
    for path in sorted(output.iterdir(), key=lambda item: item.name):
        if path.is_file() and path.name != "SHA256SUMS.metadata":
            checksums.append(f"{sha256(path)}  {path.name}")
    (output / "SHA256SUMS.metadata").write_text("\n".join(checksums) + "\n", encoding="utf-8")
    print(f"PASS: generated SBOM and {len(inventory)} license inventory rows")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
