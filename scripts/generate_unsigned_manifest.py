#!/usr/bin/env python3
"""Generate a release manifest without retaining the complete file inventory in memory."""
from __future__ import annotations

import argparse
import hashlib
import json
import os
from pathlib import Path
from typing import Any, TextIO


def _json(value: Any) -> str:
    return json.dumps(value, ensure_ascii=False, separators=(",", ":"))


def _sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as source:
        while chunk := source.read(1024 * 1024):
            digest.update(chunk)
    return digest.hexdigest()


def _files(stage: Path) -> list[Path]:
    files = [path for path in stage.rglob("*") if path.is_file()]
    files.sort(key=lambda path: path.relative_to(stage).as_posix())
    if not files:
        raise ValueError(f"Empty component stage: {stage}")
    return files


def _write_component(output: TextIO, component: dict[str, Any]) -> None:
    stage = Path(component.pop("stage")).resolve()
    if not stage.is_dir():
        raise FileNotFoundError(f"Missing component stage: {stage}")
    output.write("{")
    for index, (name, value) in enumerate(component.items()):
        if index:
            output.write(",")
        output.write(f"{_json(name)}:{_json(value)}")
    output.write(',"files":[')
    for index, path in enumerate(_files(stage)):
        if index:
            output.write(",")
        relative = path.relative_to(stage).as_posix()
        output.write(_json({"path": relative, "size": path.stat().st_size, "sha256": _sha256(path)}))
    output.write("]}")


def generate(descriptor_path: Path, output_path: Path) -> None:
    descriptor = json.loads(descriptor_path.read_text(encoding="utf-8-sig"))
    components = descriptor.pop("components")
    descriptor["components"] = None
    temporary = output_path.with_suffix(output_path.suffix + ".tmp")
    temporary.parent.mkdir(parents=True, exist_ok=True)
    try:
        with temporary.open("w", encoding="utf-8", newline="\n") as output:
            output.write("{")
            first = True
            for name, value in descriptor.items():
                if name == "components":
                    continue
                if not first:
                    output.write(",")
                output.write(f"{_json(name)}:{_json(value)}")
                first = False
            if not first:
                output.write(",")
            output.write('"components":[')
            for index, component in enumerate(components):
                if index:
                    output.write(",")
                _write_component(output, dict(component))
            output.write('],"manifestSignature":""}\n')
        os.replace(temporary, output_path)
    except BaseException:
        temporary.unlink(missing_ok=True)
        raise


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--descriptor", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    generate(args.descriptor, args.output)
    print(f"Generated unsigned manifest at {args.output}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
