from __future__ import annotations

import argparse
from difflib import SequenceMatcher
import json
from pathlib import Path
import sqlite3
import sys
from typing import Any

import numpy as np


MODEL_NAME = "xlm-roberta-base-ViT-B-32"


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


def _asset(connection: sqlite3.Connection, path: str) -> dict[str, Any]:
    try:
        row = connection.execute(
            """SELECT media_path,library_root,name,extension,size_bytes,modified_utc,duration_ms,
                      width,height,codec,status,thumbnail_path
               FROM media_assets WHERE media_path=?""", (path,)
        ).fetchone()
    except sqlite3.OperationalError:
        row = None
    if row is None:
        return {"path": path, "name": Path(path).name, "library": str(Path(path).parent)}
    keys = ("path", "library", "name", "extension", "sizeBytes", "modifiedUtc", "durationMs",
            "width", "height", "codec", "status", "thumbnail")
    return dict(zip(keys, row, strict=True))


def _nearest_thumbnail(connection: sqlite3.Connection, path: str, timestamp_ms: int) -> str | None:
    row = connection.execute(
        """SELECT thumbnail_path FROM real_visual_frames WHERE media_path=?
           ORDER BY ABS(timestamp_ms-?) LIMIT 1""", (path, timestamp_ms)
    ).fetchone()
    return row[0] if row else None


class SearchEngine:
    def __init__(self, database: Path, models_root: Path) -> None:
        self.database = database
        self.models_root = models_root
        self.torch = None
        self.device = ""
        self.model = None
        self.tokenizer = None

    def _ensure_visual_model(self) -> None:
        if self.model is not None:
            return
        import open_clip
        import torch
        from transformers import PreTrainedTokenizerFast

        checkpoint = self.models_root / "openclip-standard" / "open_clip_pytorch_model.bin"
        tokenizer_file = self.models_root / "openclip-standard" / "tokenizer.json"
        if not checkpoint.is_file() or not tokenizer_file.is_file():
            raise RuntimeError("标准视觉模型或本地分词器不完整")
        self.torch = torch
        self.device = "cuda:0" if torch.cuda.is_available() else "cpu"
        self.model, _, _ = open_clip.create_model_and_transforms(
            MODEL_NAME, pretrained=str(checkpoint), device=self.device
        )
        if self.device.startswith("cuda"):
            self.model.half()
        self.model.eval()
        self.tokenizer = PreTrainedTokenizerFast(
            tokenizer_file=str(tokenizer_file), bos_token="<s>", eos_token="</s>",
            unk_token="<unk>", pad_token="<pad>", mask_token="<mask>"
        )

    def _text_vector(self, query: str) -> np.ndarray:
        self._ensure_visual_model()
        ids = self.tokenizer(
            [query], padding="max_length", truncation=True, max_length=77, return_tensors="pt"
        )["input_ids"].to(self.device)
        with self.torch.inference_mode():
            vector = self.model.encode_text(ids, normalize=True).to(dtype=self.torch.float32).cpu().numpy()[0]
        return vector

    def search(self, query: str, source: str = "all", limit: int = 60,
               phonetic_level: str = "medium", library: str = "") -> list[dict[str, Any]]:
        if not query.strip():
            return []
        connection = sqlite3.connect(self.database)
        connection.row_factory = sqlite3.Row
        ranked: dict[tuple[str, int], dict[str, Any]] = {}
        library = library.strip()
        asset_scope = " AND library_root=?" if library else ""
        joined_scope = " AND asset.library_root=?" if library else ""
        scope_parameters: tuple[str, ...] = (library,) if library else ()

        def contribute(path: str, timestamp_ms: int, source_name: str, rank: int,
                       explanation: str, thumbnail: str | None = None, weight: float = 1.0) -> None:
            key = (path, timestamp_ms)
            item = ranked.setdefault(key, {
                **_asset(connection, path), "timestampMs": timestamp_ms,
                "startMs": max(0, timestamp_ms - 2000), "endMs": timestamp_ms + 2000,
                "score": 0.0, "sources": [], "explanations": []
            })
            item["score"] += weight / (60 + rank)
            if source_name not in item["sources"]:
                item["sources"].append(source_name)
            item["explanations"].append(explanation)
            item["thumbnail"] = thumbnail or item.get("thumbnail") or _nearest_thumbnail(connection, path, timestamp_ms)

        if source in ("all", "filename"):
            rows = connection.execute(
                f"""SELECT media_path,name FROM media_assets
                   WHERE status!='Missing'{asset_scope} AND name LIKE ? ORDER BY
                   CASE WHEN lower(name)=lower(?) THEN 0 WHEN lower(name) LIKE lower(?) THEN 1 ELSE 2 END,
                   name LIMIT ?""",
                (*scope_parameters, f"%{query}%", query, f"{query}%", limit)
            ).fetchall()
            for rank, row in enumerate(rows, 1):
                exact = row["name"].casefold() == query.casefold()
                contribute(row["media_path"], 0, "filename", rank,
                           "文件名精确匹配" if exact else "文件名包含查询", weight=1.8 if exact else 1.2)
            matched_paths = {row["media_path"] for row in rows}
            if phonetic_level != "off":
                candidates = connection.execute(
                    f"""SELECT media_path,name FROM media_assets
                        WHERE status!='Missing'{asset_scope} ORDER BY name LIMIT 10000""",
                    scope_parameters
                ).fetchall()
                phonetic_rows = [(score, row) for row in candidates
                                 if row["media_path"] not in matched_paths
                                 if (score := _phonetic_match(query, row["name"], phonetic_level)) > 0]
                phonetic_rows.sort(key=lambda item: item[0], reverse=True)
                for rank, (score, row) in enumerate(phonetic_rows[:limit], 1):
                    contribute(row["media_path"], 0, "phonetic", rank,
                               f"文件名音近匹配 {score:.0%}", weight=0.65 * score)

        if source in ("all", "visual"):
            vector = self._text_vector(query)
            rows = connection.execute(
                f"""SELECT frame.media_path,frame.timestamp_ms,frame.embedding_f16,frame.thumbnail_path
                    FROM real_visual_frames frame
                    JOIN media_assets asset ON asset.media_path=frame.media_path
                    WHERE frame.model_version=?{joined_scope}""",
                ("openclip-standard-506d40eb", *scope_parameters)
            ).fetchall()
            scored: list[tuple[float, sqlite3.Row]] = []
            for row in rows:
                candidate = np.frombuffer(row["embedding_f16"], dtype=np.float16).astype(np.float32)
                if candidate.size == vector.size:
                    scored.append((float(candidate @ vector), row))
            scored.sort(key=lambda item: item[0], reverse=True)
            for rank, (similarity, row) in enumerate(scored[:limit], 1):
                contribute(row["media_path"], int(row["timestamp_ms"]), "visual", rank,
                           f"画面语义相似度 {similarity:.1%}", row["thumbnail_path"], max(0.2, similarity))

        if source in ("all", "speech"):
            try:
                rows = connection.execute(
                    f"""SELECT segment.media_path,segment.start_ms,segment.end_ms,segment.text
                        FROM transcript_segments_live segment
                        JOIN media_assets asset ON asset.media_path=segment.media_path
                        WHERE (segment.normalized_text LIKE ? OR segment.text LIKE ?)
                        {joined_scope} LIMIT ?""",
                    (f"%{query.casefold()}%", f"%{query}%", *scope_parameters, limit)
                ).fetchall()
                for rank, row in enumerate(rows, 1):
                    midpoint = (int(row["start_ms"]) + int(row["end_ms"])) // 2
                    contribute(row["media_path"], midpoint, "speech", rank,
                               f"语音文字匹配：{row['text'][:80]}", weight=1.0)
                if phonetic_level != "off":
                    candidates = connection.execute(
                        f"""SELECT segment.media_path,segment.start_ms,segment.end_ms,segment.text
                            FROM transcript_segments_live segment
                            JOIN media_assets asset ON asset.media_path=segment.media_path
                            WHERE 1=1{joined_scope}
                            ORDER BY segment.media_path,segment.start_ms LIMIT 10000""",
                        scope_parameters
                    ).fetchall()
                    phonetic_rows = [(score, row) for row in candidates
                                     if query.casefold() not in row["text"].casefold()
                                     if (score := _phonetic_match(query, row["text"], phonetic_level)) > 0]
                    phonetic_rows.sort(key=lambda item: item[0], reverse=True)
                    for rank, (score, row) in enumerate(phonetic_rows[:limit], 1):
                        midpoint = (int(row["start_ms"]) + int(row["end_ms"])) // 2
                        contribute(row["media_path"], midpoint, "phonetic", rank,
                                   f"语音音近匹配：{row['text'][:80]}", weight=0.55 * score)
            except sqlite3.OperationalError:
                pass

        if source in ("all", "ocr"):
            try:
                rows = connection.execute(
                    f"""SELECT observation.media_path,observation.timestamp_ms,
                               observation.text,observation.confidence
                        FROM ocr_observations_live observation
                        JOIN media_assets asset ON asset.media_path=observation.media_path
                        WHERE (observation.normalized_text LIKE ? OR observation.text LIKE ?)
                        {joined_scope} LIMIT ?""",
                    (f"%{query.casefold()}%", f"%{query}%", *scope_parameters, limit)
                ).fetchall()
                for rank, row in enumerate(rows, 1):
                    contribute(row["media_path"], int(row["timestamp_ms"]), "ocr", rank,
                               f"画面文字匹配：{row['text'][:80]}", weight=0.9 * float(row["confidence"]))
                if phonetic_level != "off":
                    candidates = connection.execute(
                        f"""SELECT observation.media_path,observation.timestamp_ms,
                                   observation.text,observation.confidence
                            FROM ocr_observations_live observation
                            JOIN media_assets asset ON asset.media_path=observation.media_path
                            WHERE 1=1{joined_scope}
                            ORDER BY observation.media_path,observation.timestamp_ms LIMIT 10000""",
                        scope_parameters
                    ).fetchall()
                    phonetic_rows = [(score, row) for row in candidates
                                     if query.casefold() not in row["text"].casefold()
                                     if (score := _phonetic_match(query, row["text"], phonetic_level)) > 0]
                    phonetic_rows.sort(key=lambda item: item[0], reverse=True)
                    for rank, (score, row) in enumerate(phonetic_rows[:limit], 1):
                        contribute(row["media_path"], int(row["timestamp_ms"]), "phonetic", rank,
                                   f"画面文字音近匹配：{row['text'][:80]}",
                                   weight=0.45 * score * float(row["confidence"]))
            except sqlite3.OperationalError:
                pass

        connection.close()
        results = sorted(ranked.values(), key=lambda item: item["score"], reverse=True)[:limit]
        for item in results:
            item["source"] = " / ".join(item.pop("sources"))
            item["explanation"] = "；".join(dict.fromkeys(item.pop("explanations")))
        return results


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--database", type=Path, required=True)
    parser.add_argument("--models-root", type=Path, required=True)
    args = parser.parse_args()
    engine = SearchEngine(args.database, args.models_root)
    print(json.dumps({"ready": True, "device": engine.device}, ensure_ascii=False), flush=True)
    for line in sys.stdin:
        try:
            request = json.loads(line)
            results = engine.search(str(request.get("query", "")), str(request.get("source", "all")),
                                    int(request.get("limit", 60)),
                                    str(request.get("phoneticLevel", "medium")),
                                    str(request.get("library", "")))
            print(json.dumps({"ok": True, "results": results}, ensure_ascii=False), flush=True)
        except Exception as error:
            print(json.dumps({"ok": False, "error": str(error)}, ensure_ascii=False), flush=True)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
