from __future__ import annotations

from dataclasses import asdict, dataclass
import os
import platform
import shutil
import subprocess

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
    driver_version: str | None
    supported: bool
    support_issues: tuple[str, ...]
    on_battery: bool

    def json(self) -> dict[str, object]:
        return asdict(self)


def detect(storage_path: str) -> Capabilities:
    import psutil
    gpu_name: str | None = None
    cuda_version: str | None = None
    vram = 0
    cuda = False
    driver_version: str | None = None
    try:
        import torch
        cuda = bool(torch.cuda.is_available())
        if cuda:
            gpu_name = torch.cuda.get_device_name(0)
            vram = int(torch.cuda.get_device_properties(0).total_memory)
            cuda_version = str(torch.version.cuda)
    except (ImportError, RuntimeError):
        pass
    try:
        completed = subprocess.run(
            ["nvidia-smi", "--query-gpu=name,memory.total,driver_version", "--format=csv,noheader,nounits"],
            check=True, capture_output=True, text=True, timeout=5,
            creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0),
        )
        parts = [part.strip() for part in completed.stdout.splitlines()[0].split(",")]
        if len(parts) >= 3:
            gpu_name = gpu_name or parts[0]
            if vram == 0:
                vram = int(float(parts[1]) * 1024**2)
            driver_version = parts[2] or None
    except (OSError, subprocess.SubprocessError, IndexError, ValueError):
        pass
    issues: list[str] = []
    windows_version = platform.version()
    try:
        windows_build = int(windows_version.split(".")[-1])
    except ValueError:
        windows_build = 0
    if platform.system() != "Windows" or windows_build < 17763:
        issues.append("需要 Windows 10 1809（build 17763）或更高版本的 64 位 Windows")
    if platform.machine().lower() not in {"amd64", "x86_64"}:
        issues.append("需要 x64 Windows")
    if not gpu_name or "nvidia" not in gpu_name.casefold():
        issues.append("未检测到受支持的 NVIDIA GPU")
    if vram < 4 * 1024**3:
        issues.append("NVIDIA GPU 显存必须至少为 4 GB")
    if not cuda:
        issues.append("随应用提供的 PyTorch CUDA 12.8 运行时无法使用当前驱动")
    battery = psutil.sensors_battery()
    return Capabilities(windows_version, platform.processor(), os.cpu_count() or 1,
                        int(psutil.virtual_memory().total), shutil.disk_usage(storage_path).free,
                        gpu_name, cuda, cuda_version, vram, driver_version, not issues, tuple(issues),
                        bool(battery and not battery.power_plugged))


def require_v1_hardware(capabilities: Capabilities) -> None:
    if capabilities.supported:
        return
    from .contracts import WorkerError
    raise WorkerError("CAPABILITY_UNSUPPORTED_HARDWARE", "；".join(capabilities.support_issues), False)


def require_speech(capabilities: Capabilities) -> None:
    if not capabilities.cuda_available:
        from .contracts import WorkerError
        raise WorkerError("CAPABILITY_SPEECH_REQUIRES_CUDA", "Speech indexing requires a supported CUDA GPU.", False)


def recommend_whisper(vram_bytes: int) -> dict[str, str]:
    gib = vram_bytes / (1024**3)
    if gib < 4:
        return {"model": "small", "compute_type": "int8_float16", "tier": "节省资源"}
    if gib < 10:
        return {"model": "medium", "compute_type": "int8_float16", "tier": "推荐"}
    return {"model": "large-v3", "compute_type": "float16", "tier": "高质量"}
