"""检索命中率评测：v1 基线（纯 bigram）对比 v2（同义词 + IDF + KG 加权）。

Run from repo root:
    python ai-service/evals/run_retrieval_eval.py
Exit code 0 when v2 top3 >= 0.90 and v2 >= v1 top3.

评测指标：
  top1/top3/top5 —— 期望片段出现在前 N 名的比例（主指标 top3）
  MRR            —— 首个命中名次倒数的均值（衡量排序质量）
"""

from __future__ import annotations

import json
import math
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT))

import core  # noqa: E402

CASES = json.loads((ROOT / "evals" / "retrieval_cases.json").read_text(encoding="utf-8"))["cases"]


def baseline(query: str, tags, top_k: int = 5):
    """旧算法：中文 bigram 重合数 / sqrt(len)，加标签分。用于量化升级幅度。"""
    q = (query or "").strip()
    tag_set = {t.lower() for t in tags if t}
    q_tokens = core._tokens(q)
    scored = []
    for ch in core.CHUNKS:
        text = (ch.get("title", "") + " " + ch.get("text", "")).lower()
        ch_tags = {t.lower() for t in ch.get("tags", [])}
        overlap = len(q_tokens & core._tokens(text))
        score = overlap / math.sqrt(max(1, len(q_tokens))) if q_tokens else 0.0
        for tag in tag_set:
            if tag in ch_tags:
                score += 4.0
            elif tag and tag in text:
                score += 1.0
        if q and q.lower() in text:
            score += 1.5
        if score > 0:
            scored.append((score, ch["id"]))
    scored.sort(key=lambda x: (-x[0], x[1]))
    return [cid for _, cid in scored[:top_k]]


def metrics(rank_for_case):
    hits = {1: 0, 3: 0, 5: 0}
    rr = 0.0
    total = 0
    for ranks in rank_for_case:
        total += 1
        best = next((r for r in ranks if r is not None), None)
        for n in hits:
            if best is not None and best <= n:
                hits[n] += 1
        rr += 1.0 / best if best else 0.0
    return {
        "top1": hits[1] / total,
        "top3": hits[3] / total,
        "top5": hits[5] / total,
        "mrr": rr / total,
    }


def evaluate(fn, use_tags: bool, alias_only: bool = False):
    per_case = []
    for c in CASES:
        if alias_only and not c.get("alias"):
            continue
        tags = (c.get("tags") or []) if use_tags else []
        ranked = fn(c["question"], tags, 5)
        expect = set(c["expect"])
        ranks = [(i + 1) if cid in expect else None for i, cid in enumerate(ranked)]
        per_case.append((c["id"], ranks, ranked))
    return metrics([x[1] for x in per_case]), per_case


def pct(x):
    return f"{x * 100:.1f}%"


def show(label, m1, m2):
    print(f"\n--- {label} ---")
    print(f"  v1 baseline : top1={pct(m1['top1'])} top3={pct(m1['top3'])} top5={pct(m1['top5'])} MRR={m1['mrr']:.3f}")
    print(f"  v2 upgraded : top1={pct(m2['top1'])} top3={pct(m2['top3'])} top5={pct(m2['top5'])} MRR={m2['mrr']:.3f}")
    print(f"  delta top3  : {(m2['top3'] - m1['top3']) * 100:+.1f}pp   delta MRR: {m2['mrr'] - m1['mrr']:+.3f}")


def main() -> int:
    v2 = lambda q, t, k: [c["id"] for c in core.retrieve_detailed(q, t, top_k=k)]

    # 轨道 A：带结果类/关键帧标签（助教真实调用路径）
    a1, _ = evaluate(baseline, use_tags=True)
    a2, _ = evaluate(v2, use_tags=True)
    # 轨道 B：仅问题文本（剥离标签，考验同义词 + IDF 本身）
    b1, _ = evaluate(baseline, use_tags=False)
    b2, _ = evaluate(v2, use_tags=False)
    # 轨道 C：别名/口语措辞查询（CO2/CaCO3/气瓶/毛玻璃片等），同义词归一的主战场
    c1, _ = evaluate(baseline, use_tags=False, alias_only=True)
    c2, cdetail = evaluate(v2, use_tags=False, alias_only=True)

    alias_n = sum(1 for x in CASES if x.get("alias"))
    print(f"cases: {len(CASES)} (alias {alias_n})")
    show("A. 带标签（助教真实路径）", a1, a2)
    show("B. 纯问题文本（同义词+IDF）", b1, b2)
    show("C. 别名/口语措辞（同义词主战场）", c1, c2)

    misses = [(cid, ranked, next(x["expect"] for x in CASES if x["id"] == cid)) for cid, ranks, ranked in cdetail
              if not any(r is not None and r <= 3 for r in ranks)]
    if misses:
        print("\nv2 top3 misses（别名轨）:")
        for cid, ranked, expect in misses:
            print(f"  {cid}: expect={expect} got={ranked}")

    ok = a2["top3"] >= 0.90 and a2["top3"] >= a1["top3"] and b2["top3"] >= b1["top3"] and c2["top3"] >= c1["top3"]
    print("\nRESULT:", "PASS" if ok else "FAIL")
    return 0 if ok else 1


if __name__ == "__main__":
    raise SystemExit(main())
