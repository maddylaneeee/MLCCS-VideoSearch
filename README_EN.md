<p align="center">
  <img src="assets/MLCCS.VideoSearch.png" width="160" alt="MLCCS Video Search logo">
</p>

<h1 align="center">MLCCS Video Search</h1>

<p align="center">
  A local-first Windows video search tool. Find the right moment with a filename, a visual clue, spoken words, or text that appears on screen.
</p>

<p align="center">
  <a href="#download-and-install">Download</a> ·
  <a href="#what-it-does">Features</a> ·
  <a href="#system-requirements">Requirements</a> ·
  <a href="#privacy-and-networking">Privacy</a> ·
  <a href="README.md">中文</a>
</p>

## The short version

When you remember what happened in a video but not its filename, MLCCS Video Search builds a searchable index of your local library and takes you straight to the matching timestamp.

| What you get | What it means |
| --- | --- |
| More than filename search | Search filenames, visuals, speech, on-screen text, and Chinese pinyin or phonetic matches. |
| A useful result, not just a file | Each result includes its source and timestamp; open it directly at the matching moment. |
| Data stays on your PC | Videos, paths, queries, transcripts, OCR text, thumbnails, and vectors are stored locally by default. |
| Control over your library | Narrow results by library, extension, duration, modified date, and indexing status. |

## What it does

- **Search by what you remember.** Find a visual scene, a spoken phrase, or words shown in a frame—without needing an exact filename.
- **Jump to the relevant moment.** Browse results in a list or grid, preview the matching timestamp, and seek there in the player.
- **Show why something matched.** Results retain filename, visual, speech, and OCR sources so the match is easier to judge.
- **Handle real libraries.** Sort by relevance, modified date, or filename. A damaged or unsupported video is marked as a file-level failure instead of stopping the whole library job.
- **Use resources only when needed.** The search-model process and private Qdrant service stop after idle time and restart automatically for the next search or indexing task.
- **Update with safeguards.** Updates require confirmation and use resumable downloads, per-file verification, health checks, and rollback.

## How it works

1. **Add a library** — choose a folder containing your videos.
2. **Build the index** — the app analyzes visual scenes and, when enabled, processes speech and OCR text.
3. **Search with a clue** — enter a filename, a visual description, something said in the video, or on-screen text.
4. **Open the match** — inspect the match source and timestamp, then play, copy the path, or reveal the file in Explorer.

## Download and install

[Download MLCCS Video Search v1.0.0](https://lixinchen.ca/docs/mlccs-video-search/1.0.0/MLCCS-VideoSearch-Online-Setup-1.0.0.exe)

The first installation downloads the app core, private Python/PyTorch CUDA runtime, Qdrant Server, OpenCLIP Standard, and BGE Small: `5,458,820,128` bytes (about `5.084 GiB`) in total. Setup shows the exact download size and can resume interrupted downloads. Whisper downloads on demand; OCR is optional and its v1.0.0 package is about `17.77 MiB`.

The installer is designed for a nearly bare Windows 10/11 machine. It does not require winget, .NET, Windows App Runtime, Python, the CUDA Toolkit, a browser, or a third-party package manager in advance. Before large downloads, it checks the Windows components the app needs. Dependencies available from official Microsoft sources are signature-verified before installation. Windows N/KN receives the Media Feature Pack; when a restart is required, setup preserves downloaded content and explains the next step.

> **Download-safety note:** This project does not yet use Windows Authenticode signing, so SmartScreen may show an unknown-publisher warning. Download only from the official link above and verify the SHA-256 before running the installer.

```powershell
Get-FileHash .\MLCCS-VideoSearch-Online-Setup-1.0.0.exe -Algorithm SHA256
```

Compare the result with the published [SHA256SUMS](https://lixinchen.ca/docs/mlccs-video-search/1.0.0/SHA256SUMS.txt).

## System requirements

- Windows 10 1809 (build 17763) or later, or Windows 11, x64.
- An NVIDIA GPU with 4 GB VRAM recommended; the app requires at least `3.75 GiB` reported by the driver.
- An NVIDIA driver that lets the bundled `PyTorch 2.7.1+cu128` runtime report CUDA as available.
- No separate CUDA Toolkit installation is required; a compatible NVIDIA driver is required.

Installation can still finish on an unsupported machine so you can update the driver first. On first launch, the app reports the detected Windows build, GPU, VRAM, driver, and CUDA state. Until requirements pass, indexing and search are blocked, while library browsing, settings, logs, and updates remain available.

Version 1.0.0 does not provide a CPU, AMD GPU, Intel GPU, or below-`3.75 GiB`-reported-VRAM fallback for indexing or search.

## Privacy and networking

| Stays local | Connects only when you choose it |
| --- | --- |
| Media, paths, queries, transcripts, OCR text, thumbnails, and vectors | Downloading locked components or optional models; enabled automatic update checks; manually selecting “Check for updates” |

The private Qdrant Server binds only to a dynamically selected `127.0.0.1` port. Its payload contains only opaque IDs, timestamps, and model or algorithm versions. Version 1.0.0 does not transmit diagnostic data, include automatic telemetry, or provide a “help improve” network endpoint. With automatic checks disabled, the app does not proactively contact the update channel. Local logs are redacted and can be opened from Settings.

## Updates, reset, and uninstall

- Updates never install or restart silently. The app keeps a rollback-ready previous version and runs a health check after switching.
- Reset and uninstall never delete your source videos.
- Settings, indexes, and on-demand models in the user-data directory are retained by default. Interactive setup asks before resetting existing data.

## Technical overview

For readers who want implementation detail: the app runs low-resolution scene analysis at roughly 2 FPS and keeps extra representative frames for high-motion segments. OpenCLIP Standard produces visual vectors; BGE Small produces Chinese text vectors. SQLite is authoritative for paths, source text, FTS5, and the vector Outbox, while a private Qdrant instance handles vector retrieval. Filename, speech, OCR, pinyin/phonetic FTS, and semantic recall are fused with RRF, retaining timestamp and score contributions from each source.

## Development and documentation

Windows build entry point:

```powershell
.\scripts\Build-Windows.ps1 -Configuration Release
```

Release gates require a warning-free Windows build, all automated tests, `Recall@10 ≥ 0.80` and `MRR ≥ 0.65` on a fixed dataset of at least 30 queries, plus NVIDIA/CUDA end-to-end and manual UI acceptance.

More documentation: [Search](SEARCH.md) · [Indexing](INDEXING.md) · [Architecture](ARCHITECTURE.md) · [Contributing](CONTRIBUTING.md) · [Security](SECURITY.md) · [Support](SUPPORT.md) · [Third-party notices](THIRD_PARTY_NOTICES.md)
