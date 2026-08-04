PRAGMA foreign_keys = ON;
PRAGMA journal_mode = WAL;
PRAGMA synchronous = FULL;

CREATE TABLE IF NOT EXISTS schema_migrations(version INTEGER PRIMARY KEY, applied_utc TEXT NOT NULL);
CREATE TABLE IF NOT EXISTS real_index_runs(
  id TEXT PRIMARY KEY, library_root TEXT NOT NULL, started_utc TEXT NOT NULL,
  completed_utc TEXT, status TEXT NOT NULL, device TEXT NOT NULL, model_version TEXT NOT NULL,
  files_total INTEGER NOT NULL, files_completed INTEGER NOT NULL DEFAULT 0,
  segments_indexed INTEGER NOT NULL DEFAULT 0, error TEXT
);
CREATE TABLE IF NOT EXISTS libraries(
  id TEXT PRIMARY KEY, name TEXT NOT NULL, canonical_root TEXT NOT NULL UNIQUE,
  is_online INTEGER NOT NULL DEFAULT 1, created_utc TEXT NOT NULL
);
CREATE TABLE IF NOT EXISTS assets(
  id TEXT PRIMARY KEY, library_id TEXT NOT NULL REFERENCES libraries(id), canonical_path TEXT NOT NULL,
  size_bytes INTEGER NOT NULL, modified_utc TEXT NOT NULL, fast_fingerprint TEXT NOT NULL, full_sha256 TEXT,
  media_kind TEXT NOT NULL, duration_ms INTEGER, width INTEGER, height INTEGER, fps REAL, codec TEXT,
  status TEXT NOT NULL, error_code TEXT, UNIQUE(library_id, canonical_path)
);
CREATE INDEX IF NOT EXISTS ix_assets_fingerprint ON assets(size_bytes, fast_fingerprint);
CREATE TABLE IF NOT EXISTS media_assets(
  media_path TEXT PRIMARY KEY, asset_id TEXT NOT NULL UNIQUE, library_root TEXT NOT NULL,
  name TEXT NOT NULL, extension TEXT NOT NULL, size_bytes INTEGER NOT NULL,
  modified_utc TEXT NOT NULL, duration_ms INTEGER NOT NULL, width INTEGER, height INTEGER,
  codec TEXT, status TEXT NOT NULL, thumbnail_path TEXT, error TEXT, last_indexed_utc TEXT,
  visual_version TEXT, speech_version TEXT, ocr_version TEXT
);
CREATE INDEX IF NOT EXISTS ix_media_assets_library ON media_assets(library_root, name);
CREATE TABLE IF NOT EXISTS visual_segments(
  id TEXT PRIMARY KEY, asset_id TEXT NOT NULL REFERENCES assets(id) ON DELETE CASCADE,
  start_ms INTEGER NOT NULL, end_ms INTEGER NOT NULL, representative_json TEXT NOT NULL,
  thumbnail_path TEXT NOT NULL, algorithm_version INTEGER NOT NULL, model_version TEXT NOT NULL,
  UNIQUE(asset_id,start_ms,end_ms,algorithm_version,model_version)
);
CREATE TABLE IF NOT EXISTS transcript_segments(
  id TEXT PRIMARY KEY, asset_id TEXT NOT NULL REFERENCES assets(id) ON DELETE CASCADE,
  start_ms INTEGER NOT NULL, end_ms INTEGER NOT NULL, original_text TEXT NOT NULL,
  normalized_text TEXT NOT NULL, words_json TEXT NOT NULL, model_version TEXT NOT NULL
);
CREATE TABLE IF NOT EXISTS speech_windows(
  id TEXT PRIMARY KEY, asset_id TEXT NOT NULL REFERENCES assets(id) ON DELETE CASCADE,
  start_ms INTEGER NOT NULL, end_ms INTEGER NOT NULL, original_segment_ids_json TEXT NOT NULL,
  normalized_text TEXT NOT NULL, algorithm_version INTEGER NOT NULL, model_version TEXT NOT NULL
);
CREATE TABLE IF NOT EXISTS ocr_observations(
  id TEXT PRIMARY KEY, asset_id TEXT NOT NULL REFERENCES assets(id) ON DELETE CASCADE,
  timestamp_ms INTEGER NOT NULL, original_text TEXT NOT NULL, normalized_text TEXT NOT NULL,
  language TEXT, confidence REAL NOT NULL, boxes_json TEXT NOT NULL,
  algorithm_version INTEGER NOT NULL, model_version TEXT NOT NULL
);
CREATE TABLE IF NOT EXISTS vector_outbox(
  id INTEGER PRIMARY KEY AUTOINCREMENT, operation TEXT NOT NULL, collection TEXT NOT NULL,
  point_id TEXT NOT NULL, payload_json TEXT NOT NULL, created_utc TEXT NOT NULL,
  completed_utc TEXT, UNIQUE(operation,collection,point_id)
);
CREATE VIRTUAL TABLE IF NOT EXISTS search_fts USING fts5(
  asset_id UNINDEXED, filename, transcript, ocr, pinyin, initials, fuzzy,
  tokenize='unicode61 remove_diacritics 2'
);
INSERT OR IGNORE INTO schema_migrations(version, applied_utc)
VALUES(1, strftime('%Y-%m-%dT%H:%M:%fZ','now'));
