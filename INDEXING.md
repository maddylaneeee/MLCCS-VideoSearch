# Indexing

## Discovery and identity

Libraries are scanned breadth-first without following NTFS reparse points. Application data, Windows system, temp and recursively nested library roots are excluded. A media identity combines canonical path, size, modification time and an xxHash128 of first/middle/last 64 KiB blocks plus length. Full SHA-256 resolves suspected moves or duplicates. Offline roots are marked unavailable rather than interpreted as mass deletions.

SQLite is authoritative. Job claims, bounded attempts, leases and checkpoints are transactional. Expired running leases recover to queued after crash/reboot. Derived vector mutations pass through a SQLite outbox and use deterministic IDs, making replay safe. Writes to files use same-volume temporary files and atomic rename.

## Visual segmentation v1

1. Decode low resolution at approximately 2 FPS.
2. Detect scene changes at default score 27.
3. Suppress cuts that would create windows shorter than 2 seconds.
4. Split scenes longer than 8 seconds into bounded time windows.
5. Select the midpoint frame; add a peak-motion representative at least 500 ms away for high-motion windows.
6. Persist file ID, 2–8 second bounds, representative times, thumbnail path, algorithm version and model version.

There is never one visual vector for a whole video.

## Speech windows v1

Whisper receives the original media/audio and uses its own VAD. Original segments, original text and every word timestamp are persisted without replacement. Adjacent originals form 8–30 second semantic embedding windows. Each semantic window stores original segment IDs; result timing maps to the closest original segment. Speech work is rejected with `CAPABILITY_SPEECH_REQUIRES_CUDA` when CUDA is unavailable, including requests originating from advanced settings.

OOM handling lowers batch size and retries no more than the job maximum. A repeated OOM becomes an actionable failure; no loop creates duplicate jobs.

## OCR v1

OCR runs on representative frames and frames with strong subtitle-region change. Each observation stores original/normalized text, language, confidence, timestamp and bounding boxes. Low-confidence observations may join semantic recall with reduced weight but do not receive a high exact-match boost.

## Incremental rebuild rules

- Same fingerprint and unchanged algorithm/model version: reuse.
- Changed content: invalidate all derived segments for that asset through the outbox and enqueue replacements.
- Path-only move with matching full SHA-256: update identity without recomputing.
- Visual algorithm/model/dimension change: rebuild visual collection only.
- BGE change: rebuild speech/OCR semantic vectors; retain original text/timestamps.
- OCR model change: rebuild OCR observations and OCR vectors.
- Offline media: pause affected jobs; do not delete.

The Agent uses adaptive concurrency at maximum safe throughput, considering VRAM/RAM, disk queue, OOM, power and temperature. UI exit never cancels indexing. Tray pause drains the current atomic checkpoint; safe exit closes the Worker and Qdrant shard.

