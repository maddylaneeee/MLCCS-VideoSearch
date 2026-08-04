# MLCCS Video Search v1.0.0

This is the first stable release of the local Windows video-search application.

It supports Windows 10 1809+/Windows 11 x64 with a 4 GB-class NVIDIA GPU and a driver compatible with bundled PyTorch 2.7.1+cu128. Driver-reported VRAM down to 3.75 GiB is accepted so firmware-reserved memory does not incorrectly reject retail 4 GB GPUs. A system CUDA Toolkit is not required. Indexing and search are blocked when this hardware gate fails.

Highlights include 2–8 second scene indexing, OpenCLIP/BGE multimodal recall, private Qdrant Server v1.18.3 with a SQLite Outbox, FTS5 and phonetic matching, RRF explanations, filters and sorting, timestamp playback, idle RAM/VRAM release, signed/resumable updates, health-checked atomic rollback, and file-scoped handling of corrupt media.

Scene sampling is processed as a bounded stream, so host-memory use no longer grows with video duration. Four-GB-class GPUs use a conservative automatic batch cap, background indexing no longer receives a system-disruptive high process priority, and already indexed files with the same fingerprint and model version are skipped after Agent restarts.

The self-contained installer supports a nearly bare Windows 10/11 x64 environment. Before downloading the large signed payload, it checks and, when necessary, obtains the Microsoft Visual C++ x64 runtime, Windows N/KN Media Feature Pack, and missing Windows media/DirectX/WinUI/network/cryptography components from Microsoft or Windows Update. Microsoft executables are Authenticode-verified and all repairs are rechecked. .NET 10, Windows App SDK, Python/PyTorch CUDA, and Qdrant are bundled; no winget, browser, third-party package manager, or CUDA Toolkit is required.

The installer window uses buffered layout painting and throttled progress text updates to avoid whole-window flicker during long downloads, extraction, and verification.

The release does not include image indexing, diagnostics upload, telemetry, experimental controls, CPU/AMD/Intel fallback, Windows Authenticode, or a 72-hour endurance claim.

Because the installer is not Authenticode-signed, Windows SmartScreen may display an unknown-publisher warning. Verify `MLCCS-VideoSearch-Online-Setup-1.0.0.exe` with `Get-FileHash -Algorithm SHA256` and compare the result with the attached `SHA256SUMS`.

Required first-install components total exactly `5,458,820,128` bytes (about `5.084 GiB`). Optional Whisper models download on demand; the selectable v1.0.0 OCR package is `18,631,826` bytes (about `17.77 MiB`). Final artifact hashes are published in the signed Release Manifest and `SHA256SUMS`.
