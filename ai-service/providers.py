"""LLM providers: deterministic mock + OpenAI-compatible cloud adapter.

Provider selection is automatic:
  * no LLM_BASE_URL / LLM_API_KEY  -> MockProvider (offline demo)
  * otherwise                       -> CloudProvider, fallback to Mock on failure
"""

from __future__ import annotations

import json
import re
import urllib.error
import urllib.request
from typing import Any, Dict, List, Optional

import config
import prompts
from core import cite, get_class, get_error, get_keyframe


def _refuse(reply: str) -> Dict[str, Any]:
    return {
        "answer": reply,
        "hints": [],
        "next_action": "请先回到本实验的装置、试剂、验满和检验四个点。",
        "citations": cite(["k-safety-acid"]),
        "safety": "refuse",
        "level": "L0",
    }


class MockProvider:
    """Deterministic, template-based answers.  Used without an API key and in tests."""

    name = "mock"

    def answer(
        self,
        mode: str,
        ctx: Dict[str, Any],
        chunks: List[Dict[str, Any]],
        question: str = "",
        level: str = "L1",
    ) -> Dict[str, Any]:
        cls = (ctx.get("result") or {}).get("class")
        errors = list((ctx.get("result") or {}).get("errors") or [])
        info = get_class(cls)

        if mode == "explain":
            return self._explain(ctx, info, errors, chunks)
        if mode == "hint":
            return self._hint(ctx, info, errors, level, chunks)
        if mode == "ask":
            return self._ask(ctx, info, question, chunks)
        if mode == "report":
            return self._report(ctx, info, errors)
        return self._explain(ctx, info, errors, chunks)

    # ---------------------------------------------------------------- modes

    def _explain(self, ctx, info, errors, chunks):
        if not info and errors:
            errs = [get_error(e) for e in errors]
            errs = [e for e in errs if e]
            answer = "装置结构还不完整："
            answer += "；".join(f"{e['title']}——{e['text']}" for e in errs)
            next_action = errs[0]["fix"] if errs else "按右侧提示补齐装置。"
            cids = [c for e in errs for c in e.get("citations", [])]
            return self._out(answer, [], next_action, cids, "L1")

        if not info:
            return self._out(
                "这次的方案还没有命中可解释的结果类；请先检查器材、试剂和连线是否完整。",
                [], "按右侧诊断提示补齐后再提交。",
                [c["id"] for c in chunks], "L1",
            )

        answer = f"{info['title']}。{info['summary']}"
        if cls_is_partial(ctx) and info["title"].startswith("部分达成"):
            answer += " " + partial_reason(ctx)
        if errors:
            errs = [get_error(e) for e in errors]
            errs = [e for e in errs if e]
            if errs:
                answer += " 结构上还有：" + "；".join(e["text"] for e in errs)
        next_action = info["fix"]
        cids = list(info.get("citations", []))
        for e in errors:
            ei = get_error(e)
            if ei:
                cids += ei.get("citations", [])
        if not cids:
            cids = [c["id"] for c in chunks]
        return self._out(answer, list(info.get("hints", []))[:2], next_action, cids, "L1")

    def _hint(self, ctx, info, errors, level, chunks):
        level = (level or "L1").upper()
        if level not in ("L1", "L2", "L3"):
            level = "L1"
        if not info:
            if errors:
                ei = get_error(errors[0])
                if ei:
                    return self._out(ei["text"], [], ei["fix"], ei.get("citations", []), level)
            return self._out("先检查装置是否完整：发生容器、漏斗、导管、集气瓶。",
                             [], "按右侧红字补齐。", [c["id"] for c in chunks], level)

        hints = list(info.get("hints", []))
        if level == "L1":
            answer = hints[0] if hints else info["summary"]
            next_action = "先观察，不急着改。"
        elif level == "L2":
            answer = hints[1] if len(hints) > 1 else info["cause"]
            next_action = "想一想这个原理对应装置里的哪个部件。"
        else:
            answer = f"具体操作：{info['fix']}"
            next_action = info["fix"]
        return self._out(answer, hints, next_action, info.get("citations", []), level)

    def _ask(self, ctx, info, question, chunks):
        kf = ctx.get("keyframe")
        if kf and kf.get("explain"):
            return self._out(kf["explain"], [], "可以继续观察下一个关键帧。",
                             kf.get("citations", []), "L1")
        if chunks:
            top = chunks[0]
            answer = f"{top['text']}"
            if info:
                answer = f"{info['summary']} {answer}"
            return self._out(answer, [], "如果还想深入，可以问“为什么”或“下一步怎么做”。",
                             [c["id"] for c in chunks[:3]], "L1")
        return self._out("本节知识包没有覆盖这个问题；可以先从装置、试剂、验满、检验四个点问起。",
                         [], "换一个与本实验有关的问题。", [], "L1")

    def _report(self, ctx, info, errors):
        cls = (ctx.get("result") or {}).get("class")
        if info:
            title = info["title"]
            good = info["summary"]
            gap = info["cause"] if cls and cls not in ("R-OK-01",) else "本次没有明显失误。"
            nxt = info["fix"]
            cids = info.get("citations", [])
        else:
            title = "结构尚未完成"
            good = "你已经开始搭建装置。"
            gap = "；".join((get_error(e) or {}).get("text", e) for e in errors) or "装置不完整。"
            nxt = "先补齐发生装置、加料路径和收集装置。"
            cids = [c["id"] for c in []]

        answer = (
            f"本次结果：{title}。\n"
            f"掌握点：{good}\n"
            f"待补点：{gap}\n"
            f"下一步：{nxt}"
        )
        return self._out(answer, [], nxt, cids, "L1")

    # ---------------------------------------------------------------- helper

    def _out(self, answer, hints, next_action, cids, level):
        return {
            "answer": answer,
            "hints": [h for h in hints if h][:3],
            "next_action": next_action,
            "citations": cite(cids),
            "safety": "ok",
            "level": level,
        }


def cls_is_partial(ctx: Dict[str, Any]) -> bool:
    return (ctx.get("result") or {}).get("class") == "R-OK-02"


def partial_reason(ctx: Dict[str, Any]) -> str:
    plan = ctx.get("plan") or {}
    fullness = plan.get("fullness", "none")
    identity = plan.get("identity", "none")
    if fullness == "none" and identity == "none":
        return "验满和检验都没有做。"
    if fullness == "none":
        return "缺验满：检验栏的〔验满〕没有放燃着的木条。"
    if identity == "none":
        return "缺检验：检验栏的〔检验〕没有放澄清石灰水。"
    return ""


class CloudProvider:
    """OpenAI-compatible chat completions.  Key stays in the backend .env."""

    name = "cloud"

    def __init__(self) -> None:
        self.base = config.LLM_BASE_URL
        self.key = config.LLM_API_KEY
        self.model = config.LLM_MODEL
        self.timeout = config.LLM_TIMEOUT

    def answer(
        self,
        mode: str,
        ctx: Dict[str, Any],
        chunks: List[Dict[str, Any]],
        question: str = "",
        level: str = "L1",
    ) -> Dict[str, Any]:
        user = prompts.render(mode, ctx, chunks, question, level)
        try:
            content = self._chat([
                {"role": "system", "content": prompts.SYSTEM},
                {"role": "user", "content": user},
            ])
            data = self._parse(content)
            if not data or not isinstance(data.get("answer"), str):
                raise ValueError("missing answer")

            allowed = {c["id"] for c in chunks} | {
                cid for info in (get_class((ctx.get("result") or {}).get("class")),)
                for cid in (info or {}).get("citations", [])
            }
            raw_cites = data.get("citations") or []
            ids: List[str] = []
            for c in raw_cites:
                if isinstance(c, str):
                    ids.append(c)
                elif isinstance(c, dict) and c.get("id"):
                    ids.append(str(c["id"]))
            ids = [i for i in ids if i in allowed]
            if not ids:
                info = get_class((ctx.get("result") or {}).get("class"))
                ids = list((info or {}).get("citations", [])) or [c["id"] for c in chunks[:3]]

            return {
                "answer": data["answer"].strip(),
                "hints": [str(h) for h in (data.get("hints") or [])][:3],
                "next_action": str(data.get("next_action") or ""),
                "citations": cite(ids),
                "safety": "refuse" if str(data.get("safety", "ok")) == "refuse" else "ok",
                "level": level,
                "provider": "cloud",
            }
        except Exception:
            fallback = MockProvider().answer(mode, ctx, chunks, question, level)
            fallback["provider"] = "mock-fallback"
            return fallback

    def _chat(self, messages: List[Dict[str, str]]) -> str:
        url = self.base + "/chat/completions"
        body = json.dumps({
            "model": self.model,
            "messages": messages,
            "temperature": 0.2,
            "max_tokens": 700,
        }, ensure_ascii=False).encode("utf-8")
        req = urllib.request.Request(url, data=body, method="POST")
        req.add_header("Content-Type", "application/json")
        req.add_header("Authorization", "Bearer " + self.key)
        with urllib.request.urlopen(req, timeout=self.timeout) as resp:
            raw = resp.read().decode("utf-8", "replace")
        data = json.loads(raw)
        return data["choices"][0]["message"]["content"]

    @staticmethod
    def _parse(content: str) -> Optional[Dict[str, Any]]:
        text = (content or "").strip()
        text = re.sub(r"^```(?:json)?\s*", "", text, flags=re.I)
        text = re.sub(r"\s*```$", "", text)
        start, end = text.find("{"), text.rfind("}")
        if start < 0 or end <= start:
            return None
        try:
            obj = json.loads(text[start:end + 1])
            return obj if isinstance(obj, dict) else None
        except json.JSONDecodeError:
            return None
