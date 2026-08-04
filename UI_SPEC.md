# v1.0.0 UI specification

The product uses native WinUI 3 controls and Windows interaction patterns. Every visible control invokes a production behavior; v1 contains no placeholders, experimental switches, diagnostics upload, telemetry, image indexing, or CPU/non-NVIDIA fallback.

## Pages

- **Search:** query, source, library, extension, duration, modified-date and index-status filters; relevance/recent-modified/filename sort; persistent result/list-grid state; RRF source explanation and timestamp; player/context actions.
- **Library:** video-only grid/list, add/rescan/remove, status, details, default-open, containing-folder and path copy.
- **Index jobs:** stage, file, completed/failed count, progress, segment count, pause/resume/cancel and actionable file-level failures.
- **Settings:** startup, libraries, optional speech/OCR, search-model idle timeout (5/10/30 minutes, default 10), storage, update controls, local logs and About.

Navigation is at most two levels deep. Cards show a 16:9 thumbnail, two-line filename, duration, format and status; detailed paths/codecs remain in the details surface.

## First run and hardware gate

The first run explains local-only data handling, reports detected Windows build, NVIDIA GPU, VRAM, driver and CUDA result, then configures locations, video libraries and optional speech/OCR. OpenCLIP Standard and BGE Small are required. If Windows 10 build 17763+, x64, NVIDIA GPU, 4 GB VRAM or bundled PyTorch CUDA availability is missing, Index and Search remain blocked with concrete remediation; Library and Settings remain accessible. A system CUDA Toolkit is not required.

Feedback configuration, indexes and model caches require explicit reset confirmation. Source media is never deleted.

## Search memory and updates

Agent status exposes search Worker PID/state/last use/idle time and Qdrant state. When the idle timeout expires with no active query, the whole Worker exits; a later query transparently restarts it. Update UI supports automatic-check opt-out, Check now, Update now, Later and Skip this version, with download/application progress. No update silently installs or restarts the app.

## Player, accessibility and keyboard

The packaged player seeks to the hit and provides context, volume, speed and fullscreen; decode failure offers default-system open without altering the index. Core flows are keyboard complete with logical focus and programmatic names. Validate Ctrl+L/F, Ctrl+, Ctrl+O, Ctrl+Shift+O, Space, arrows and Esc where applicable.

The frozen candidate must be manually checked at 100/150/200% scaling, narrow/maximized windows and high contrast. Text wraps or scrolls without overlap; status/errors expose labels and remediation; critical actions never depend on hover.
