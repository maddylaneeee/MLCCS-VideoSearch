# Architecture

MLCCS Video Search v1.0.0 has five process boundaries:

1. WinUI UI: interactive browsing, search, playback, settings, and explicit update decisions.
2. Agent: single-instance named-pipe authority for configuration, hardware gating, job lifecycle, Worker/Qdrant processes, status, and local logs.
3. Private Python Workers: one indexing process and one lazily started search process. Only these load ML models.
4. Qdrant Server v1.18.3: a private component bound to a dynamic `127.0.0.1` port with a per-install API key. The Agent starts, health-checks, restarts, and stops it.
5. External Updater: stored outside `current`, re-verifies the signed Manifest, stages the app core, requests safe shutdown, atomically swaps `current/previous`, health-checks, and rolls back.

SQLite is authoritative for libraries, canonical paths, source text, FTS5, segments, index-run state, and the vector Outbox. A metadata/vector change first commits to SQLite. Qdrant success then marks the Outbox row complete. Missing Qdrant collections reset their authoritative Outbox rows for deterministic rebuild.

Qdrant payloads are deliberately limited to asset/segment IDs, times, and algorithm/model versions. Paths, filenames, queries, transcripts, OCR text, and confidence values never enter Qdrant. Its endpoint and API key are delivered to child processes without command-line arguments and are not written to logs.

The indexing pipeline loads one model family at a time: OpenCLIP, optional Whisper, optional OCR, then required BGE. Each stage releases the previous model before the next begins. A corrupt video is recorded as a file-scoped failure.

The search Worker is terminated after the configured 5/10/30 minute idle timeout, never while a query is running. When neither indexing nor search needs Qdrant, the Agent stops Qdrant too. Operating-system process teardown is the memory/VRAM release boundary.

Product version `1.0.0` is independent from IPC/schema protocol `1.0`. Network activity is limited to signed component/model downloads and enabled/manual update checks. There is no diagnostics upload or telemetry surface in v1.
