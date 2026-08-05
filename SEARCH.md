# Search

`search.query` accepts `query`, `source`, `limit`, `filters`, and `sort`. `filters` supports resource libraries, extensions, minimum/maximum duration, modification-date bounds, and indexing statuses. Sort modes are `relevance`, `modified-desc`, and `filename`. The old top-level `library` value remains only for transition tests.

Recall sources are:

1. SQLite FTS5 over filename, transcript, OCR, pinyin, and initials.
2. Qdrant `visual_v1` queried with OpenCLIP text embeddings.
3. Qdrant `speech_v1` and `ocr_v1` queried with BGE Small embeddings.

Only asset IDs admitted by the SQLite filter scope are sent in the Qdrant filter. Results are fused with reciprocal-rank fusion. Every result includes its source labels, matching timestamp, raw similarity where available, RRF contribution, and a user-readable explanation. Paths and source text are resolved from SQLite after vector recall.

The UI preserves query, source, filters, sort, results, status, and list/grid mode while navigating. A query holds the Worker busy flag until it finishes. After 5/10/30 idle minutes the Agent terminates the whole search process, then stops Qdrant when no index job needs it; the next query starts a new PID automatically.
