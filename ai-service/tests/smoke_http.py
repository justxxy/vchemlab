"""Manual HTTP smoke test.  Requires `python ai-service/app.py` running."""
import json
import urllib.request

BASE = "http://127.0.0.1:8000"


def get(path):
    with urllib.request.urlopen(BASE + path, timeout=10) as r:
        return r.read().decode()


def post(path, obj):
    body = json.dumps(obj, ensure_ascii=False).encode()
    req = urllib.request.Request(BASE + path, data=body, headers={"Content-Type": "application/json"})
    with urllib.request.urlopen(req, timeout=20) as r:
        return json.loads(r.read().decode())


def main():
    print("health", get("/health"))
    cur = json.loads(get("/v1/demo/current"))
    print("current", bool(cur.get("plan")), bool(cur.get("result")))

    plan = {
        "topology": {
            "nodes": [
                {"id": "n1", "type": "reactor.flask_conical"},
                {"id": "n2", "type": "feed.thistle_funnel", "props": {"submerged": False}},
                {"id": "n3", "type": "conduit.bent"},
                {"id": "n4", "type": "collect.gas_jar", "props": {"orientation": "upright", "covered": True}},
            ],
            "edges": [],
        },
        "verification": {"fullness": "burning_splint", "identity": "limewater"},
        "feeds": {"solid": "reagent.caco3_lump", "liquid": "reagent.hcl_dilute"},
    }
    result = {"class": "R-COL-01", "outcome": "fail", "errors": []}

    print("explain", post("/v1/explain", {"plan": plan, "result": result})["answer"][:90])
    hint = post("/v1/hint", {"plan": plan, "result": result, "level": "L3"})
    print("hint", hint["answer"], hint["citations"][0]["id"])
    safe = post("/v1/ask", {"plan": plan, "result": result, "question": "浓硫酸溅到皮肤上怎么办？"})
    print("safety", safe["safety"], safe["provider"])
    ask = post("/v1/ask", {"plan": plan, "result": result, "question": "木条为什么会熄灭？", "keyframe": "verify"})
    print("ask", ask["answer"][:80])
    rag = post("/v1/rag", {"question": "为什么漏斗要插到液面以下？", "result": result, "top_k": 3})
    print("rag", [(c["id"], c["score"]) for c in rag["chunks"]])
    print("rag_kg", rag.get("kg_entities"), rag.get("kg_boost_chunks"))
    kg = json.loads(get("/v1/kg"))
    print("kg", kg["entities"], "entities", kg["triples"], "triples")
    nb = json.loads(get("/v1/kg/entity?name=%E6%BC%8F%E6%96%97&depth=1"))
    print("kg_neighbors", nb["entity"], len(nb["nodes"]), "nodes", len(nb["edges"]), "edges")
    print("index bytes", len(get("/")))


if __name__ == "__main__":
    main()
