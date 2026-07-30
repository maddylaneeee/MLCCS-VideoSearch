from __future__ import annotations

import argparse
import os
from pathlib import Path
import sys

from .pipe import read_frame, write_frame
from .pipeline import WorkerPipeline


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--data-root", type=Path, required=True)
    parser.add_argument("--tools-root", type=Path, required=True)
    parser.add_argument("--models-root", type=Path, required=True)
    parser.add_argument("--pipe-name", help=r"User-scoped Windows named pipe, for example \\.\pipe\MLCCS.VideoSearch.Worker.v1")
    parser.add_argument("--stdio", action="store_true", help="Framed stdio transport reserved for offline protocol tests")
    args = parser.parse_args()
    pipeline = WorkerPipeline(args.data_root, args.tools_root, args.models_root)
    if args.pipe_name:
        pipe = open(args.pipe_name, "r+b", buffering=0)
        source = destination = pipe
    elif args.stdio:
        source = os.fdopen(sys.stdin.fileno(), "rb", buffering=0, closefd=False)
        destination = os.fdopen(sys.stdout.fileno(), "wb", buffering=0, closefd=False)
    else:
        parser.error("A private named pipe is required; no network listener is available.")
    while True:
        try:
            request = read_frame(source)
            write_frame(destination, pipeline.dispatch(request))
        except EOFError:
            return 0


if __name__ == "__main__":
    raise SystemExit(main())
