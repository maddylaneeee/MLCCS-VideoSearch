from __future__ import annotations

import argparse
import json
from pathlib import Path

from .capabilities import detect, recommend_whisper


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--storage", type=Path, required=True)
    args = parser.parse_args()
    capability = detect(str(args.storage))
    print(json.dumps({
        **capability.json(),
        "whisperRecommendation": recommend_whisper(capability.vram_bytes)
    }, ensure_ascii=False), flush=True)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
