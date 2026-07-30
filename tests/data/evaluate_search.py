#!/usr/bin/env python3
from __future__ import annotations
import argparse, json
from pathlib import Path

def main() -> int:
    parser=argparse.ArgumentParser(); parser.add_argument("judgments",type=Path); parser.add_argument("results",type=Path); args=parser.parse_args()
    judgments=json.loads(args.judgments.read_text())["queries"]; results=json.loads(args.results.read_text())["queries"]
    recalls=[]; reciprocal=[]
    for query,relevant in judgments.items():
        ranked=results.get(query,[])[:10]; hits=[i for i,item in enumerate(ranked,1) if item in relevant]
        recalls.append(bool(hits)); reciprocal.append(1/min(hits) if hits else 0)
    metrics={"recallAt10":sum(recalls)/len(recalls),"mrr":sum(reciprocal)/len(reciprocal),"queryCount":len(recalls)}
    print(json.dumps(metrics,indent=2)); return 0 if metrics["recallAt10"]>=.8 and metrics["mrr"]>=.65 else 1
if __name__=="__main__": raise SystemExit(main())

