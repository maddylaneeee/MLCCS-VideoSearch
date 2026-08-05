# Changelog

## 1.0.0 — 2026-08-03

- Replaced Feedback-era SQLite vector blobs and beta Qdrant Edge with private Qdrant Server v1.18.3 plus a durable SQLite Outbox.
- Added production scene windows, required OpenCLIP Standard and BGE Small models, optional Whisper/OCR stages, and file-scoped failure handling.
- Added NVIDIA/VRAM/CUDA hard gating and whole-process idle release for search models and Qdrant.
- Added FTS5/Qdrant RRF search, source explanations, filters, sorting, and persistent in-session result presentation state.
- Added one signed P-256/P1363 Manifest format shared by setup and updates, immutable components, atomic update/rollback, and resumable downloads.
- Added a bare-Windows prerequisite gate that securely downloads or repairs missing Microsoft VC++ and Windows media/core components before payload installation.
- Accepted driver-reported VRAM down to 3.75 GiB for retail 4 GB-class NVIDIA GPUs and kept the same CUDA/vendor hard gate.
- Buffered installer layout painting and throttled high-frequency progress redraws to prevent whole-window flicker.
- Streamed scene sampling in bounded 2–8 second windows instead of retaining full-resolution frames for an entire video, capped 4 GB-class GPU batches, and kept background indexing at non-disruptive process priorities.
- Added deterministic unchanged-file reuse so Agent restarts do not re-index an entire library, plus complete and backward-compatible task-page hardware status fields.
- Removed all v1 diagnostics-upload, telemetry, image-library, Beta, and unfinished UI surfaces.
- Added release CI, CodeQL, Dependabot, repository governance, deterministic acceptance data, SBOM/license packaging, and bilingual release documentation.
