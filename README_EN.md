# MLCCS Video Search

MLCCS Video Search `v1.0.0` is a local-only Windows application for finding videos by filename, visual content, speech, and on-screen text, then playing from the matching timestamp.

[中文 README](README.md)

## Requirements

- Windows 10 1809 (build 17763) or later, or Windows 11, x64.
- An NVIDIA GPU with at least 4 GB VRAM.
- A driver compatible with the bundled `PyTorch 2.7.1+cu128` runtime.
- The CUDA Toolkit is not required; a compatible NVIDIA driver is required.

Installation is allowed on an unsupported machine so the driver can be updated. First launch reports the detected Windows build, GPU, VRAM, driver, and CUDA state. Indexing and search are hard-blocked until all requirements pass, while library browsing, settings, logs, and updates remain accessible. v1.0.0 has no public CPU, AMD, Intel GPU, or sub-4 GB fallback.

## Download and integrity

[Download MLCCS Video Search v1.0.0](https://lixinchen.ca/docs/mlccs-video-search/1.0.0/MLCCS-VideoSearch-Online-Setup-1.0.0.exe)

Required first-install components are the app core, private Python/PyTorch CUDA runtime, Qdrant Server v1.18.3, OpenCLIP Standard, and BGE Small: `5,458,820,128` bytes (about `5.084 GiB`) in total. The installer displays the exact signed download size and supports resume. Whisper is downloaded on demand; OCR is optional, and the v1.0.0 OCR package is `18,631,826` bytes (about `17.77 MiB`).

The project does not use Windows Authenticode, so SmartScreen may show an unknown-publisher warning. Download only from the official link or GitHub Release and verify the installer:

```powershell
Get-FileHash .\MLCCS-VideoSearch-Online-Setup-1.0.0.exe -Algorithm SHA256
```

Compare the result with the Release `SHA256SUMS` before running it.

## Implemented behavior

- Approximately 2 FPS scene analysis with threshold 27, 2–8 second windows, and extra representatives for high motion.
- OpenCLIP Standard visual vectors, BGE Small Chinese text vectors, and a private Qdrant Server; SQLite remains authoritative for paths, source text, FTS5, and the vector Outbox.
- Filename, speech, OCR, pinyin, and phonetic FTS plus Qdrant semantic recall, fused with RRF and per-source timestamp/score explanations.
- Library, extension, duration, modification-date, and indexing-status filters; relevance, newest, and filename sorting.
- List/grid browsing, timestamp previews, playback seek, copy path, and reveal in Explorer.
- Whole-process search-model release after 5/10/30 idle minutes. Private Qdrant also stops when no indexing or search task needs it, then restarts automatically.
- Signed update checks, explicit user confirmation, resumable download, per-file verification, atomic `current/previous` swap, health check, and rollback.
- Corrupt or unsupported videos fail at file scope without failing the library job.

## Privacy and networking

Media, paths, queries, transcripts, OCR text, thumbnails, and vectors remain local. Qdrant binds only to a dynamic `127.0.0.1` port and stores only opaque IDs, times, and model/algorithm versions in payloads. v1.0.0 has no diagnostics upload, telemetry, or “help improve” network entry point.

Networking occurs only for locked component/optional model downloads and update checks. Disabling automatic checks prevents proactive update-channel requests. Redacted local logs can be opened from Settings.

Feedback settings, indexes, and model caches are not reused. Interactive setup requires confirmation before resetting them; silent setup rejects old state unless given the explicit reset flag. Reset and uninstall never delete source videos.

See [CONTRIBUTING.md](CONTRIBUTING.md), [SECURITY.md](SECURITY.md), [SUPPORT.md](SUPPORT.md), and [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md).
