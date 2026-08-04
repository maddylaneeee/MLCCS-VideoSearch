# macOS centralized static acceptance

Final gate: **PASS with Windows-only items explicitly deferred**  
Executed: 2026-07-28, macOS arm64, .NET SDK 10.0.301, Python 3.13 validation environment.  
Entry point: `scripts/Static-Acceptance.sh`. Raw logs: `artifacts/static-acceptance/` (logs are intentionally not part of the clean source archive; this report records outcomes).

## Pass

| Area | Evidence |
|---|---|
| Clean-room boundary | Target directory was absent before creation. Repository validation found no retired website path, private key, credential file, model/cache/media payload or mutable model revision. No old video-search source was opened/scanned/read. |
| JSON and schemas | All JSON parsed. Draft 2020-12 examples for settings, diagnostics, update and full model lock passed. IPC, settings, models, update, diagnostics and Qdrant schemas are versioned. |
| Python/Worker | `compileall` passed. 7/7 tests passed: schema locks, pipe framing/protocol rejection, frame limit and CUDA/VRAM speech recommendations. |
| C# platform-independent logic | 10/10 tests passed: visual windows, Whisper mapping, RRF, phonetics, redaction, recovery/retry, settings migration, SQLite/FTS migration, generated signatures and bundled signed-update verification. |
| C# builds | Core, Agent, Updater and SigningTool built Release with 0 warnings / 0 errors. Agent/Updater used `EnableWindowsTargeting=true`. |
| WinUI static | UI NuGet restore passed; every XAML file passed `xmllint`. |
| PowerShell | Every `.ps1` parsed into a PowerShell scriptblock without error. |
| Dependencies | Windows CPython 3.12 dependency resolution passed. 137 runtime/wheel artifacts have exact URL/bundled path, size and SHA-256. Vulnerable SQLitePCLRaw 2.1.11 was rejected and replaced by 2.1.12. |
| Models | 15 repositories/79 runtime files pinned to immutable commit, exact size/SHA-256, license, dimension, Worker version and VRAM/disk guidance. No `latest`, `main` or `master`. |
| Website plugin | `py_compile` passed; 8/8 targeted tests passed for normal upload, duplicate/hash, traversal/type, compression bomb, symlink, event allowlist, 180-day retention and route permissions. |
| Website upgrade | Backend-only ZIP contains exactly manifest + `videosearch_diagnostics_plugin.py`; `package_type=upgrade`; only `videosearch-diagnostics:v1`. SHA-256 `44bd5ce06cbc403beea8f2140701eab666f27afa645dc7098af796994ff81bff`. |
| Signed update sample | Manifest and ZIP signatures verify against embedded acceptance public key; tamper rejection is tested. ZIP SHA-256 `27daa157c5702c3d305484f595ef49277269dca926c200e6f56119567a624b12`. Private key is external to source/handoff. |
| Documentation | All 13 required handoff/build/index/search/UI/diagnostics/update/acceptance documents exist; commands are explicit and Windows-only claims are separated. |

## Static failures found and fixed

- Corrected unavailable Paddle GPU/PyAV package selections; pinned deliverable CPU Paddle OCR and `av` wheels while preserving CUDA-only speech.
- Built and pinned the missing pure-Python GPUtil wheel required by PaddleX on Windows.
- Upgraded Microsoft.Data.Sqlite to 10.0.10 and overrode vulnerable SQLitePCLRaw 2.1.11 with compatible 2.1.12.
- Fixed Windows path/secret/query/transcript redaction ordering and path boundary matching.
- Fixed migration resource selection and multi-statement execution; empty migration discovery now fails loudly.
- Fixed update manifest canonicalization to the same UTF-8 camelCase representation in publisher and verifier.
- Updated Windows SDK reference from unavailable `.79` to exact available `10.0.26100.80`.

No known macOS-executable static failure remains.

## Can only be verified on Windows

- WinUI XAML compiler and native application startup. The official XAML compiler is a Windows executable and cannot run on macOS.
- Named-pipe ACLs, single-instance activation, tray lifecycle and safe multi-process shutdown.
- Private CPython/FFmpeg/libVLC/Qdrant Edge/CUDA binary loading and clean-machine dependency download.
- Real media indexing/search/player correctness, fixed-dataset metrics and warm/cold latency.
- 30-minute high-load observation and 72-hour endurance.
- No-CUDA Sandbox and real CUDA/OOM behavior.
- 100/150/200%, narrow/maximized/high-contrast, keyboard, Narrator and screenshot acceptance.
- Real Defender diagnostics storage/cleanup, production website reload/rollback and update hosting/Range behavior.
- Test-channel updater install/rollback and stable `latest.json` publication with a production signing key.

These entries are **not passed** and must be executed under `WINDOWS_ACCEPTANCE.md`; macOS results must not be represented as Windows runtime acceptance.
