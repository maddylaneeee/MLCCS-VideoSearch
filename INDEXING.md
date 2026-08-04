# Indexing

Only video extensions `.mp4`, `.mkv`, `.avi`, `.mov`, `.wmv`, `.m4v`, `.ts`, and `.webm` enter the v1 pipeline.

The Agent hard-gates indexing on Windows build 17763+, x64, NVIDIA GPU, 4 GB VRAM, and CUDA availability from bundled PyTorch 2.7.1+cu128. It starts private Qdrant and the Worker only after the gate passes.

Visual analysis decodes low-resolution samples at approximately 2 FPS. A motion difference of 27 starts a new scene after the 2 second minimum; every window is forcibly split at 8 seconds. The midpoint and strongest-motion frame are retained, with neighboring representatives for high-motion scenes. OpenCLIP encodes representatives and their normalized aggregate is queued to `visual_v1`.

Optional Whisper produces timed original transcript segments. Consecutive segments are grouped into semantic windows, encoded by required BGE Small, and queued to `speech_v1`. Optional PP-OCRv5 scans scene representatives; original text and boxes stay in SQLite while BGE vectors enter `ocr_v1`.

SQLite commits metadata and an idempotent Outbox operation together. Qdrant is updated afterward and the row is marked complete only on success. Startup replays incomplete rows; a missing collection reactivates stored upserts to rebuild it. Removing a library queues vector deletions before removing authoritative metadata. Original media is never modified or deleted.

Model stages are sequential so OpenCLIP, Whisper, OCR, and BGE do not remain resident together. Individual decode/model errors set the affected asset to `Failed` and the library run continues.
