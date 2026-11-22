"""Zero-dependency HTTP server for the CO2 AI teaching-assistant demo.

Why stdlib instead of FastAPI: the demo must run on a clean Python install with
no pip step.  Endpoints are intentionally small and JSON-only.

Run:
    python ai-service/app.py
    curl http://127.0.0.1:8000/health
"""

from __future__ import annotations

import json
import sys
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path
from typing import Any, Dict
from urllib.parse import parse_qs, urlparse

import config
import service

STATIC_DIR = Path(__file__).resolve().parent / "static"
MAX_BODY = 2 * 1024 * 1024


class Handler(BaseHTTPRequestHandler):
    server_version = "VchemAI/0.1"

    # ---------------------------------------------------------------- helpers

    def _allowed_origins(self):
        port = self.server.server_port
        return {"http://127.0.0.1:%d" % port, "http://localhost:%d" % port}

    def _origin_ok(self) -> bool:
        origin = self.headers.get("Origin", "")
        return (not origin) or (origin in self._allowed_origins())

    def _host_ok(self) -> bool:
        host = (self.headers.get("Host") or "").lower()
        if not host:
            return True
        port = self.server.server_port
        allowed = {
            "127.0.0.1:%d" % port, "localhost:%d" % port, "[::1]:%d" % port,
        }
        return host in allowed

    def _deny(self) -> None:
        # No CORS headers on a denial: a cross-origin caller must not read this.
        body = json.dumps({"error": "forbidden origin or host"}, ensure_ascii=False).encode("utf-8")
        self.send_response(403)
        self.send_header("Content-Type", "application/json; charset=utf-8")
        self.send_header("Content-Length", str(len(body)))
        self.end_headers()
        self.wfile.write(body)

    def _send(self, code: int, payload: Any, content_type: str = "application/json; charset=utf-8") -> None:
        if isinstance(payload, (dict, list)):
            body = json.dumps(payload, ensure_ascii=False).encode("utf-8")
        elif isinstance(payload, str):
            body = payload.encode("utf-8")
        else:
            body = bytes(payload)
        self.send_response(code)
        self.send_header("Content-Type", content_type)
        self.send_header("Content-Length", str(len(body)))
        origin = self.headers.get("Origin", "")
        if origin and origin in self._allowed_origins():
            self.send_header("Access-Control-Allow-Origin", origin)
            self.send_header("Vary", "Origin")
        self.end_headers()
        self.wfile.write(body)

    def _body(self) -> Dict[str, Any]:
        try:
            n = int(self.headers.get("Content-Length", "0"))
        except ValueError:
            n = 0
        if n <= 0 or n > MAX_BODY:
            return {}
        raw = self.rfile.read(n)
        try:
            data = json.loads(raw.decode("utf-8"))
            return data if isinstance(data, dict) else {}
        except Exception:
            return {}

    def _path(self) -> str:
        return urlparse(self.path).path.rstrip("/") or "/"

    # ---------------------------------------------------------------- routes

    def do_OPTIONS(self) -> None:  # noqa: N802
        if not self._host_ok() or not self._origin_ok():
            self._deny()
            return
        self.send_response(204)
        origin = self.headers.get("Origin", "")
        if origin and origin in self._allowed_origins():
            self.send_header("Access-Control-Allow-Origin", origin)
            self.send_header("Vary", "Origin")
        self.send_header("Access-Control-Allow-Headers", "Content-Type")
        self.send_header("Access-Control-Allow-Methods", "GET, POST, OPTIONS")
        self.send_header("Access-Control-Max-Age", "600")
        self.send_header("Content-Length", "0")
        self.end_headers()

    def do_GET(self) -> None:  # noqa: N802
        if not self._host_ok() or not self._origin_ok():
            self._deny()
            return
        path = self._path()
        if path == "/health":
            self._send(200, service.health())
            return
        if path == "/v1/demo/current":
            self._send(200, service.read_current_submission())
            return
        if path == "/v1/kg":
            self._send(200, service.kg_overview())
            return
        if path in ("/v1/kg/entity", "/v1/kg/query"):
            qs = parse_qs(urlparse(self.path).query)
            name = (qs.get("name") or qs.get("entity") or [""])[0]
            depth = int((qs.get("depth") or ["1"])[0])
            relation = (qs.get("relation") or [""])[0]
            self._send(200, service.kg_entity(name, depth=depth, relation=relation))
            return
        if path in ("/", "/index.html"):
            f = STATIC_DIR / "index.html"
            if not f.exists():
                self._send(404, {"error": "index.html not found"})
                return
            self._send(200, f.read_text(encoding="utf-8"), "text/html; charset=utf-8")
            return
        self._send(404, {"error": "not found", "path": path})

    def do_POST(self) -> None:  # noqa: N802
        if not self._host_ok() or not self._origin_ok():
            self._deny()
            return
        ctype = (self.headers.get("Content-Type") or "").split(";")[0].strip().lower()
        if ctype != "application/json":
            # Blocks simple cross-site form posts (text/plain) that skip CORS preflight.
            self._send(415, {"error": "content-type must be application/json"})
            return
        path = self._path()
        modes = {
            "/v1/explain": "explain",
            "/v1/hint": "hint",
            "/v1/ask": "ask",
            "/v1/report": "report",
        }
        mode = modes.get(path)

        if path == "/v1/rag":
            data = self._body()
            try:
                out = service.rag_retrieve(
                    question=str(data.get("question", ""))[:500],
                    result=data.get("result") if isinstance(data.get("result"), dict) else {},
                    keyframe=data.get("keyframe"),
                    top_k=int(data.get("top_k", 4)),
                )
                self._send(200, out)
            except Exception as exc:
                self._send(500, {"error": type(exc).__name__, "message": str(exc)[:200]})
            return

        if not mode:
            self._send(404, {"error": "not found", "path": path})
            return

        data = self._body()
        try:
            out = service.handle(
                mode=mode,
                plan=data.get("plan") if isinstance(data.get("plan"), dict) else {},
                result=data.get("result") if isinstance(data.get("result"), dict) else {},
                timeline=data.get("timeline") if isinstance(data.get("timeline"), dict) else None,
                question=str(data.get("question", ""))[:500],
                level=str(data.get("level", "L1")),
                keyframe=data.get("keyframe"),
            )
            self._send(200, out)
        except Exception as exc:  # keep the demo alive, never leak keys
            self._send(500, {"error": type(exc).__name__, "message": str(exc)[:200]})

    def log_message(self, fmt: str, *args) -> None:  # noqa: A003
        # Do not log request bodies (they may contain student answers).
        sys.stderr.write("%s - %s\n" % (self.address_string(), fmt % args))


def serve(host: str = None, port: int = None) -> None:
    host = host or config.HOST
    port = int(port or config.PORT)
    if host not in ("127.0.0.1", "::1", "localhost"):
        raise SystemExit("refusing non-loopback host: %s" % host)
    httpd = ThreadingHTTPServer((host, port), Handler)
    print(f"[vchem-ai] listening on http://{host}:{port}  provider={config.provider_mode()}  knowledge={len(service.CHUNKS)} chunks")
    try:
        httpd.serve_forever()
    except KeyboardInterrupt:
        pass
    finally:
        httpd.server_close()


if __name__ == "__main__":
    serve()
