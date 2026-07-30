from __future__ import annotations

from pathlib import Path
from typing import Any


class EdgeCollection:
    def __init__(self, root: Path, vector_name: str, dimension: int) -> None:
        from qdrant_edge import Distance, EdgeConfig, EdgeShard, EdgeVectorParams
        root.mkdir(parents=True, exist_ok=True)
        self.vector_name = vector_name
        try:
            self.shard = EdgeShard.load(str(root), None)
        except Exception:
            self.shard = EdgeShard.create(str(root), EdgeConfig(vectors={vector_name: EdgeVectorParams(size=dimension, distance=Distance.Cosine)}))

    def upsert(self, point_id: int, vector: list[float], payload: dict[str, Any]) -> None:
        from qdrant_edge import Point, UpdateOperation
        self.shard.update(UpdateOperation.upsert_points([Point(id=point_id, vector={self.vector_name: vector}, payload=payload)]))

    def query(self, vector: list[float], limit: int = 50) -> Any:
        from qdrant_edge import Query, QueryRequest
        return self.shard.query(QueryRequest(query=Query.Nearest(vector, using=self.vector_name), limit=limit, with_vector=False, with_payload=True))

    def close(self) -> None:
        self.shard.close()

