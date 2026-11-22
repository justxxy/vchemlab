"""Runtime configuration.  Secrets come from ai-service/.env (gitignored)."""

from __future__ import annotations

import os
from pathlib import Path

BASE_DIR = Path(__file__).resolve().parent
PROJECT_DIR = BASE_DIR.parent
WORKSPACE_DIR = PROJECT_DIR.parent.parent          # .../vchem
RUN_DIR = WORKSPACE_DIR / ".workbuddy" / "artifacts" / "runs" / "current"


def _load_dotenv() -> None:
    env = BASE_DIR / ".env"
    if not env.exists():
        return
    for raw in env.read_text(encoding="utf-8").splitlines():
        line = raw.strip()
        if not line or line.startswith("#") or "=" not in line:
            continue
        k, v = line.split("=", 1)
        k = k.strip()
        v = v.strip().strip('"').strip("'")
        if k and k not in os.environ:
            os.environ[k] = v


_load_dotenv()

HOST = os.environ.get("AI_HOST", "127.0.0.1")
PORT = int(os.environ.get("AI_PORT", "8000"))
_LOOPBACK = ("127.0.0.1", "::1", "localhost")
if HOST not in _LOOPBACK:
    raise SystemExit(
        "AI_HOST must be loopback (127.0.0.1 / ::1 / localhost); "
        "refusing to expose the local demo service to the network"
    )
LLM_BASE_URL = os.environ.get("LLM_BASE_URL", "").rstrip("/")
LLM_API_KEY = os.environ.get("LLM_API_KEY", "")
LLM_MODEL = os.environ.get("LLM_MODEL", "deepseek-chat")
LLM_TIMEOUT = float(os.environ.get("LLM_TIMEOUT", "30"))


def provider_mode() -> str:
    return "cloud" if (LLM_BASE_URL and LLM_API_KEY) else "mock"
