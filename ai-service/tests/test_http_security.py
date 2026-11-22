from __future__ import annotations

import http.client
import sys
import threading
import unittest
from http.server import ThreadingHTTPServer
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT))

import app  # noqa: E402


class HttpSecurityTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.server = ThreadingHTTPServer(("127.0.0.1", 0), app.Handler)
        cls.port = cls.server.server_port
        cls.thread = threading.Thread(target=cls.server.serve_forever, daemon=True)
        cls.thread.start()

    @classmethod
    def tearDownClass(cls):
        cls.server.shutdown()
        cls.server.server_close()
        cls.thread.join(timeout=5)

    def req(self, method, path, headers=None):
        conn = http.client.HTTPConnection("127.0.0.1", self.port, timeout=5)
        conn.request(method, path, headers=headers or {})
        resp = conn.getresponse()
        body = resp.read()
        conn.close()
        return resp, body

    def test_same_origin_allowed(self):
        origin = "http://127.0.0.1:%d" % self.port
        resp, body = self.req("GET", "/health", {"Origin": origin})
        self.assertEqual(resp.status, 200)
        self.assertEqual(resp.getheader("Access-Control-Allow-Origin"), origin)
        self.assertIn(b'"ok": true', body)

    def test_cross_origin_rejected(self):
        resp, body = self.req("GET", "/health", {"Origin": "https://evil.example"})
        self.assertEqual(resp.status, 403)
        self.assertIsNone(resp.getheader("Access-Control-Allow-Origin"))
        self.assertIn(b"forbidden", body)

    def test_bad_host_rejected(self):
        resp, body = self.req("GET", "/health", {"Host": "evil.example:%d" % self.port})
        self.assertEqual(resp.status, 403)

    def test_options_same_origin(self):
        origin = "http://localhost:%d" % self.port
        resp, _ = self.req("OPTIONS", "/v1/ask", {"Origin": origin})
        self.assertEqual(resp.status, 204)
        self.assertEqual(resp.getheader("Access-Control-Allow-Origin"), origin)

    def test_options_cross_origin(self):
        resp, _ = self.req("OPTIONS", "/v1/ask", {"Origin": "https://evil.example"})
        self.assertEqual(resp.status, 403)

    def test_post_cross_origin_rejected(self):
        resp, _ = self.req(
            "POST", "/v1/explain",
            {"Origin": "https://evil.example", "Content-Type": "application/json", "Content-Length": "2"},
        )
        self.assertEqual(resp.status, 403)

    def test_post_requires_json(self):
        origin = "http://127.0.0.1:%d" % self.port
        resp, _ = self.req(
            "POST", "/v1/explain",
            {"Origin": origin, "Content-Type": "text/plain", "Content-Length": "2"},
        )
        self.assertEqual(resp.status, 415)


if __name__ == "__main__":
    unittest.main()
