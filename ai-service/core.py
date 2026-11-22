"""Knowledge, context assembly, retrieval and safety for the CO2 AI assistant demo.

This module is deliberately dependency-free (stdlib only) so the demo runs on a
clean Python install.  It is the single source of truth for:
  * the curated knowledge pack (knowledge/knowledge.json)
  * plan/result/timeline -> compact context
  * lightweight Chinese retrieval (char bigrams + tags)
  * scope/safety refusal policy
"""

from __future__ import annotations

import json
import math
import re
from pathlib import Path
from typing import Any, Dict, Iterable, List, Optional, Tuple

BASE_DIR = Path(__file__).resolve().parent
KNOWLEDGE_PATH = BASE_DIR / "knowledge" / "knowledge.json"

with KNOWLEDGE_PATH.open("r", encoding="utf-8") as f:
    KNOWLEDGE: Dict[str, Any] = json.load(f)

CHUNKS: List[Dict[str, Any]] = KNOWLEDGE["chunks"]
CHUNK_BY_ID: Dict[str, Dict[str, Any]] = {c["id"]: c for c in CHUNKS}
CLASSES: Dict[str, Dict[str, Any]] = KNOWLEDGE["classes"]
ERRORS: Dict[str, Dict[str, Any]] = KNOWLEDGE["errors"]
KEYFRAMES: Dict[str, Dict[str, Any]] = KNOWLEDGE["keyframes"]
SAFETY: Dict[str, Any] = KNOWLEDGE["safety"]

_CN = re.compile(r"[\u4e00-\u9fff]")
_WORD = re.compile(r"[a-zA-Z0-9_\-]+")

# --- 领域同义词归一 -----------------------------------------------------------
# 学生提问与知识包措辞不一致是召回失败的主因。这里把常见别名映射到规范词，
# 检索时同时保留原文 token 与归一后 token（见 _search_tokens），兼顾召回与精度。
# 纯字符串替换、零依赖、确定性，不改变知识包本身。
_SYNONYM_GROUPS: Dict[str, List[str]] = {
    "二氧化碳": ["co2", "co₂", "二氧化碳", "碳酸气"],
    "木条": ["木条", "燃着的木条", "燃着木条", "带火星的木条"],
    "集气瓶": ["集气瓶", "瓶子", "瓶口", "气瓶"],
    "漏斗": ["漏斗", "长颈漏斗"],
    "导管": ["导管", "导气管", "气管"],
    "稀盐酸": ["稀盐酸", "稀 hcl", "稀hcl"],
    "稀硫酸": ["稀硫酸", "稀 h2so4", "稀h2so4"],
    "浓盐酸": ["浓盐酸", "浓 hcl", "浓hcl"],
    "大理石": ["大理石", "碳酸钙", "caco3", "石灰石"],
    "硫酸钙": ["硫酸钙", "caso4", "caso₄"],
    "石灰水": ["石灰水", "澄清石灰水", "氢氧化钙"],
    "锥形瓶": ["锥形瓶", "发生瓶", "发生容器"],
    "玻璃片": ["玻璃片", "毛玻璃片"],
    "液封": ["液封", "水封"],
    "验满": ["验满", "收满", "集满", "已满"],
    "排水法": ["排水法", "排水集气", "排水收集"],
    "向上排空气": ["向上排空气", "正放", "排空气法"],
}
_ALIAS_TO_CANON: Dict[str, str] = {
    alias: canon for canon, aliases in _SYNONYM_GROUPS.items() for alias in aliases
}
_ALIASES_BY_LEN = sorted(_ALIAS_TO_CANON, key=len, reverse=True)


def _canonicalize(text: str) -> str:
    out = (text or "").lower()
    for alias in _ALIASES_BY_LEN:
        if alias in out:
            out = out.replace(alias, _ALIAS_TO_CANON[alias])
    return out


def _tokens(text: str) -> set[str]:
    """Cheap Chinese-friendly tokens: CJK character bigrams + latin words."""
    text = (text or "").lower()
    chars = _CN.findall(text)
    grams = {"".join(chars[i:i + 2]) for i in range(max(0, len(chars) - 1))}
    words = {w for w in _WORD.findall(text) if len(w) > 1}
    return grams | words


def _search_tokens(text: str) -> set[str]:
    """原文 token ∪ 同义词归一后 token，兼顾不同说法的召回。"""
    return _tokens(text) | _tokens(_canonicalize(text))


def _build_idf() -> Tuple[Dict[str, float], float]:
    """语料内 IDF（出现越少的 token 权重越高），基于受控知识包静态计算。"""
    df: Dict[str, int] = {}
    for ch in CHUNKS:
        toks = _search_tokens((ch.get("title", "") or "") + " " + (ch.get("text", "") or ""))
        for t in toks:
            df[t] = df.get(t, 0) + 1
    n = max(1, len(CHUNKS))
    idf = {t: math.log((1 + n) / (1 + d)) + 1.0 for t, d in df.items()}
    return idf, max(idf.values()) if idf else 1.0


_IDF, _DEFAULT_IDF = _build_idf()

# --- 最小知识图谱 -------------------------------------------------------------
KG_PATH = BASE_DIR / "knowledge" / "co2_kg.json"
with KG_PATH.open("r", encoding="utf-8") as f:
    KG: Dict[str, Any] = json.load(f)

KG_ENTITIES: List[Dict[str, Any]] = KG["entities"]
KG_TRIPLES: List[Dict[str, Any]] = KG["triples"]
_ENTITY_BY_ID: Dict[str, Dict[str, Any]] = {e["id"]: e for e in KG_ENTITIES}


def _entity_label(ref: str) -> str:
    """实体 id 返回 label，自由文本（如「空气」「水」）原样返回。"""
    e = _ENTITY_BY_ID.get(ref)
    return e["label"] if e else str(ref)


def kg_stats() -> Dict[str, Any]:
    return {
        "schema": KG.get("schema"),
        "entities": len(KG_ENTITIES),
        "triples": len(KG_TRIPLES),
        "relation_types": sorted({t["p"] for t in KG_TRIPLES}),
    }


def kg_query(entity: Optional[str] = None, relation: Optional[str] = None) -> List[Dict[str, Any]]:
    """查询三元组，entity 可为 id 或别名/标签；relation 精确匹配谓词。"""
    eid = None
    if entity:
        eid = entity if entity in _ENTITY_BY_ID else next(
            (e["id"] for e in KG_ENTITIES
             if entity == e["label"] or entity in e.get("aliases", [])), None)

    def involved(t):
        if not eid:
            return True
        return t["s"] == eid or t["o"] == eid or t.get("via") == eid

    out = []
    for t in KG_TRIPLES:
        if not involved(t) or (relation and t["p"] != relation):
            continue
        row = dict(t)
        row["s_label"] = _entity_label(t["s"])
        row["o_label"] = _entity_label(t["o"])
        if t.get("via"):
            row["via_label"] = _entity_label(t["via"])
        out.append(row)
    return out


def kg_neighbors(entity: str, depth: int = 1) -> Dict[str, Any]:
    """实体一跳/二跳子图：返回节点、边，以及关联的知识片段 id（供 RAG 加权）。"""
    eid = entity if entity in _ENTITY_BY_ID else next(
        (e["id"] for e in KG_ENTITIES
         if entity == e["label"] or entity in e.get("aliases", [])), None)
    if not eid:
        return {"entity": entity, "found": False, "nodes": [], "edges": [], "chunks": []}

    frontier = {eid}
    seen = {eid}
    edges: List[Dict[str, Any]] = []
    for _ in range(max(1, min(2, int(depth or 1)))):
        nxt = set()
        for t in KG_TRIPLES:
            if t["s"] in frontier or t["o"] in frontier or t.get("via") in frontier:
                if t not in edges:
                    edges.append(t)
                nxt.update(x for x in (t["s"], t["o"], t.get("via")) if x and x in _ENTITY_BY_ID)
        frontier = nxt - seen
        seen.update(frontier)

    nodes = [dict(_ENTITY_BY_ID[n]) for n in seen if n in _ENTITY_BY_ID]
    chunks = sorted({cid for t in edges for cid in t.get("citations", [])})
    return {
        "entity": eid,
        "label": _entity_label(eid),
        "found": True,
        "depth": depth,
        "nodes": nodes,
        "edges": [{**t, "s_label": _entity_label(t["s"]), "o_label": _entity_label(t["o"])} for t in edges],
        "chunks": chunks,
    }


def kg_resolve(text: str) -> List[str]:
    """从自然语言里识别提到的实体 id（按别名最长匹配，避免短词误命中）。"""
    low = (text or "").lower()
    hits: List[str] = []
    for e in KG_ENTITIES:
        names = [e["label"].lower()] + [a.lower() for a in e.get("aliases", [])]
        if any(name in low for name in names):
            hits.append(e["id"])
    return hits


def retrieve_detailed(
    query: str,
    tags: Iterable[str] = (),
    top_k: int = 4,
    boost_ids: Iterable[str] = (),
) -> List[Dict[str, Any]]:
    """Return ranked chunks with scores, for the RAG retrieval trace.

    v2: synonym-normalised token overlap, IDF weighting (rarer terms count more),
    plus optional ``boost_ids`` (e.g. chunks linked by a knowledge-graph hop).
    All signals are deterministic and reported in ``score`` for inspection.
    """
    q = (query or "").strip()
    tag_set = {t.lower() for t in tags if t}
    boost = {str(x) for x in boost_ids if x}
    q_tokens = _search_tokens(q)
    scored: List[Tuple[float, Dict[str, Any]]] = []

    for ch in CHUNKS:
        text = (ch.get("title", "") + " " + ch.get("text", "")).lower()
        ch_tags = {str(t).lower() for t in ch.get("tags", [])}
        ch_tokens = _search_tokens(text)

        # IDF 加权的 token 重合度，按查询长度归一，避免长查询天然占优。
        common = q_tokens & ch_tokens
        if common and q_tokens:
            weight = sum(_IDF.get(t, _DEFAULT_IDF) for t in common)
            norm = math.sqrt(sum(_IDF.get(t, _DEFAULT_IDF) ** 2 for t in q_tokens))
            score = weight / norm if norm else 0.0
        else:
            score = 0.0

        for tag in tag_set:
            if tag in ch_tags:
                score += 4.0          # exact class/error/keyframe tag wins
            elif tag and tag in text:
                score += 1.0
        # 规范词整串命中给一点额外分（如「液封」「木条」）。
        canon_q = _canonicalize(q)
        for canon in _SYNONYM_GROUPS:
            if canon in canon_q and canon in text:
                score += 0.8

        if q and _canonicalize(q) in _canonicalize(text):
            score += 1.5

        if ch.get("id") in boost:
            score += 3.0              # KG 一跳邻居的出处，用于 KG+RAG 联动

        if score > 0:
            scored.append((score, ch))

    scored.sort(key=lambda x: (-x[0], x[1]["id"]))
    out: List[Dict[str, Any]] = []
    for score, ch in scored[:top_k]:
        item = dict(ch)
        item["score"] = round(score, 4)
        out.append(item)
    return out


def retrieve(query: str, tags: Iterable[str] = (), top_k: int = 4) -> List[Dict[str, Any]]:
    """Return the best matching knowledge chunks.  Deterministic and explainable."""
    return [
        {k: v for k, v in chunk.items() if k != "score"}
        for chunk in retrieve_detailed(query, tags, top_k)
    ]


def get_class(class_id: Optional[str]) -> Optional[Dict[str, Any]]:
    return CLASSES.get(class_id or "")


def get_error(code: str) -> Optional[Dict[str, Any]]:
    return ERRORS.get(code)


def get_keyframe(label: str) -> Optional[Dict[str, Any]]:
    return KEYFRAMES.get(label)


def cite(ids: Iterable[str]) -> List[Dict[str, str]]:
    out: List[Dict[str, str]] = []
    seen = set()
    for cid in ids:
        if cid in seen or cid not in CHUNK_BY_ID:
            continue
        seen.add(cid)
        ch = CHUNK_BY_ID[cid]
        out.append({
            "id": cid,
            "source": ch.get("source", ""),
            "quote": ch.get("text", "")[:80] + ("…" if len(ch.get("text", "")) > 80 else ""),
        })
    return out


def check_safety(question: str) -> Optional[Dict[str, str]]:
    """Return a refusal payload, or None when the question is in scope/safe."""
    q = (question or "").strip()
    if not q:
        return None
    for kw in SAFETY.get("danger_keywords", []):
        if kw and kw in q:
            return {"kind": "danger", "reply": SAFETY.get("danger_reply", "")}
    for kw in SAFETY.get("out_of_scope_keywords", []):
        if kw and kw in q:
            return {"kind": "scope", "reply": SAFETY.get("scope_reply", "")}
    return None


def _node_types(plan: Dict[str, Any]) -> Dict[str, int]:
    counts: Dict[str, int] = {}
    for n in (plan.get("topology", {}) or {}).get("nodes", []) or []:
        t = str(n.get("type", ""))
        counts[t] = counts.get(t, 0) + 1
    return counts


def build_context(
    plan: Optional[Dict[str, Any]] = None,
    result: Optional[Dict[str, Any]] = None,
    timeline: Optional[Dict[str, Any]] = None,
    keyframe: Optional[str] = None,
) -> Dict[str, Any]:
    """Compact, privacy-safe context for prompts.  No names/IDs are forwarded."""
    plan = plan or {}
    result = result or {}

    counts = _node_types(plan)
    feeds = plan.get("feeds", {}) or {}
    verification = plan.get("verification", {}) or {}
    topology = plan.get("topology", {}) or {}
    edges = topology.get("edges", []) or []

    ctx: Dict[str, Any] = {
        "plan": {
            "reactor": "flask" if counts.get("reactor.flask_conical") else
                       "test_tube" if counts.get("reactor.test_tube") else "none",
            "funnel": counts.get("feed.thistle_funnel", 0),
            "conduit": counts.get("conduit.bent", 0),
            "jar": counts.get("collect.gas_jar", 0),
            "trough": counts.get("collect.water_trough", 0),
            "feed_edges": sum(1 for e in edges if e.get("kind") == "feed"),
            "gas_edges": sum(1 for e in edges if e.get("kind") == "gas"),
            "water_edges": sum(1 for e in edges if e.get("kind") == "water"),
            "solid": feeds.get("solid", ""),
            "liquid": feeds.get("liquid", ""),
            "liquid_route": feeds.get("liquid_route", "funnel"),
            "fullness": verification.get("fullness", "none"),
            "identity": verification.get("identity", "none"),
        },
        "result": {
            "class": result.get("class"),
            "outcome": result.get("outcome"),
            "errors": list(result.get("errors") or []),
            "diagnosis": (result.get("diagnosis") or {}).get("summary", ""),
            "phenomena": result.get("phenomena") or {},
            "quantities": result.get("quantities") or {},
        },
    }

    if timeline:
        ctx["timeline"] = {
            "totalSec": timeline.get("totalSec"),
            "class": timeline.get("class"),
            "keyframes": timeline.get("keyframes", []),
        }
    if keyframe:
        kf = get_keyframe(keyframe)
        if kf:
            ctx["keyframe"] = {"label": keyframe, **kf}
    return ctx


def context_text(ctx: Dict[str, Any], max_chars: int = 2400) -> str:
    text = json.dumps(ctx, ensure_ascii=False, separators=(",", ":"))
    return text[:max_chars] + ("…" if len(text) > max_chars else "")
