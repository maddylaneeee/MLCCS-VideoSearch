# Support

Before filing an issue, confirm Windows 10 build 17763 or later, x64, an NVIDIA GPU with at least 4 GB VRAM, and a driver compatible with the bundled PyTorch CUDA 12.8 runtime. Installing the CUDA Toolkit does not replace the driver requirement.

For installation or update problems, provide the app version, Windows build, GPU model, VRAM, NVIDIA driver version, the stable error code, and a short redacted excerpt from the local log folder. Do not attach private media, paths, queries, transcripts, OCR text, API keys, full logs, or Manifest private-key material.

SmartScreen may warn because releases do not use Authenticode. Verify the installer with PowerShell `Get-FileHash -Algorithm SHA256` and compare it with the GitHub Release `SHA256SUMS`.

Unsupported configurations include Windows older than 10 1809, 32-bit Windows, CPU-only systems, AMD/Intel GPUs, NVIDIA GPUs below 4 GB VRAM, and drivers for which the bundled PyTorch runtime reports CUDA unavailable.
