from __future__ import annotations

import sys
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT))

import service  # noqa: E402


def plan(**kw):
    p = {
        "schema": "vchem.plan/1",
        "topology": {
            "nodes": [
                {"id": "n1", "type": "reactor.flask_conical"},
                {"id": "n2", "type": "feed.thistle_funnel", "props": {"submerged": kw.get("submerged", True)}},
                {"id": "n3", "type": "conduit.bent"},
                {"id": "n4", "type": "collect.gas_jar", "props": {"orientation": kw.get("orientation", "upright"), "covered": kw.get("covered", True)}},
            ],
            "edges": [
                {"from": "n2.bottom", "to": "n1.mouth", "kind": "feed"},
                {"from": "n3.in", "to": "n1.mouth", "kind": "gas"},
                {"from": "n3.out", "to": "n4.mouth", "kind": "gas", "props": {"depth": kw.get("depth", "deep")}},
            ],
        },
        "verification": {"fullness": kw.get("fullness", "burning_splint"), "identity": kw.get("identity", "limewater")},
        "feeds": {"solid": kw.get("solid", "reagent.caco3_lump"), "liquid": kw.get("liquid", "reagent.hcl_dilute")},
    }
    return p


def result(cls: str, errors=None):
    return {"schema": "vchem.result/1", "class": cls, "outcome": "fail", "errors": errors or [], "diagnosis": {"summary": "test"}}


class AssistantTests(unittest.TestCase):
    def test_explain_unsealed(self):
        out = service.handle("explain", plan(submerged=False), result("R-COL-01"))
        self.assertIn("液封", out["answer"])
        self.assertIn("k-seal", [c["id"] for c in out["citations"]])

    def test_explain_caso4(self):
        out = service.handle("explain", plan(solid="reagent.caso4"), result("R-RXN-05"))
        self.assertTrue("硫酸钙" in out["answer"] or "不反应" in out["answer"])
        self.assertIn("k-caso4", [c["id"] for c in out["citations"]])

    def test_hint_levels(self):
        l1 = service.handle("hint", plan(submerged=False), result("R-COL-01"), level="L1")
        l3 = service.handle("hint", plan(submerged=False), result("R-COL-01"), level="L3")
        self.assertIn("漏斗", l1["answer"])
        self.assertIn("拖到", l3["answer"])

    def test_ask_keyframe(self):
        out = service.handle("ask", plan(), result("R-OK-01"), question="木条为什么会熄灭？", keyframe="verify")
        self.assertIn("木条", out["answer"])
        self.assertIn("k-splint", [c["id"] for c in out["citations"]])

    def test_safety_refusal(self):
        out = service.handle("ask", plan(), result("R-OK-01"), question="浓硫酸溅到皮肤上怎么处理？")
        self.assertEqual(out["safety"], "refuse")
        self.assertEqual(out["provider"], "policy")

    def test_partial_reason(self):
        out = service.handle("explain", plan(fullness="none"), result("R-OK-02"))
        self.assertIn("缺验满", out["answer"])

    def test_structural_errors(self):
        out = service.handle("explain", {}, result("", ["E-STR-02", "E-STR-07"]))
        self.assertIn("结构", out["answer"])
        self.assertTrue(out["next_action"])

    def test_report(self):
        out = service.handle("report", plan(), result("R-OK-01"))
        self.assertIn("掌握点", out["answer"])
        self.assertIn("下一步", out["answer"])

    def test_rag_retrieval_trace(self):
        out = service.rag_retrieve(
            question="为什么漏斗要插到液面以下？",
            result=result("R-COL-01"),
            top_k=3,
        )
        ids = [c["id"] for c in out["chunks"]]
        self.assertIn("k-seal", ids)
        self.assertGreater(out["chunks"][0]["score"], 0)
        self.assertIn("[k-seal]", out["prompt_context"])
        self.assertIn("k-seal", [c["id"] for c in out["citations"]])

    def test_rag_keyframe_trace(self):
        out = service.rag_retrieve(
            question="木条为什么会熄灭？",
            result=result("R-OK-01"),
            keyframe="verify",
            top_k=4,
        )
        ids = [c["id"] for c in out["chunks"]]
        self.assertIn("k-splint", ids)
        self.assertIn("木条", out["answer"])

    def test_rag_kg_linkage(self):
        out = service.rag_retrieve(
            question="长颈漏斗为什么要液封？",
            result=result("R-COL-01"),
            top_k=4,
        )
        self.assertIn("e-funnel", out["kg_entities"])
        self.assertTrue(out["kg_boost_chunks"])
        self.assertTrue(out["kg_hops"])

    def test_kg_overview(self):
        out = service.kg_overview()
        self.assertGreaterEqual(out["entities"], 15)
        self.assertGreaterEqual(out["triples"], 18)

    def test_kg_entity_neighbors(self):
        out = service.kg_entity("漏斗", depth=1)
        self.assertTrue(out["found"])
        self.assertIn("e-funnel", [n["id"] for n in out["nodes"]])
        self.assertIn("k-seal", out["chunks"])
        self.assertTrue(out["edges"])

    def test_kg_query_relation(self):
        out = service.kg_entity("木条", relation="用于验满")
        self.assertTrue(out["triples"])
        self.assertEqual(out["triples"][0]["p"], "用于验满")


if __name__ == "__main__":
    unittest.main()
