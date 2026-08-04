from __future__ import annotations

import argparse
from difflib import SequenceMatcher
import json
from pathlib import Path
import re
import sqlite3
import sys
from typing import Any

import numpy as np

from .vector_store import QdrantServer


CLIP_MODEL_NAME = "xlm-roberta-base-ViT-B-32"


def _phonetic(text: str) -> str:
    try:
        from pypinyin import lazy_pinyin
        return "".join(lazy_pinyin(text)).casefold().replace(" ", "")
    except ImportError:
        return text.casefold().replace(" ", "")


def _phonetic_match(query: str, candidate: str, level: str) -> float:
    if level == "off":
        return 0.0
    left, right = _phonetic(query), _phonetic(candidate)
    if not left or not right:
        return 0.0
    if left in right:
        return 1.0
    threshold = {"low": 0.90, "medium": 0.80, "high": 0.70}.get(level, 0.80)
    score = SequenceMatcher(None, left, right).ratio()
    return score if score >= threshold else 0.0


def _fts_expression(query: str, columns: list[str]) -> str:
    tokens = [token for token in re.split(r"\s+", query.strip()) if token]
    if not tokens:
        return ""
    # FTS5's Unicode tokenizer commonly stores an unspaced Chinese phrase as
    # one token. Prefix terms allow a natural partial query such as “相同” to
    # match “相同名称” while preserving column scoping and parameter binding.
    quoted = " AND ".join('"' + token.replace('"', '""') + '"*' for token in tokens)
    return " OR ".join(f"{column}:({quoted})" for column in columns)


class SearchEngine:
    def __init__(self, database: Path, models_root: Path, text_models_root: Path | None = None,
                 qdrant: QdrantServer | None = None) -> None:
        self.database = database
        self.models_root = models_root
        self.text_models_root = text_models_root or models_root
        self.torch = None
        self.clip_model = None
        self.clip_tokenizer = None
        self.bge_model = None
        self.qdrant = qdrant

    def _qdrant(self) -> QdrantServer:
        if self.qdrant is None:
            self.qdrant = QdrantServer()
        return self.qdrant

    def _ensure_cuda(self) -> None:
        if self.torch is None:
            import torch
            self.torch = torch
        if not self.torch.cuda.is_available():
            raise RuntimeError("CAPABILITY_UNSUPPORTED_HARDWARE: CUDA 12.8 runtime is unavailable")
        if self.torch.cuda.get_device_properties(0).total_memory < 4 * 1024**3:
            raise RuntimeError("CAPABILITY_UNSUPPORTED_HARDWARE: at least 4 GB NVIDIA VRAM is required")

    def _ensure_visual_model(self) -> None:
        if self.clip_model is not None:
            return
        self._ensure_cuda()
        import open_clip
        from transformers import PreTrainedTokenizerFast
        checkpoint = self.models_root / "openclip-standard" / "open_clip_pytorch_model.bin"
        tokenizer_file = self.models_root / "openclip-standard" / "tokenizer.json"
        if not checkpoint.is_file() or not tokenizer_file.is_file():
            raise RuntimeError("必装的 OpenCLIP Standard 模型不完整")
        self.clip_model, _, _ = open_clip.create_model_and_transforms(
            CLIP_MODEL_NAME, pretrained=str(checkpoint), device="cuda:0")
        self.clip_model.half().eval()
        self.clip_tokenizer = PreTrainedTokenizerFast(
            tokenizer_file=str(tokenizer_file), bos_token="<s>", eos_token="</s>",
            unk_token="<unk>", pad_token="<pad>", mask_token="<mask>")

    def _visual_vector(self, query: str) -> list[float]:
        self._ensure_visual_model()
        ids = self.clip_tokenizer([query], padding="max_length", truncation=True,
                                  max_length=77, return_tensors="pt")["input_ids"].to("cuda:0")
        with self.torch.inference_mode(), self.torch.autocast("cuda", dtype=self.torch.float16):
            vector = self.clip_model.encode_text(ids, normalize=True).float().cpu().numpy()[0]
        return vector.tolist()

    def _text_vector(self, query: str) -> list[float]:
        if self.bge_model is None:
            self._ensure_cuda()
            from sentence_transformers import SentenceTransformer
            model_root = self.text_models_root / "bge-small"
            if not model_root.is_dir():
                raise RuntimeError("必装的 BGE Small 中文模型不完整")
            self.bge_model = SentenceTransformer(str(model_root), device="cuda")
        return self.bge_model.encode(query, normalize_embeddings=True).tolist()

    @staticmethod
    def _asset(connection: sqlite3.Connection, asset_id: str) -> dict[str, Any] | None:
        row = connection.execute("""SELECT media_path,library_root,name,extension,size_bytes,modified_utc,
                   duration_ms,width,height,codec,status,thumbnail_path FROM media_assets WHERE asset_id=?""",
                                 (asset_id,)).fetchone()
        if row is None:
            return None
        keys = ("path", "library", "name", "extension", "sizeBytes", "modifiedUtc", "durationMs",
                "width", "height", "codec", "status", "thumbnail")
        return dict(zip(keys, row, strict=True))

    @staticmethod
    def _nearest_thumbnail(connection: sqlite3.Connection, asset_id: str, timestamp_ms: int) -> str | None:
        row = connection.execute("""SELECT thumbnail_path FROM visual_segments WHERE asset_id=?
            ORDER BY ABS(((start_ms+end_ms)/2)-?) LIMIT 1""", (asset_id, timestamp_ms)).fetchone()
        return row[0] if row else None

    @staticmethod
    def _scope(connection: sqlite3.Connection, filters: dict[str, Any]) -> list[str]:
        clauses = ["status!='Missing'"]
        values: list[Any] = []
        mapping = (("libraries", "library_root"), ("extensions", "extension"), ("statuses", "status"))
        for key, column in mapping:
            selected = [str(item) for item in filters.get(key, []) if str(item)]
            if selected:
                clauses.append(f"{column} IN ({','.join('?' for _ in selected)})")
                values.extend(selected)
        ranges = (("durationMinMs", "duration_ms", ">="), ("durationMaxMs", "duration_ms", "<="),
                  ("modifiedFromUtc", "modified_utc", ">="), ("modifiedToUtc", "modified_utc", "<="))
        for key, column, operator in ranges:
            if filters.get(key) is not None:
                clauses.append(f"{column}{operator}?")
                values.append(filters[key])
        return [row[0] for row in connection.execute(
            f"SELECT asset_id FROM media_assets WHERE {' AND '.join(clauses)}", values)]

    def search(self, query: str, source: str = "all", limit: int = 60,
               phonetic_level: str = "medium", filters: dict[str, Any] | None = None,
               sort: str = "relevance", library: str = "") -> list[dict[str, Any]]:
        if not query.strip():
            return []
        filters = dict(filters or {})
        if library and not filters.get("libraries"):
            filters["libraries"] = [library]
        connection = sqlite3.connect(self.database)
        connection.row_factory = sqlite3.Row
        allowed = self._scope(connection, filters)
        if not allowed:
            connection.close()
            return []
        allowed_set = set(allowed)
        ranked: dict[tuple[str, int], dict[str, Any]] = {}

        def contribute(asset_id: str, timestamp_ms: int, source_name: str, rank: int,
                       explanation: str, raw_score: float = 0.0) -> None:
            if asset_id not in allowed_set:
                return
            asset = self._asset(connection, asset_id)
            if asset is None:
                return
            key = (asset_id, timestamp_ms)
            contribution = 1.0 / (60 + rank)
            item = ranked.setdefault(key, {**asset, "assetId": asset_id, "timestampMs": timestamp_ms,
                "startMs": max(0, timestamp_ms - 2000), "endMs": timestamp_ms + 2000,
                "score": 0.0, "sources": [], "explanations": [], "scoreContributions": []})
            item["score"] += contribution
            if source_name not in item["sources"]:
                item["sources"].append(source_name)
            item["explanations"].append(explanation)
            item["scoreContributions"].append({"source": source_name, "rank": rank,
                                                "rrf": contribution, "rawScore": raw_score})
            item["thumbnail"] = item.get("thumbnail") or self._nearest_thumbnail(connection, asset_id, timestamp_ms)

        columns = []
        if source in ("all", "filename"):
            columns.append("filename")
        if source in ("all", "speech"):
            columns.append("transcript")
        if source in ("all", "ocr"):
            columns.append("ocr")
        fts_sources = {
            "filename": ("filename-fts", "SQLite FTS5 文件名匹配"),
            "transcript": ("speech-fts", "SQLite FTS5 语音原文匹配"),
            "ocr": ("ocr-fts", "SQLite FTS5 OCR 原文匹配"),
        }
        for column in columns:
            expression = _fts_expression(query, [column])
            rows = connection.execute("""SELECT asset_id,bm25(search_fts,0,3,1.5,1,0.8,0.6,0.2) AS score
                FROM search_fts WHERE search_fts MATCH ? ORDER BY score LIMIT ?""", (expression, limit * 5)).fetchall()
            source_name, explanation = fts_sources[column]
            for rank, row in enumerate((row for row in rows if row["asset_id"] in allowed_set), 1):
                contribute(row["asset_id"], 0, source_name, rank, explanation, -float(row["score"]))

        if phonetic_level != "off" and source in ("all", "filename", "speech", "ocr"):
            candidates = connection.execute("SELECT asset_id,filename||' '||transcript||' '||ocr FROM search_fts").fetchall()
            matched = sorted(((score, row["asset_id"]) for row in candidates if row["asset_id"] in allowed_set
                              if (score := _phonetic_match(query, row[1], phonetic_level)) > 0), reverse=True)
            for rank, (score, asset_id) in enumerate(matched[:limit], 1):
                contribute(asset_id, 0, "phonetic", rank, f"拼音/音近匹配 {score:.0%}", score)

        query_filter = {"must": [{"key": "assetId", "match": {"any": allowed}}]}
        qdrant = self._qdrant() if source in ("all", "visual", "speech", "ocr") else None
        if source in ("all", "visual") and qdrant.collection_exists("visual_v1"):
            for rank, hit in enumerate(qdrant.query("visual_v1", self._visual_vector(query), limit * 3, query_filter), 1):
                payload = hit.get("payload", {})
                timestamp = int(payload.get("representativeMs", payload.get("startMs", 0)))
                contribute(str(payload.get("assetId", "")), timestamp, "visual", rank,
                           f"OpenCLIP 画面语义相似度 {float(hit.get('score', 0)):.1%}", float(hit.get("score", 0)))
        if source in ("all", "speech", "ocr"):
            collections = (["speech_v1", "ocr_v1"] if source == "all" else
                           ["speech_v1"] if source == "speech" else ["ocr_v1"])
            collections = [collection for collection in collections if qdrant.collection_exists(collection)]
            vector = self._text_vector(query) if collections else []
            for collection in collections:
                source_name = "speech-semantic" if collection == "speech_v1" else "ocr-semantic"
                for rank, hit in enumerate(qdrant.query(collection, vector, limit * 3, query_filter), 1):
                    payload = hit.get("payload", {})
                    timestamp = int(payload.get("timestampMs", (int(payload.get("startMs", 0)) + int(payload.get("endMs", 0))) // 2))
                    contribute(str(payload.get("assetId", "")), timestamp, source_name, rank,
                               f"BGE 文本语义相似度 {float(hit.get('score', 0)):.1%}", float(hit.get("score", 0)))

        items = list(ranked.values())
        if sort == "modified-desc":
            items.sort(key=lambda item: (item.get("modifiedUtc") or "", item["score"]), reverse=True)
        elif sort == "filename":
            items.sort(key=lambda item: (str(item.get("name", "")).casefold(), -item["score"]))
        else:
            items.sort(key=lambda item: item["score"], reverse=True)
        connection.close()
        for item in items[:limit]:
            item["source"] = " / ".join(item.pop("sources"))
            item["explanation"] = "；".join(dict.fromkeys(item.pop("explanations")))
        return items[:limit]

    def close(self) -> None:
        if self.qdrant is not None:
            self.qdrant.close()


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--database", type=Path, required=True)
    parser.add_argument("--models-root", type=Path, required=True)
    parser.add_argument("--text-models-root", type=Path, required=True)
    args = parser.parse_args()
    engine = SearchEngine(args.database, args.models_root, args.text_models_root)
    print(json.dumps({"ready": True}, ensure_ascii=False), flush=True)
    try:
        for line in sys.stdin:
            try:
                request = json.loads(line)
                results = engine.search(str(request.get("query", "")), str(request.get("source", "all")),
                                        int(request.get("limit", 60)), str(request.get("phoneticLevel", "medium")),
                                        request.get("filters") if isinstance(request.get("filters"), dict) else None,
                                        str(request.get("sort", "relevance")), str(request.get("library", "")))
                print(json.dumps({"ok": True, "results": results}, ensure_ascii=False), flush=True)
            except Exception as error:
                print(json.dumps({"ok": False, "error": str(error)}, ensure_ascii=False), flush=True)
    finally:
        engine.close()
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
