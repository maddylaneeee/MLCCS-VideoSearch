# MLCCS Video Search v1.0.0

This is the first stable release of the local Windows video-search application.

It supports Windows 10 1809+/Windows 11 x64 with an NVIDIA GPU, at least 4 GB VRAM, and a driver compatible with bundled PyTorch 2.7.1+cu128. A system CUDA Toolkit is not required. Indexing and search are blocked when this hardware gate fails.

Highlights include 2–8 second scene indexing, OpenCLIP/BGE multimodal recall, private Qdrant Server v1.18.3 with a SQLite Outbox, FTS5 and phonetic matching, RRF explanations, filters and sorting, timestamp playback, idle RAM/VRAM release, signed/resumable updates, health-checked atomic rollback, and file-scoped handling of corrupt media.

The release does not include image indexing, diagnostics upload, telemetry, experimental controls, CPU/AMD/Intel fallback, Windows Authenticode, or a 72-hour endurance claim.

Because the installer is not Authenticode-signed, Windows SmartScreen may display an unknown-publisher warning. Verify `MLCCS-VideoSearch-Online-Setup-1.0.0.exe` with `Get-FileHash -Algorithm SHA256` and compare the result with the attached `SHA256SUMS`.

Required first-install bytes and final hashes will be copied from the frozen signed Release Manifest before publication. Optional Whisper models download on demand; OCR models are selectable.
