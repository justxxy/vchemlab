"""Offline 50-case evaluation for the CO2 assistant demo.

Run from the repo root:
    python ai-service/evals/run_eval.py
Exit code 0 when >= 45/50 pass.
"""

from __future__ import annotations

import json
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT))

import service  # noqa: E402

CASES = json.loads((ROOT / "evals" / "cases.json").read_text(encoding="utf-8"))["cases"]


def make_plan(case):
    cls = case.get("class")
    p = {
        "topology": {
            "nodes": [
                {"id": "n1", "type": "reactor.flask_conical"},
                {"id": "n2", "type": "feed.thistle_funnel", "props": {"submerged": True}},
                {"id": "n3", "type": "conduit.bent"},
                {"id": "n4", "type": "collect.gas_jar", "props": {"orientation": "upright", "covered": True}},
            ],
            "edges": [
                {"from": "n2.bottom", "to": "n1.mouth", "kind": "feed"},
                {"from": "n3.in", "to": "n1.mouth", "kind": "gas"},
                {"from": "n3.out", "to": "n4.mouth", "kind": "gas", "props": {"depth": "deep"}},
            ],
        },
        "verification": {"fullness": "burning_splint", "identity": "limewater"},
        "feeds": {"solid": "reagent.caco3_lump", "liquid": "reagent.hcl_dilute"},
    }
    if cls == "R-COL-01":
        p["topology"]["nodes"][1]["props"]["submerged"] = False
    elif cls == "R-COL-02":
        p["topology"]["edges"][2]["props"]["depth"] = "shallow"
    elif cls == "R-COL-03":
        p["topology"]["nodes"][3]["props"]["covered"] = False
    elif cls == "R-COL-04":
        p["topology"]["nodes"][3]["props"]["orientation"] = "inverted"
    elif cls == "R-OK-02":
        p["verification"]["fullness"] = "none"
    elif cls == "R-RXN-01":
        p["feeds"]["liquid"] = "reagent.h2so4_dilute"
    elif cls == "R-RXN-02":
        p["feeds"]["solid"] = "reagent.caco3_powder"
    elif cls == "R-RXN-03":
        p["feeds"]["liquid"] = "reagent.hcl_conc"
    elif cls == "R-RXN-04":
        p["feeds"]["liquid_route"] = "direct"
    elif cls == "R-RXN-05":
        p["feeds"]["solid"] = "reagent.caso4"
    elif cls == "R-NONE-01":
        p["feeds"]["liquid"] = "reagent.water"
    return p


def make_result(case):
    cls = case.get("class")
    return {
        "class": cls,
        "outcome": "success" if cls == "R-OK-01" else "fail",
        "errors": list(case.get("errors") or []),
        "diagnosis": {"summary": "eval"},
        "phenomena": {},
    }


def main() -> int:
    passed = 0
    failures = []
    for c in CASES:
        out = service.handle(
            mode=c["mode"],
            plan=make_plan(c),
            result=make_result(c),
            question=c.get("question", ""),
            level=c.get("level", "L1"),
            keyframe=c.get("keyframe"),
        )
        ok = True
        if not out.get("answer"):
            ok = False
        if out.get("safety") != c.get("expect_safety", "ok"):
            ok = False
        ids = [x.get("id") for x in out.get("citations", [])]
        if c.get("citations") and c["citations"][0] not in ids:
            ok = False
        if c.get("keywords") and not any(k in out.get("answer", "") for k in c["keywords"]):
            ok = False
        if ok:
            passed += 1
        else:
            failures.append((c["id"], out))

    print(f"eval: {passed}/{len(CASES)} passed")
    for cid, out in failures[:10]:
        print(f"FAIL {cid}: {out.get('answer','')[:120]!r} citations={[x.get('id') for x in out.get('citations',[])]} safety={out.get('safety')}")
    return 0 if passed >= 45 else 1


if __name__ == "__main__":
    raise SystemExit(main())
