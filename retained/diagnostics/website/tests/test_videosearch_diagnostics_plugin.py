import hashlib
import io
import json
import sys
import tempfile
import time
import unittest
import zipfile
from pathlib import Path
from unittest.mock import patch

BACKEND = Path(__file__).resolve().parents[1] / "backend"
if not BACKEND.exists():
    BACKEND = Path(__file__).resolve().parents[1] / "source"
if str(BACKEND) not in sys.path:
    sys.path.insert(0, str(BACKEND))

try:
    from plugins import videosearch_diagnostics_plugin as plugin
except ModuleNotFoundError:
    import videosearch_diagnostics_plugin as plugin


def make_zip(name="logs/app.log", content=b"safe diagnostic"):
    output = io.BytesIO()
    with zipfile.ZipFile(output, "w", zipfile.ZIP_DEFLATED) as archive:
        archive.writestr(name, content)
    return output.getvalue()


class DiagnosticsTests(unittest.TestCase):
    def setUp(self):
        plugin._RATE_BUCKETS.clear()
        plugin._LAST_CLEANUP = 0.0

    @staticmethod
    def payload(response):
        return json.loads(response["body"])

    def begin(self, data):
        request = {
            "method": "POST", "path": "/api/mlccs-videosearch/diagnostics/begin", "client_ip": "127.0.0.1",
            "body": json.dumps({
                "size": len(data), "sha256": hashlib.sha256(data).hexdigest(),
                "total_chunks": (len(data) + plugin.MAX_CHUNK_BYTES - 1) // plugin.MAX_CHUNK_BYTES,
                "installation_hash": "a" * 64,
            }),
        }
        return self.payload(plugin.handle(request))

    def upload(self, data, begin):
        for index in range(begin.get("total_chunks", (len(data) + plugin.MAX_CHUNK_BYTES - 1) // plugin.MAX_CHUNK_BYTES)):
            chunk = data[index * plugin.MAX_CHUNK_BYTES:(index + 1) * plugin.MAX_CHUNK_BYTES]
            response = plugin.handle({
                "method": "POST", "path": "/api/mlccs-videosearch/diagnostics/chunk",
                "query": {"report_id": begin["report_id"], "index": str(index)},
                "headers": {"X-Upload-Token": begin["token"]}, "body": chunk,
            })
            self.assertEqual(200, response["status"])

    def storage(self, root):
        return patch.multiple(plugin, DIAGNOSTICS_ROOT=root, TEMP_ROOT=root / ".uploads", EVENT_ROOT=root / "events")

    def test_complete_happy_path_and_duplicate_rejected(self):
        data = make_zip()
        with tempfile.TemporaryDirectory() as temporary, self.storage(Path(temporary)), patch.object(plugin, "_defender_scan", return_value={"clean": True, "status": "clean"}):
            begin = self.begin(data)
            self.upload(data, begin)
            request = {"method": "POST", "path": "/api/mlccs-videosearch/diagnostics/complete", "headers": {"X-Upload-Token": begin["token"]}, "body": json.dumps({"report_id": begin["report_id"], "user_note": "clicked search"})}
            response = plugin.handle(request)
            self.assertEqual(200, response["status"])
            final = next(Path(temporary).glob("20*/*/*/vs-*"))
            self.assertTrue((final / "report.zip").exists())
            self.assertEqual("clicked search", (final / "user-note.txt").read_text())
            self.assertEqual(409, plugin.handle(request)["status"])

    def test_wrong_hash_is_rejected(self):
        data = make_zip()
        with tempfile.TemporaryDirectory() as temporary, self.storage(Path(temporary)):
            begin = self.begin(data)
            self.upload(data, begin)
            transaction = Path(temporary) / ".uploads" / begin["report_id"] / "transaction.json"
            metadata = json.loads(transaction.read_text())
            metadata["sha256"] = "0" * 64
            transaction.write_text(json.dumps(metadata))
            response = plugin.handle({"method": "POST", "path": "/complete", "headers": {"X-Upload-Token": begin["token"]}, "body": json.dumps({"report_id": begin["report_id"]})})
            self.assertEqual("HASH_MISMATCH", self.payload(response)["code"])

    def test_path_traversal_and_executable_are_rejected(self):
        for name, code in (("../escape.log", "ZIP_PATH_TRAVERSAL"), ("payload.exe", "ZIP_FILE_TYPE_NOT_ALLOWED")):
            data = make_zip(name)
            with tempfile.TemporaryDirectory() as temporary, self.storage(Path(temporary)):
                begin = self.begin(data)
                self.upload(data, begin)
                response = plugin.handle({"method": "POST", "path": "/complete", "headers": {"X-Upload-Token": begin["token"]}, "body": json.dumps({"report_id": begin["report_id"]})})
                self.assertEqual(code, self.payload(response)["code"])

    def test_zip_bomb_ratio_is_rejected(self):
        data = make_zip(content=b"0" * (2 * 1024 * 1024))
        with tempfile.TemporaryDirectory() as temporary, self.storage(Path(temporary)):
            begin = self.begin(data)
            self.upload(data, begin)
            response = plugin.handle({"method": "POST", "path": "/complete", "headers": {"X-Upload-Token": begin["token"]}, "body": json.dumps({"report_id": begin["report_id"]})})
            self.assertEqual("ZIP_COMPRESSION_RATIO_EXCEEDED", self.payload(response)["code"])

    def test_symlink_zip_entry_is_rejected(self):
        output = io.BytesIO()
        with zipfile.ZipFile(output, "w") as archive:
            item = zipfile.ZipInfo("logs/link.log")
            item.create_system = 3
            item.external_attr = 0o120777 << 16
            archive.writestr(item, "target.log")
        data = output.getvalue()
        with tempfile.TemporaryDirectory() as temporary, self.storage(Path(temporary)):
            begin = self.begin(data)
            self.upload(data, begin)
            response = plugin.handle({"method": "POST", "path": "/complete", "headers": {"X-Upload-Token": begin["token"]}, "body": json.dumps({"report_id": begin["report_id"]})})
            self.assertEqual("ZIP_SYMLINK_NOT_ALLOWED", self.payload(response)["code"])

    def test_completed_reports_expire_after_180_days(self):
        with tempfile.TemporaryDirectory() as temporary, self.storage(Path(temporary)):
            old = Path(temporary) / "2025" / "01" / "02" / "vs-20250102-aaaaaaaaaaaaaaaaaaaaaaaa"
            recent = Path(temporary) / "2026" / "07" / "28" / "vs-20260728-bbbbbbbbbbbbbbbbbbbbbbbb"
            old.mkdir(parents=True)
            recent.mkdir(parents=True)
            stale_time = time.time() - (plugin.REPORT_RETENTION_DAYS + 1) * 24 * 60 * 60
            import os
            os.utime(old, (stale_time, stale_time))
            plugin._cleanup_expired_reports()
            self.assertFalse(old.exists())
            self.assertTrue(recent.exists())

    def test_event_rejects_query_and_accepts_allowlist(self):
        with tempfile.TemporaryDirectory() as temporary, self.storage(Path(temporary)):
            bad = plugin.handle({"method": "POST", "path": "/event", "body": json.dumps({"installation_hash": "a" * 64, "query": "secret"})})
            self.assertEqual(400, bad["status"])
            good = plugin.handle({"method": "POST", "path": "/event", "body": json.dumps({"installation_hash": "b" * 64, "event": "index.completed", "app_version": "0.1.0"})})
            self.assertEqual(202, good["status"])

    def test_production_route_has_required_sandbox_permissions(self):
        routes_path = BACKEND / "config" / "routes.json"
        if routes_path.exists():
            config = json.loads(routes_path.read_text(encoding="utf-8"))
            route = next(item for item in config["routes"] if item.get("plugin") == "videosearch-diagnostics:v1")
            registered = next(item for item in config["plugins"] if item.get("id") == "videosearch-diagnostics:v1")
        else:
            fragment = json.loads((Path(__file__).resolve().parents[1] / "routes-fragment.json").read_text(encoding="utf-8"))
            route, registered = fragment["route"], fragment["plugin"]
        self.assertEqual("prefix", route["path_match"])
        self.assertTrue(route["permissions"]["process_spawn"])
        self.assertIn("D:/lixinchenca-videosearch", route["permissions"]["allow_paths"])
        self.assertGreaterEqual(registered["max_retries"], 1)


if __name__ == "__main__":
    unittest.main()
