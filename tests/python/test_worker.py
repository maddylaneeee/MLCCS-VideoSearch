from __future__ import annotations

from pathlib import Path
import json
import os
import runpy
import shutil
import sys
import tempfile
import unittest
from unittest.mock import patch
import numpy as np

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / "worker"))

from mlccs_worker.capabilities import Capabilities, recommend_whisper, require_speech, require_v1_hardware
from mlccs_worker.contracts import WorkerError
from mlccs_worker.batch_index import _database, _remove_missing_assets, _segment_sample_ranges, _speech_window_groups
from mlccs_worker.search_server import SearchEngine, _phonetic_match
from mlccs_worker.model_download import _atomic as atomic_model_status


class WorkerTests(unittest.TestCase):
    def test_model_status_atomic_write_retries_windows_reader_contention(self):
        with tempfile.TemporaryDirectory() as directory:
            status = Path(directory) / "status.json"
            real_replace = os.replace
            attempts = 0

            def replace_after_contention(source, destination):
                nonlocal attempts
                attempts += 1
                if attempts < 3:
                    raise PermissionError("simulated Windows reader contention")
                real_replace(source, destination)

            with patch("mlccs_worker.model_download.os.replace", side_effect=replace_after_contention), \
                 patch("mlccs_worker.model_download.time.sleep"):
                atomic_model_status(status, {"status": "Downloading"})
            self.assertEqual(3, attempts)
            self.assertEqual("Downloading", json.loads(status.read_text())["status"])

    def test_runtime_sitecustomize_finds_source_and_installed_worker_layouts(self):
        bootstrap = ROOT / "worker" / "runtime-sitecustomize.py"
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            layouts = (
                (root / "source" / "worker" / "python", root / "source" / "worker"),
                (root / "install" / "components" / "runtime" / "hash", root / "install" / "current" / "worker"),
            )
            for runtime, worker in layouts:
                sitecustomize = runtime / "Lib" / "site-packages" / "sitecustomize.py"
                sitecustomize.parent.mkdir(parents=True)
                shutil.copy2(bootstrap, sitecustomize)
                package = worker / "mlccs_worker"
                package.mkdir(parents=True)
                (package / "__init__.py").write_text("", encoding="utf-8")
                original = list(sys.path)
                try:
                    runpy.run_path(str(sitecustomize))
                    self.assertEqual(str(worker.resolve()), sys.path[0])
                finally:
                    sys.path[:] = original

    def test_scene_analysis_enforces_two_to_eight_second_windows(self):
        samples = [(index * 500, 40.0 if index == 12 else 0.0) for index in range(41)]
        ranges = _segment_sample_ranges(samples)
        durations = [(samples[right][0] if right < len(samples) else 20_000) - samples[left][0]
                     for left, right in ranges]
        self.assertTrue(all(2_000 <= value <= 8_000 for value in durations))
        self.assertIn((0, 12), ranges)

    def test_speech_semantic_windows_are_eight_to_thirty_seconds(self):
        rows = [(f"s{index}", index * 2_000, (index + 1) * 2_000, f"text {index}")
                for index in range(17)]
        windows = _speech_window_groups(rows)
        self.assertTrue(all(8_000 <= end - start <= 30_000 for start, end, _ in windows))
        self.assertEqual([row[0] for row in rows], [row[0] for _, _, group in windows for row in group])

    def test_speech_requires_cuda_and_recommendation_follows_vram(self):
        capability = Capabilities("10.0.17763", "CPU", 8, 16, 100, None, False, None, 0,
                                  None, False, ("no NVIDIA",), False)
        with self.assertRaisesRegex(WorkerError, "CUDA"):
            require_speech(capability)
        with self.assertRaisesRegex(WorkerError, "NVIDIA"):
            require_v1_hardware(capability)
        self.assertEqual("small", recommend_whisper(3 * 1024**3)["model"])
        self.assertEqual("medium", recommend_whisper(5 * 1024**3)["model"])
        self.assertEqual("medium", recommend_whisper(8 * 1024**3)["model"])
        self.assertEqual("large-v3", recommend_whisper(12 * 1024**3)["model"])
        self.assertEqual("float16", recommend_whisper(12 * 1024**3)["compute_type"])

    def test_production_catalog_schema_uses_sqlite_outbox_without_live_tables(self):
        with tempfile.TemporaryDirectory() as directory:
            connection = _database(Path(directory) / "catalog.db")
            try:
                tables = {row[0] for row in connection.execute(
                    "SELECT name FROM sqlite_master WHERE type='table'"
                )}
                self.assertTrue({"media_assets", "visual_segments", "transcript_segments",
                                 "speech_windows", "ocr_observations", "vector_outbox", "search_fts"} <= tables)
                self.assertFalse({"real_visual_frames", "transcript_segments_live",
                                  "ocr_observations_live"} & tables)
            finally:
                connection.close()

    def test_missing_video_removal_queues_vector_deletes(self):
        with tempfile.TemporaryDirectory() as directory:
            connection = _database(Path(directory) / "catalog.db")
            connection.execute("INSERT INTO libraries VALUES('lib','L','D:\\L',1,'now')")
            connection.execute("""INSERT INTO assets(id,library_id,canonical_path,size_bytes,modified_utc,
                fast_fingerprint,media_kind,status) VALUES('asset','lib','D:\\L\\gone.mp4',1,'now','f','video','Indexed')""")
            connection.execute("""INSERT INTO media_assets(media_path,asset_id,library_root,name,extension,
                size_bytes,modified_utc,duration_ms,status) VALUES('D:\\L\\gone.mp4','asset','D:\\L','gone.mp4','.mp4',1,'now',1000,'Indexed')""")
            connection.execute("INSERT INTO visual_segments VALUES('point','asset',0,1000,'[]','x',1,'m')")
            connection.commit()
            self.assertEqual(1, _remove_missing_assets(connection, "D:\\L", set(), Path(directory) / "thumbs"))
            self.assertEqual(("delete", "visual_v1", "point"), connection.execute(
                "SELECT operation,collection,point_id FROM vector_outbox").fetchone())
            self.assertEqual(0, connection.execute("SELECT count(*) FROM media_assets").fetchone()[0])
            columns = {row[1] for row in connection.execute("PRAGMA table_info(media_assets)")}
            self.assertTrue({"width", "height", "codec", "thumbnail_path", "visual_version",
                             "speech_version", "ocr_version"} <= columns)
            connection.close()

    def test_chinese_phonetic_expansion_is_bounded_by_level(self):
        self.assertGreater(_phonetic_match("皇帝", "huangdi.mp4", "medium"), 0)
        self.assertEqual(0, _phonetic_match("皇帝", "完全无关.mp4", "low"))
        self.assertEqual(0, _phonetic_match("皇帝", "huangdi.mp4", "off"))

    def test_search_filters_by_exact_library_root(self):
        with tempfile.TemporaryDirectory() as directory:
            database = Path(directory) / "catalog.db"
            connection = _database(database)
            for suffix, library, path in (("A", "D:\\A", "D:\\A\\相同名称.mp4"),
                                          ("B", "D:\\B", "D:\\B\\相同名称.mp4")):
                library_id = f"library-{suffix}"
                asset_id = f"asset-{suffix}"
                connection.execute("INSERT INTO libraries VALUES(?,?,?,?,?)",
                                   (library_id, suffix, library, 1, "2026-01-01T00:00:00Z"))
                connection.execute("""INSERT INTO assets(id,library_id,canonical_path,size_bytes,modified_utc,
                    fast_fingerprint,media_kind,duration_ms,status) VALUES(?,?,?,?,?,?,?,?,?)""",
                    (asset_id, library_id, path, 1, "2026-01-01T00:00:00Z", suffix, "video", 1000, "Indexed"))
                connection.execute(
                    """INSERT INTO media_assets(
                         media_path,asset_id,library_root,name,extension,size_bytes,modified_utc,
                         duration_ms,status)
                       VALUES(?,?,?,?,?,?,?,?,?)""",
                    (path, asset_id, library, "相同名称.mp4", ".mp4", 1, "2026-01-01T00:00:00Z",
                     1000, "Indexed")
                )
                connection.execute("INSERT INTO search_fts VALUES(?,?,?,?,?,?,?)",
                                   (asset_id, "相同名称.mp4", "目标台词", "目标字幕", "", "", ""))
            connection.commit()
            connection.close()
            # Non-visual search must remain available without loading the large
            # CLIP model; the visual vector is stubbed only for that source.
            class EmptyQdrant:
                def collection_exists(self, _name): return False
                def close(self): pass
            engine = SearchEngine(database, Path(directory) / "models-not-installed", qdrant=EmptyQdrant())
            try:
                for query, source in (("相同", "filename"), ("目标台词", "speech"), ("目标字幕", "ocr")):
                    results = engine.search(query, source, 60, "off", {"libraries": ["D:\\B"]})
                    self.assertEqual(["D:\\B\\相同名称.mp4"],
                                     list(dict.fromkeys(item["path"] for item in results)),
                                     source)
            finally:
                engine.close()


if __name__ == "__main__":
    unittest.main()
