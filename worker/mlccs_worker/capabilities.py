from __future__ import annotations

from dataclasses import asdict, dataclass
import os
import platform
import shutil

@dataclass(frozen=True)
class Capabilities:
    windows_version: str
    cpu: str
    logical_processors: int
    memory_bytes: int
    free_disk_bytes: int
    gpu_name: str | None
    cuda_available: bool
    cuda_version: str | None
    vram_bytes: int
    on_battery: bool

    def json(self) -> dict[str, object]:
        return asdict(self)


def detect(storage_path: str) -> Capabilities:
    import psutil
    gpu_name: str | None = None
    cuda_version: str | None = None
    vram = 0
    cuda = False
    try:
        import torch
        cuda = bool(torch.cuda.is_available())
        if cuda:
            gpu_name = torch.cuda.get_device_name(0)
            vram = int(torch.cuda.get_device_properties(0).total_memory)
            cuda_version = str(torch.version.cuda)
    except (ImportError, RuntimeError):
        pass
    battery = psutil.sensors_battery()
    return Capabilities(platform.version(), platform.processor(), os.cpu_count() or 1,
                        int(psutil.virtual_memory().total), shutil.disk_usage(storage_path).free,
                        gpu_name, cuda, cuda_version, vram, bool(battery and not battery.power_plugged))


def require_speech(capabilities: Capabilities) -> None:
    if not capabilities.cuda_available:
        from .contracts import WorkerError
        raise WorkerError("CAPABILITY_SPEECH_REQUIRES_CUDA", "Speech indexing requires a supported CUDA GPU.", False)


def recommend_whisper(vram_bytes: int) -> dict[str, str]:
    gib = vram_bytes / (1024**3)
    if gib < 4:
        return {"model": "small", "compute_type": "int8_float16", "tier": "节省资源"}
    if gib < 6:
        return {"model": "medium", "compute_type": "int8_float16", "tier": "推荐"}
    if gib < 10:
        return {"model": "large-v3-turbo", "compute_type": "int8_float16", "tier": "推荐"}
    return {"model": "large-v3-turbo", "compute_type": "float16", "tier": "推荐"}
