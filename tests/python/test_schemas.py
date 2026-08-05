from __future__ import annotations
import json
import hashlib
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest
import zipfile
import jsonschema

ROOT = Path(__file__).resolve().parents[2]

class SchemaTests(unittest.TestCase):
    def validate(self, schema: str, document: Path) -> None:
        validator = jsonschema.Draft202012Validator(json.loads((ROOT / "schemas" / schema).read_text()))
        errors = sorted(validator.iter_errors(json.loads(document.read_text())), key=lambda e: list(e.path))
        self.assertEqual([], [f"{list(error.path)}: {error.message}" for error in errors])

    def test_settings(self): self.validate("settings.schema.json", ROOT / "tests/contracts/settings.valid.json")
    def test_update(self): self.validate("update-manifest.schema.json", ROOT / "tests/contracts/update.valid.json")
    def test_model_lock(self): self.validate("model-manifest.schema.json", ROOT / "worker/manifests/models.lock.json")

    def test_unsigned_manifest_generator_streams_sorted_file_inventory(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            stage = root / "stage"
            (stage / "nested").mkdir(parents=True)
            (stage / "z.txt").write_text("z", encoding="utf-8")
            (stage / "nested" / "a.txt").write_text("alpha", encoding="utf-8")
            descriptor = {
                "schemaVersion": 1, "productVersion": "1.0.0", "minimumCompatibleVersion": "1.0.0",
                "requirements": {"minimumWindowsVersion": "Windows 10 1809", "minimumWindowsBuild": 17763,
                                 "architecture": "x64", "gpuVendor": "NVIDIA", "minimumVramBytes": 4026531840,
                                 "cudaRuntime": "PyTorch 2.7.1+cu128", "cudaToolkitRequired": False},
                "entryPoint": "current/ui/app.exe", "publishedUtc": "2026-08-04T00:00:00.0000000+00:00",
                "releaseNotes": "notes", "mandatory": False,
                "components": [{"id": "app-core", "name": "App", "description": "Core",
                                "url": "https://example.invalid/app.zip", "installScope": "current",
                                "required": True, "defaultSelected": True, "size": 1,
                                "sha256": "0" * 64, "stage": str(stage)}], "keyId": "manifest-v1",
            }
            descriptor_path = root / "descriptor.json"
            output = root / "manifest.json"
            descriptor_path.write_text(json.dumps(descriptor), encoding="utf-8")
            subprocess.run([sys.executable, str(ROOT / "scripts/generate_unsigned_manifest.py"),
                            "--descriptor", str(descriptor_path), "--output", str(output)], check=True)
            manifest = json.loads(output.read_text(encoding="utf-8"))
            files = manifest["components"][0]["files"]
            self.assertEqual(["nested/a.txt", "z.txt"], [item["path"] for item in files])
            self.assertEqual(hashlib.sha256(b"alpha").hexdigest(), files[0]["sha256"])
            self.assertEqual("", manifest["manifestSignature"])
            setup = root / "MLCCS-VideoSearch-Online-Setup-1.0.0.exe"
            setup.write_bytes(b"setup")
            publication = root / "publication-record.json"
            subprocess.run([sys.executable, str(ROOT / "scripts/generate_publication_record.py"),
                            "--manifest", str(output), "--setup", str(setup),
                            "--output", str(publication)], check=True)
            record = json.loads(publication.read_text(encoding="utf-8"))
            self.assertEqual(1, record["requiredDownloadBytes"])
            self.assertEqual(hashlib.sha256(b"setup").hexdigest(), record["setup"]["sha256"])

    def test_release_payload_validator_checks_archive_and_installed_hashes(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            packages = root / "packages"
            packages.mkdir()
            archive = packages / "app-core-1.0.0.zip"
            with zipfile.ZipFile(archive, "w") as output:
                output.writestr("app.txt", b"application")
            archive_hash = hashlib.sha256(archive.read_bytes()).hexdigest()
            setup = packages / "MLCCS-VideoSearch-Online-Setup-1.0.0.exe"
            setup.write_bytes(b"setup")
            manifest = {"productVersion": "1.0.0", "manifestSignature": "", "components": [{
                "id": "app-core", "required": True, "size": archive.stat().st_size,
                "sha256": archive_hash, "files": [{"path": "app.txt", "size": 11,
                                                     "sha256": hashlib.sha256(b"application").hexdigest()}]
            }] * 5}
            # Five distinct identifiers satisfy the production minimum while sharing the test archive.
            manifest["components"] = [{**item, "id": f"component-{index}"}
                                      for index, item in enumerate(manifest["components"])]
            for item in manifest["components"]:
                item["url"] = f"https://example.invalid/{archive.name}"
            (packages / "release-manifest.unsigned.json").write_text(json.dumps(manifest), encoding="utf-8")
            record = {"requiredDownloadBytes": archive.stat().st_size * 5,
                      "setup": {"file": setup.name, "size": setup.stat().st_size,
                                "sha256": hashlib.sha256(b"setup").hexdigest()},
                      "components": [{"id": item["id"], "file": archive.name}
                                     for item in manifest["components"]]}
            (root / "publication-record.json").write_text(json.dumps(record), encoding="utf-8")
            (packages / "MLCCS-VideoSearch-1.0.0.cdx.json").write_text("{}", encoding="utf-8")
            (packages / "THIRD-PARTY-LICENSES.csv").write_text("name\n", encoding="utf-8")
            completed = subprocess.run([sys.executable, str(ROOT / "scripts/validate_release_payload.py"),
                                        str(root)], check=True, capture_output=True, text=True)
            self.assertEqual(5, json.loads(completed.stdout)["componentCount"])

if __name__ == "__main__": unittest.main()
