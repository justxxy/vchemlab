"""Business logic for the assistant endpoints (transport-independent).

`handle()` is used by both the HTTP server and the offline evaluation runner.
"""

from __future__ import annotations

import json
from typing import Any, Dict, List, Optional, Tuple

import config
from core import (
    CHUNKS,
    KEYFRAMES,
    build_context,
    check_safety,
    cite,
    get_class,
    get_error,
    kg_neighbors,
    kg_query,
    kg_resolve,
    kg_stats,
    retrieve,
    retrieve_detailed,
)
from providers import CloudProvider, MockProvider

_CACHE: Dict[Tuple[Any, ...], Dict[str, Any]] = {}


def _provider():
    if config.provider_mode() == "cloud":
        return CloudProvider()
    return MockProvider()


def _tags(ctx: Dict[str, Any], keyframe: Optional[str]) -> List[str]:
    tags: List[str] = []
    cls = (ctx.get("result") or {}).get("class")
    if cls:
        tags.append(str(cls))
    for e in (ctx.get("result") or {}).get("errors") or []:
        tags.append(str(e))
    if keyframe:
        tags.append("keyframe:" + keyframe)
    return tags


def _query(ctx: Dict[str, Any], question: str, keyframe: Optional[str]) -> str:
    parts = []
    cls = (ctx.get("result") or {}).get("class")
    if cls:
        info = get_class(cls)
        if info:
            parts.append(info.get("title", ""))
            parts.append(info.get("summary", ""))
    for e in (ctx.get("result") or {}).get("errors") or []:
        ei = get_error(e)
        if ei:
            parts.append(ei.get("title", ""))
            parts.append(ei.get("text", ""))
    if keyframe:
        parts.append("keyframe " + keyframe)
    parts.append(question or "")
    return " ".join(parts)


def _normalize(data: Dict[str, Any], provider_name: str) -> Dict[str, Any]:
    out = dict(data)
    out.setdefault("answer", "")
    out.setdefault("hints", [])
    out.setdefault("next_action", "")
    out.setdefault("citations", [])
    out.setdefault("safety", "ok")
    out.setdefault("level", "L1")
    out["provider"] = out.get("provider") or provider_name
    return out


def _refusal(kind: str, reply: str) -> Dict[str, Any]:
    return {
        "answer": reply,
        "hints": [],
        "next_action": "请先回到本实验的装置、试剂、验满和检验四个点。",
        "citations": cite(["k-safety-acid"]),
        "safety": "refuse",
        "level": "L0",
        "provider": "policy",
        "refusalKind": kind,
    }


def handle(
    mode: str,
    plan: Optional[Dict[str, Any]] = None,
    result: Optional[Dict[str, Any]] = None,
    timeline: Optional[Dict[str, Any]] = None,
    question: str = "",
    level: str = "L1",
    keyframe: Optional[str] = None,
) -> Dict[str, Any]:
    """Return a normalized assistant answer.

    mode: explain | hint | ask | report
    """
    mode = (mode or "explain").lower()
    if mode not in ("explain", "hint", "ask", "report"):
        mode = "explain"
    level = (level or "L1").upper()
    if level not in ("L1", "L2", "L3"):
        level = "L1"

    plan = plan or {}
    result = result or {}
    ctx = build_context(plan, result, timeline, keyframe)

    if mode == "ask":
        refusal = check_safety(question)
        if refusal:
            return _refusal(refusal["kind"], refusal["reply"])
        if not (question or "").strip():
            return _refusal("empty", "先输入一个与本实验有关的问题，例如“木条为什么会熄灭？”")

    if mode in ("explain", "hint"):
        cache_key = (
            mode,
            (result.get("class") or ""),
            tuple(result.get("errors") or []),
            level,
            keyframe or "",
            config.provider_mode(),
        )
        if cache_key in _CACHE:
            hit = dict(_CACHE[cache_key])
            hit["cached"] = True
            return hit

    chunks = retrieve(_query(ctx, question, keyframe), _tags(ctx, keyframe), top_k=4)
    provider = _provider()
    data = provider.answer(mode, ctx, chunks, question, level)
    data = _normalize(data, provider.name)

    # Safety net: provider output must not claim "ok" with an empty answer.
    if not data.get("answer"):
        data["answer"] = "本节知识包暂时没有可用的解释。"

    if mode in ("explain", "hint"):
        _CACHE[cache_key] = dict(data)
        data["cached"] = False
    else:
        data["cached"] = False
    data["mode"] = mode
    return data


def rag_retrieve(
    question: str = "",
    result: Optional[Dict[str, Any]] = None,
    keyframe: Optional[str] = None,
    top_k: int = 4,
) -> Dict[str, Any]:
    """RAG retrieval trace: query -> ranked chunks -> prompt context -> answer.

    This is the minimal, inspectable RAG demo.  It uses the same retrieval path
    as the assistant answers, but exposes the ranked chunks and scores so the
    knowledge-grounding can be demonstrated without a cloud LLM.
    """
    result = result or {}
    ctx = build_context({}, result, None, keyframe)
    q = _query(ctx, question or "这个结果为什么会发生？", keyframe)
    tags = _tags(ctx, keyframe)

    # KG→RAG 联动：识别查询中的实体，取一跳子图关联的片段作为加权种子。
    entity_ids = kg_resolve(q)
    kg_hops = [kg_neighbors(eid, depth=1) for eid in entity_ids[:3]]
    boost_ids = {cid for hop in kg_hops for cid in hop.get("chunks", [])}
    chunks = retrieve_detailed(
        q, tags, top_k=max(1, min(6, int(top_k or 4))), boost_ids=boost_ids
    )
    lines = [
        "[{id}] {title}：{text}（来源：{source}）".format(
            id=c.get("id", ""),
            title=c.get("title", ""),
            text=c.get("text", ""),
            source=c.get("source", ""),
        )
        for c in chunks
    ]
    answer = handle(
        "ask",
        plan={},
        result=result,
        question=question or "这个结果为什么会发生？",
        keyframe=keyframe,
    )
    return {
        "mode": "rag-retrieval",
        "provider": config.provider_mode(),
        "query": q,
        "tags": tags,
        "chunks": chunks,
        "prompt_context": "\n".join(lines) or "（无匹配片段）",
        "answer": answer.get("answer", ""),
        "citations": answer.get("citations", []),
        "safety": answer.get("safety", "ok"),
        "kg_entities": entity_ids,
        "kg_boost_chunks": sorted(boost_ids),
        "kg_hops": kg_hops,
    }


def kg_overview() -> Dict[str, Any]:
    return {"mode": "kg-overview", **kg_stats(), "entity_list": [
        {"id": e["id"], "label": e["label"], "type": e["type"]} for e in __import__("core").KG_ENTITIES
    ]}


def kg_entity(name: str, depth: int = 1, relation: str = "") -> Dict[str, Any]:
    if relation:
        return {"mode": "kg-query", "entity": name, "relation": relation,
                "triples": kg_query(entity=name, relation=relation)}
    hop = kg_neighbors(name, depth=depth)
    return {"mode": "kg-neighbors", **hop}


def health() -> Dict[str, Any]:
    return {
        "ok": True,
        "provider": config.provider_mode(),
        "model": config.LLM_MODEL if config.provider_mode() == "cloud" else "mock",
        "chunks": len(CHUNKS),
        "classes": len(__import__("core").CLASSES),
        "keyframes": list(KEYFRAMES.keys()),
        "kg": kg_stats(),
    }


def read_current_submission() -> Dict[str, Any]:
    """Read the last M2 submission from .workbuddy/artifacts/runs/current."""
    d = config.RUN_DIR
    out: Dict[str, Any] = {"dir": str(d), "plan": None, "result": None}
    try:
        if (d / "plan.json").exists():
            out["plan"] = json.loads((d / "plan.json").read_text(encoding="utf-8-sig"))
        if (d / "result.json").exists():
            out["result"] = json.loads((d / "result.json").read_text(encoding="utf-8-sig"))
    except Exception as exc:
        out["error"] = str(exc)
    return out
