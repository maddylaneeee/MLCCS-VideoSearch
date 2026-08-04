# Changelog

## 1.0.0 — 2026-08-03

- Replaced Feedback-era SQLite vector blobs and beta Qdrant Edge with private Qdrant Server v1.18.3 plus a durable SQLite Outbox.
- Added production scene windows, required OpenCLIP Standard and BGE Small models, optional Whisper/OCR stages, and file-scoped failure handling.
- Added NVIDIA/VRAM/CUDA hard gating and whole-process idle release for search models and Qdrant.
- Added FTS5/Qdrant RRF search, source explanations, filters, sorting, and persistent in-session result presentation state.
- Added one signed P-256/P1363 Manifest format shared by setup and updates, immutable components, atomic update/rollback, and resumable downloads.
- Removed all v1 diagnostics-upload, telemetry, image-library, Beta, and unfinished UI surfaces.
- Added release CI, CodeQL, Dependabot, repository governance, deterministic acceptance data, SBOM/license packaging, and bilingual release documentation.

Historical Feedback records are retained under `docs/history` and are not current product documentation.
