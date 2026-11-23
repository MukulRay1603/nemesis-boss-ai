"""
Summarise headless Unity evaluation runs (written by EvalRunner.cs).

Reads every *_summary.json / *_episodes.csv pair in a folder, groups runs by
label prefix (the part of the name before "_s<seed>"), and prints win rate with
a 95% Wilson interval, plus mean fight length and decision latency.

Usage:
    python eval/summarize_unity_eval.py eval/unity
Standard library only.
"""

import csv
import glob
import json
import math
import os
import re
import sys
from collections import defaultdict


def wilson(k, n, z=1.96):
    if n == 0:
        return (0.0, 0.0)
    p = k / n
    d = 1 + z * z / n
    c = (p + z * z / (2 * n)) / d
    h = z * math.sqrt(p * (1 - p) / n + z * z / (4 * n * n)) / d
    return (c - h, c + h)


def main(folder):
    groups = defaultdict(list)
    for path in sorted(glob.glob(os.path.join(folder, "*_summary.json"))):
        name = os.path.basename(path)[: -len("_summary.json")]
        label = re.sub(r"_s\d+$", "", name)
        with open(path) as f:
            s = json.load(f)
        if s.get("mode") != "Episodes":
            continue
        with open(os.path.join(folder, name + "_episodes.csv")) as f:
            eps = list(csv.DictReader(f))
        groups[label].append((name, s, eps))

    print(f"| Run | Episodes | Boss wins | Player wins | Timeouts | Boss win rate (95% CI) | Mean fight (s) | Boss hit rate |")
    print(f"|---|---|---|---|---|---|---|---|")
    for label, runs in groups.items():
        eps = [e for _, _, es in runs for e in es]
        n = len(eps)
        bw = sum(e["outcome"] == "BOSS_WIN" for e in eps)
        pw = sum(e["outcome"] == "PLAYER_WIN" for e in eps)
        to = n - bw - pw
        lo, hi = wilson(bw, n)
        dur = sum(float(e["duration_s"]) for e in eps) / n
        swings = sum(int(e["boss_swings"]) for e in eps)
        hits = sum(int(e["boss_hits"]) for e in eps)
        is_rl = runs[0][1].get("boss") == "rl"
        hit_rate = f"{hits / swings:.1%}" if swings and is_rl else "n/a"
        seeds = ",".join(str(s["seed"]) for _, s, _ in runs)
        print(f"| {label} (seeds {seeds}) | {n} | {bw} | {pw} | {to} | "
              f"{bw / n:.1%} ({lo:.1%} to {hi:.1%}) | {dur:.1f} | {hit_rate} |")

    print()
    for label, runs in groups.items():
        for name, s, _ in runs:
            inf = s.get("inference_ms")
            if inf:
                print(f"{name}: inference on {s['sentis_backend']} "
                      f"mean {inf['mean']:.3f} ms, p50 {inf['p50']:.3f}, p95 {inf['p95']:.3f}, "
                      f"p99 {inf['p99']:.3f}, max {inf['max']:.3f} (n={inf['n']})")
            if s.get("boss") == "rl":
                acts = [s.get(f"action_{i}", 0) for i in range(4)]
                tot = sum(acts) or 1
                print(f"{name}: action mix idle/attack/dodge/special = "
                      + " / ".join(f"{a / tot:.1%}" for a in acts))


if __name__ == "__main__":
    main(sys.argv[1] if len(sys.argv) > 1 else "eval/unity")
