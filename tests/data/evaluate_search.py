#!/usr/bin/env python3
from __future__ import annotations

import argparse
import json
from pathlib import Path
from pathlib import PureWindowsPath
import statistics


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("judgments", type=Path)
    parser.add_argument("results", type=Path)
    parser.add_argument("--output", type=Path)
    args = parser.parse_args()
    judgments = json.loads(args.judgments.read_text(encoding="utf-8"))
    results = json.loads(args.results.read_text(encoding="utf-8"))
    result_map = {item["id"]: item for item in results["queries"]}
    recalls: list[int] = []
    reciprocal: list[float] = []
    phonetic_lifts: list[float] = []
    latencies: list[float] = []
    details = []
    for query in judgments["queries"]:
        observed = result_map.get(query["id"], {})
        ranked_items = observed.get("results", [])[:10]
        ranked = [str(item.get("assetId", item.get("id", ""))) for item in ranked_items]
        ranked_files = [PureWindowsPath(str(item.get("path", ""))).name for item in ranked_items]
        relevant = {item["assetId"] for item in query["relevant"]}
        relevant_files = {item.get("file") for item in query["relevant"] if item.get("file")}
        hits = [rank for rank, (asset_id, filename) in enumerate(zip(ranked, ranked_files), 1)
                if asset_id in relevant or filename in relevant_files]
        recalls.append(1 if hits else 0)
        reciprocal.append(1 / min(hits) if hits else 0.0)
        if "phoneticRecallWith" in observed and "phoneticRecallWithout" in observed:
            phonetic_lifts.append(float(observed["phoneticRecallWith"]) - float(observed["phoneticRecallWithout"]))
        if "elapsedMs" in observed:
            latencies.append(float(observed["elapsedMs"]))
        details.append({"id": query["id"], "hitRank": min(hits) if hits else None})
    metrics = {"recallAt10": sum(recalls) / len(recalls), "mrr": sum(reciprocal) / len(reciprocal),
               "queryCount": len(recalls), "phoneticRecallLift": statistics.fmean(phonetic_lifts) if phonetic_lifts else None,
               "latencyP50Ms": statistics.median(latencies) if latencies else None,
               "latencyP95Ms": sorted(latencies)[min(len(latencies) - 1, round(len(latencies) * .95))] if latencies else None,
               "details": details}
    rendered = json.dumps(metrics, ensure_ascii=False, indent=2) + "\n"
    print(rendered, end="")
    if args.output:
        args.output.write_text(rendered, encoding="utf-8")
    gates = judgments.get("qualityGates", {"recallAt10": .80, "mrr": .65})
    return 0 if metrics["recallAt10"] >= gates["recallAt10"] and metrics["mrr"] >= gates["mrr"] else 1


if __name__ == "__main__":
    raise SystemExit(main())
