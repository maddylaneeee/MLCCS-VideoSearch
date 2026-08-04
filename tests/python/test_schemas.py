from __future__ import annotations
import json
from pathlib import Path
import unittest
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

if __name__ == "__main__": unittest.main()
