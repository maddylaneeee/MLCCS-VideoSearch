from __future__ import annotations

import json
import os
import sqlite3
from typing import Any

import requests


class QdrantServer:
    """Minimal private-Qdrant REST client. Secrets and user content are never logged."""

    def __init__(self, url: str | None = None, api_key: str | None = None) -> None:
        self.url = (url or os.environ.get("MLCCS_QDRANT_URL", "")).rstrip("/")
        self.api_key = api_key or os.environ.get("MLCCS_QDRANT_API_KEY", "")
        if not self.url or not self.api_key:
            raise RuntimeError("Private Qdrant connection was not supplied by the Agent")
        self.session = requests.Session()
        self.session.headers.update({"api-key": self.api_key, "Content-Type": "application/json"})

    def _request(self, method: str, path: str, body: dict[str, Any] | None = None) -> dict[str, Any]:
        response = self.session.request(method, f"{self.url}{path}", json=body, timeout=30)
        if response.status_code >= 400:
            raise RuntimeError(f"Qdrant request failed with HTTP {response.status_code}")
        return response.json() if response.content else {}

    def ensure_collection(self, name: str, dimension: int) -> None:
        response = self.session.get(f"{self.url}/collections/{name}", timeout=10)
        if response.status_code == 200:
            actual = int(response.json()["result"]["config"]["params"]["vectors"]["size"])
            if actual != dimension:
                raise RuntimeError(f"Qdrant collection {name} has incompatible dimension")
            return
        if response.status_code != 404:
            raise RuntimeError(f"Qdrant collection check failed with HTTP {response.status_code}")
        self._request("PUT", f"/collections/{name}", {
            "vectors": {"size": dimension, "distance": "Cosine"},
            "on_disk_payload": True,
        })

    def collection_exists(self, name: str) -> bool:
        response = self.session.get(f"{self.url}/collections/{name}", timeout=10)
        if response.status_code not in (200, 404):
            raise RuntimeError(f"Qdrant collection check failed with HTTP {response.status_code}")
        return response.status_code == 200

    def reconcile_outbox(self, connection: sqlite3.Connection) -> int:
        """Rebuild a missing collection deterministically from the authoritative SQLite outbox."""
        collections = [row[0] for row in connection.execute(
            "SELECT DISTINCT collection FROM vector_outbox WHERE operation='upsert'"
        )]
        reset = 0
        for collection in collections:
            if self.collection_exists(collection):
                continue
            cursor = connection.execute(
                "UPDATE vector_outbox SET completed_utc=NULL WHERE collection=? AND operation='upsert'",
                (collection,),
            )
            reset += cursor.rowcount
        connection.commit()
        return reset

    def query(self, collection: str, vector: list[float], limit: int,
              query_filter: dict[str, Any] | None = None) -> list[dict[str, Any]]:
        body: dict[str, Any] = {"query": vector, "limit": limit, "with_payload": True,
                                "with_vector": False}
        if query_filter:
            body["filter"] = query_filter
        return list(self._request("POST", f"/collections/{collection}/points/query", body).get("result", {}).get("points", []))

    def apply_outbox(self, connection: sqlite3.Connection, maximum: int = 256) -> int:
        rows = connection.execute(
            """SELECT id,operation,collection,point_id,payload_json FROM vector_outbox
               WHERE completed_utc IS NULL ORDER BY id LIMIT ?""", (maximum,)
        ).fetchall()
        applied = 0
        for outbox_id, operation, collection, point_id, document in rows:
            value = json.loads(document)
            if operation == "upsert":
                vector = value.pop("vector")
                # Only opaque IDs, times, and model/algorithm versions are allowed in Qdrant.
                allowed = {"assetId", "segmentId", "startMs", "endMs", "representativeMs",
                           "semanticWindowId", "ocrId", "timestampMs",
                           "algorithmVersion", "modelVersion"}
                payload = {key: item for key, item in value.items() if key in allowed}
                self.ensure_collection(collection, len(vector))
                self._request("PUT", f"/collections/{collection}/points?wait=true", {
                    "points": [{"id": point_id, "vector": vector, "payload": payload}]
                })
            elif operation == "delete":
                self._request("POST", f"/collections/{collection}/points/delete?wait=true",
                              {"points": [point_id]})
            else:
                raise RuntimeError("Unknown vector outbox operation")
            connection.execute(
                "UPDATE vector_outbox SET completed_utc=strftime('%Y-%m-%dT%H:%M:%fZ','now') WHERE id=?",
                (outbox_id,),
            )
            connection.commit()
            applied += 1
        return applied

    def replay_all(self, connection: sqlite3.Connection) -> int:
        total = 0
        while True:
            count = self.apply_outbox(connection)
            total += count
            if count == 0:
                return total

    def close(self) -> None:
        self.session.close()
