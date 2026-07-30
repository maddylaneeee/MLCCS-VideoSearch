#!/usr/bin/env python3
"""Create a deterministic manifest and optional synthetic media for Windows acceptance."""
from __future__ import annotations

import argparse
import json
from pathlib import Path
import subprocess


CASES = [
    {"id":"zh-name","filename":"李新晨项目介绍.mp4","queries":["李新晨 项目", "lixinchen"],"expected":["filename","speech"],"properNoun":True},
    {"id":"homophone","filename":"理新城采访.mp4","queries":["李新晨 采访"],"expected":["phonetic"],"properNoun":True},
    {"id":"english","filename":"Project Aurora launch.mp4","queries":["Aurora launch stage"],"expected":["filename","visual","speech"],"properNoun":True},
    {"id":"ocr","filename":"会议字幕.mp4","queries":["季度规划 2026"],"expected":["ocr"],"properNoun":False},
    {"id":"silent","filename":"无声红色汽车.mp4","queries":["红色汽车"],"expected":["visual"],"properNoun":False},
    {"id":"audio","filename":"纯音频访谈.m4a","queries":["本地语义检索"],"expected":["speech"],"properNoun":False},
]


def main() -> int:
    parser = argparse.ArgumentParser(); parser.add_argument("output", type=Path); parser.add_argument("--ffmpeg", type=Path)
    args = parser.parse_args(); args.output.mkdir(parents=True, exist_ok=True)
    (args.output / "evaluation.json").write_text(json.dumps({"schemaVersion":1,"cases":CASES}, ensure_ascii=False, indent=2)+"\n", encoding="utf-8")
    if args.ffmpeg:
        subprocess.run([str(args.ffmpeg), "-y", "-f", "lavfi", "-i", "color=c=red:s=1280x720:d=12:r=30",
                        "-f", "lavfi", "-i", "anullsrc=r=48000:cl=stereo", "-shortest", str(args.output / "无声红色汽车.mp4")], check=True)
        subprocess.run([str(args.ffmpeg), "-y", "-f", "lavfi", "-i", "testsrc2=s=1280x720:d=20:r=30",
                        "-f", "lavfi", "-i", "sine=frequency=440:duration=20", "-shortest", str(args.output / "scene-boundary-test.mp4")], check=True)
    print(args.output / "evaluation.json")
    return 0


if __name__ == "__main__": raise SystemExit(main())

