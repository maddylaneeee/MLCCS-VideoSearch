# Windows v1.0.0 centralized acceptance

Run this gate once against a frozen Release Candidate. Record commit SHA, commands, machine specification, Windows build, GPU/driver, artifact hashes, logs, screenshots, start/end times, and outcomes. No 30-minute or 72-hour endurance run is required.

## Automated validation

Run repository, version, schema, C# and Python tests on a clean Windows build environment. Validate Manifest tampering, wrong-key and altered-field rejection, archive size/hash/file-hash checks, path-traversal rejection, interrupted downloads, Range resume, pause/resume, package layout, SBOM/license inventory, and rollback.

Run the end-to-end product gate on a supported Windows 10 1809+/11 x64 NVIDIA system with at least 4 GB VRAM:

1. Start the published single-file installer with `PATH` cleared and no reliance on system `dotnet`, winget, PowerShell modules, Windows App Runtime, Python, CUDA Toolkit, browser, or third-party package manager. Generate a prerequisite report and verify Windows version/architecture, VC++ x64 runtime, Media Foundation, DirectX/WinUI, networking, and cryptography checks. Exercise the official Microsoft VC++ download, final-host allowlist, Authenticode publisher verification, and safe rejection of a replaced/untrusted executable. On an N/KN or otherwise deficient disposable fixture when available, verify Media Feature Pack or DISM/SFC repair, reboot handling, and successful post-repair recheck; do not remove healthy Windows components merely to manufacture this fixture.
2. Verify clean online install, immutable component layout, shortcut target, uninstall, and confirmed Feedback-state reset that never deletes source videos.
3. Verify detected GPU, VRAM, driver and `torch.cuda.is_available()`. An unsupported machine must block index/search while keeping Library and Settings available.
4. Index the fixed dataset and verify 2 FPS analysis, threshold 27, 2–8 second windows, high-motion representatives, file-scoped corrupt-video failure, sequential model release, SQLite Outbox replay, Qdrant restart and deterministic rebuild.
5. Search filename, visual, speech, OCR and pinyin fields; validate library/extension/duration/date/status filters, relevance/modified/name sort, RRF source explanations and timestamps.
6. Compute at least 30 fixed queries. Hard gates are `Recall@10 >= 0.80` and `MRR >= 0.65`. Record phonetic improvement, cold start, warm p50/p95, indexing throughput and memory changes without making them release blockers.
7. With the default 10-minute timeout, verify the idle search Worker exits only when no query is active; RAM/VRAM drops materially; Qdrant stops when no index/search work remains; the next query starts a new PID and succeeds.
8. Verify signed Manifest and `latest.json`, resumable update download, Update now/Later/Skip, disabled automatic checks, safe UI/Agent/Worker/Qdrant shutdown, health check, atomic current/previous swap and failed-update rollback without mixed versions.

## One manual UI gate

After automated checks pass, complete one consolidated manual checklist. The release owner must explicitly approve:

- install and first-run hardware guidance;
- Search, Library, Jobs, Settings and About behavior;
- every visible filter/sort/action and result explanation;
- player seek/context/keyboard behavior;
- narrow/maximized layouts, high contrast, keyboard focus and 100/150/200% scaling.

Text clipping, overlap, missing scroll/focus, unlabeled errors, inactive controls or any experimental/unfinished surface blocks release. No formal Release may be published until the user explicitly confirms this manual gate.

## Publication gate

After acceptance, generate payloads on Windows and return only unsigned Manifest metadata to the secure signing workstation. Sign with the production key, publish component payloads, installer, then the versioned signed Manifest. Re-download public files and verify size, SHA-256, signature, Range and component HEAD responses. Merge the release PR, require green `main` CI, create immutable tag/GitHub Release `v1.0.0`, and publish signed stable `latest.json` last.

Required evidence: frozen commit SHA, artifact hashes, tests, search metrics, memory/PID records, screenshots and manual approval, SBOM/license inventory, tamper/rollback records, public-download verification and remaining known limitations.
