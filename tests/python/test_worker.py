from __future__ import annotations

import io
import json
from pathlib import Path
import struct
import sys
import tempfile
import unittest
from unittest.mock import patch
import numpy as np

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / "worker"))

from mlccs_worker.capabilities import Capabilities, recommend_whisper, require_speech
from mlccs_worker.contracts import Envelope, WorkerError
from mlccs_worker.pipe import MAXIMUM_FRAME_BYTES, read_frame, write_frame
from mlccs_worker.batch_index import _database
from mlccs_worker.search_server import SearchEngine, _phonetic_match


class WorkerTests(unittest.TestCase):
    def test_framing_round_trip_and_protocol_validation(self):
        stream = io.BytesIO()
        request = Envelope.create("worker.health", {"hello": "世界"})
        write_frame(stream, request)
        stream.seek(0)
        self.assertEqual(request, read_frame(stream))
        bad = request.to_dict(); bad["protocolVersion"] = "2.0"
        body = json.dumps(bad).encode()
        with self.assertRaises(WorkerError):
            read_frame(io.BytesIO(struct.pack("<I", len(body)) + body))

    def test_oversized_frame_is_rejected(self):
        with self.assertRaises(WorkerError):
            read_frame(io.BytesIO(struct.pack("<I", MAXIMUM_FRAME_BYTES + 1)))

    def test_speech_requires_cuda_and_recommendation_follows_vram(self):
        capability = Capabilities("Windows", "CPU", 8, 16, 100, None, False, None, 0, False)
        with self.assertRaisesRegex(WorkerError, "CUDA"):
            require_speech(capability)
        self.assertEqual("small", recommend_whisper(3 * 1024**3)["model"])
        self.assertEqual("medium", recommend_whisper(5 * 1024**3)["model"])
        self.assertEqual("large-v3-turbo", recommend_whisper(8 * 1024**3)["model"])
        self.assertEqual("float16", recommend_whisper(12 * 1024**3)["compute_type"])

    def test_live_catalog_schema_supports_preview_and_timed_text(self):
        with tempfile.TemporaryDirectory() as directory:
            connection = _database(Path(directory) / "catalog.db")
            tables = {row[0] for row in connection.execute(
                "SELECT name FROM sqlite_master WHERE type='table'"
            )}
            self.assertTrue({"media_assets", "real_visual_frames",
                             "transcript_segments_live", "ocr_observations_live"} <= tables)
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
            for library, path in (("D:\\A", "D:\\A\\相同名称.mp4"),
                                  ("D:\\B", "D:\\B\\相同名称.mp4")):
                connection.execute(
                    """INSERT INTO media_assets(
                         media_path,library_root,name,extension,size_bytes,modified_utc,
                         duration_ms,status)
                       VALUES(?,?,?,?,?,?,?,?)""",
                    (path, library, "相同名称.mp4", ".mp4", 1, "2026-01-01T00:00:00Z",
                     1000, "Indexed")
                )
                suffix = library[-1]
                connection.execute(
                    """INSERT INTO real_visual_frames(
                         id,run_id,media_path,timestamp_ms,embedding_f16,thumbnail_path,
                         model_version,device) VALUES(?,?,?,?,?,?,?,?)""",
                    (f"visual-{suffix}", "run", path, 100,
                     np.array([1.0, 0.0], dtype=np.float16).tobytes(), "",
                     "openclip-standard-506d40eb", "cpu")
                )
                connection.execute(
                    """INSERT INTO transcript_segments_live(
                         id,media_path,start_ms,end_ms,text,normalized_text,words_json,
                         model_version) VALUES(?,?,?,?,?,?,?,?)""",
                    (f"speech-{suffix}", path, 200, 400, "目标台词", "目标台词", "[]", "test")
                )
                connection.execute(
                    """INSERT INTO ocr_observations_live(
                         id,media_path,timestamp_ms,text,normalized_text,confidence,boxes_json,
                         model_version) VALUES(?,?,?,?,?,?,?,?)""",
                    (f"ocr-{suffix}", path, 500, "目标字幕", "目标字幕", 0.9, "[]", "test")
                )
            connection.commit()
            connection.close()
            # Non-visual search must remain available without loading the large
            # CLIP model; the visual vector is stubbed only for that source.
            engine = SearchEngine(database, Path(directory) / "models-not-installed")
            for query, source in (("相同", "filename"), ("画面", "visual"),
                                  ("目标台词", "speech"), ("目标字幕", "ocr")):
                if source == "visual":
                    engine._text_vector = lambda _: np.array([1.0, 0.0], dtype=np.float32)
                results = engine.search(query, source, 60, "off", "D:\\B")
                self.assertEqual(["D:\\B\\相同名称.mp4"],
                                 list(dict.fromkeys(item["path"] for item in results)),
                                 source)


if __name__ == "__main__":
    unittest.main()
