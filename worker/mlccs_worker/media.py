from __future__ import annotations

from dataclasses import asdict, dataclass
import json
import subprocess
from pathlib import Path

from .contracts import WorkerError


@dataclass(frozen=True)
class MediaInfo:
    duration_ms: int
    width: int | None
    height: int | None
    fps: float | None
    codec: str | None
    has_audio: bool
    streams: list[dict[str, object]]


def probe(path: Path, ffprobe: Path) -> MediaInfo:
    command = [str(ffprobe), "-v", "error", "-show_format", "-show_streams", "-of", "json", str(path)]
    result = subprocess.run(command, capture_output=True, text=True, encoding="utf-8", timeout=120, check=False)
    if result.returncode:
        raise WorkerError("INDEX_MEDIA_UNSUPPORTED", result.stderr.strip()[:500])
    doc = json.loads(result.stdout)
    streams = doc.get("streams", [])
    video = next((s for s in streams if s.get("codec_type") == "video"), None)
    duration = float(doc.get("format", {}).get("duration") or (video or {}).get("duration") or 0)
    fps = _rate((video or {}).get("avg_frame_rate"))
    return MediaInfo(round(duration * 1000), (video or {}).get("width"), (video or {}).get("height"), fps,
                     (video or {}).get("codec_name"), any(s.get("codec_type") == "audio" for s in streams), streams)


def _rate(value: str | None) -> float | None:
    if not value or value == "0/0":
        return None
    numerator, denominator = value.split("/", 1)
    return float(numerator) / float(denominator)

