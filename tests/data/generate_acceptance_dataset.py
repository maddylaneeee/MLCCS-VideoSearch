#!/usr/bin/env python3
"""Generate the deterministic, project-owned v1 release-gate video corpus."""
from __future__ import annotations

import argparse
import hashlib
import json
import os
from pathlib import Path
import subprocess


CASES = [
    ("red-circle", "红色圆形.mp4", "red", "circle", "红色圆形 本地视频搜索", [
        ("红色圆形", "visual"), ("本地视频搜索", "speech"), ("hong se yuan xing", "phonetic")]),
    ("blue-square", "蓝色方块.mp4", "blue", "square", "蓝色方块 索引完成", [
        ("蓝色方块", "visual"), ("索引完成", "speech"), ("lan se fang kuai", "phonetic")]),
    ("green-triangle", "绿色三角形.mp4", "green", "triangle", "绿色三角形 Qdrant", [
        ("绿色三角形", "visual"), ("Qdrant", "speech"), ("绿色三角", "filename")]),
    ("aurora", "Project Aurora launch.mp4", "purple", "circle", "Project Aurora launch stage", [
        ("Aurora launch", "filename"), ("purple circle", "visual"), ("launch stage", "speech")]),
    ("nvidia", "NVIDIA CUDA 演示.mp4", "green", "square", "NVIDIA CUDA twelve point eight", [
        ("NVIDIA CUDA", "filename"), ("CUDA twelve point eight", "speech"), ("green square", "visual")]),
    ("quarter-plan", "季度规划会议.mp4", "navy", "triangle", "季度规划 二零二六", [
        ("季度规划", "ocr"), ("二零二六", "speech"), ("季度规划会议", "filename")]),
    ("local-privacy", "本地隐私说明.mp4", "black", "circle", "媒体和查询不会上传", [
        ("不会上传", "speech"), ("本地隐私", "filename"), ("媒体和查询", "ocr")]),
    ("memory-release", "显存自动释放.mp4", "orange", "square", "空闲十分钟释放显存", [
        ("释放显存", "speech"), ("十分钟", "ocr"), ("显存自动释放", "filename")]),
    ("scene-boundary", "场景边界测试.mp4", "yellow", "triangle", "场景切分 两秒到八秒", [
        ("场景切分", "speech"), ("两秒到八秒", "ocr"), ("yellow triangle", "visual")]),
    ("phonetic", "理新城采访.mp4", "gray", "circle", "李新晨 项目采访", [
        ("李新晨采访", "phonetic"), ("理新城采访", "filename"), ("项目采访", "speech")]),
    ("negative-ocean", "海洋负例.mp4", "cyan", "square", "平静海洋", [
        ("平静海洋", "speech"), ("cyan square", "visual"), ("海洋负例", "filename")]),
]


def _run(command: list[str]) -> None:
    subprocess.run(command, check=True, stdout=subprocess.DEVNULL)


def _tts(text: str, output: Path) -> None:
    if os.name != "nt":
        raise RuntimeError("Deterministic acceptance TTS must be generated on Windows")
    escaped_text = text.replace("'", "''")
    escaped_path = str(output).replace("'", "''")
    script = ("Add-Type -AssemblyName System.Speech; "
              "$s=[System.Speech.Synthesis.SpeechSynthesizer]::new(); "
              f"$s.SetOutputToWaveFile('{escaped_path}'); $s.Speak('{escaped_text}'); $s.Dispose()")
    _run(["powershell.exe", "-NoProfile", "-NonInteractive", "-Command", script])


def _draw_filter(color: str, shape: str, text: str, font: str) -> str:
    escaped = text.replace("\\", "\\\\").replace(":", "\\:").replace("'", "\\'")
    rgb = {"red": (230, 40, 40), "blue": (40, 90, 230), "green": (35, 190, 80),
           "purple": (150, 60, 210), "navy": (25, 65, 130), "black": (5, 5, 5),
           "orange": (240, 125, 25), "yellow": (240, 215, 30), "gray": (150, 150, 150),
           "cyan": (25, 205, 215)}[color]
    if shape == "circle":
        condition = "lte((X-640)*(X-640)+(Y-340)*(Y-340),40000)"
        geometry = "geq=" + ":".join(f"{channel}='if({condition},{value},32)'" for channel, value in zip("rgb", rgb))
    elif shape == "triangle":
        condition = "gte(Y,140)*lte(Y,560)*gte(X,640-(Y-140)*0.55)*lte(X,640+(Y-140)*0.55)"
        geometry = "geq=" + ":".join(f"{channel}='if({condition},{value},32)'" for channel, value in zip("rgb", rgb))
    else:
        geometry = f"drawbox=x=440:y=160:w=400:h=400:color={color}:t=fill"
    return f"{geometry},drawtext=fontfile='{font}':text='{escaped}':fontcolor=white:fontsize=44:x=(w-text_w)/2:y=h-90"


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("output", type=Path)
    parser.add_argument("--ffmpeg", type=Path, required=True)
    parser.add_argument("--font", type=Path, required=True)
    args = parser.parse_args()
    args.output.mkdir(parents=True, exist_ok=True)
    records = []
    query_records = []
    for case_id, filename, color, shape, speech, queries in CASES:
        wave = args.output / f".{case_id}.wav"
        target = args.output / filename
        _tts(speech, wave)
        video_filter = _draw_filter(color, shape, speech, str(args.font))
        if case_id == "scene-boundary":
            escaped = speech.replace(":", "\\:")
            video_filter = ("drawbox=x=0:y=0:w=iw:h=ih:color=yellow:t=fill:enable='between(t,0,2.49)',"
                            "drawbox=x=0:y=0:w=iw:h=ih:color=red:t=fill:enable='between(t,2.5,5.49)',"
                            "drawbox=x=0:y=0:w=iw:h=ih:color=blue:t=fill:enable='gte(t,5.5)',"
                            f"drawtext=fontfile='{args.font}':text='{escaped}':fontcolor=white:fontsize=44:x=(w-text_w)/2:y=h-90")
        _run([str(args.ffmpeg), "-y", "-f", "lavfi", "-i", "color=c=#202020:s=1280x720:d=8:r=30",
              "-i", str(wave), "-vf", video_filter,
              "-c:v", "libx264", "-pix_fmt", "yuv420p", "-c:a", "aac", "-shortest", str(target)])
        wave.unlink()
        digest = hashlib.sha256(target.read_bytes()).hexdigest()
        records.append({"id": case_id, "file": filename, "sha256": digest, "durationMs": 8000})
        for index, (query, source) in enumerate(queries):
            query_records.append({"id": f"{case_id}-{index + 1}", "query": query,
                                  "relevant": [{"assetId": case_id, "file": filename, "source": source,
                                                "startMs": 0, "endMs": 8000}]})
    corrupt = args.output / "损坏视频.mp4"
    corrupt.write_bytes(b"not-a-video\x00mlccs-v1")
    records.append({"id": "corrupt", "file": corrupt.name,
                    "sha256": hashlib.sha256(corrupt.read_bytes()).hexdigest(), "expectedFailure": True})
    manifest = {"schemaVersion": 1, "generatorVersion": "1.0.0", "media": records,
                "queries": query_records, "qualityGates": {"recallAt10": 0.80, "mrr": 0.65}}
    (args.output / "evaluation.json").write_text(json.dumps(manifest, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    if len(query_records) < 30:
        raise RuntimeError("Acceptance corpus must contain at least 30 queries")
    print(args.output / "evaluation.json")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
