"""A scripted stand-in for an OpenAI-compatible model: POST /v1/chat/completions
with streaming, as the plugin's OpenAI client sends it. No real model is ever
called.

It answers from the last user message of the request (the text after the
[host] lines) and from the tool results that came back since it:

    hello                    a plain answer
    restore my other ship    my_grid_restore with the scenario's other_ship
    restore my ship          my_grid_restore with the scenario's loss_ship
    check my grids           my_grids, then my_grid_restore with whatever grid id
                             a grid name tells it to restore (it obeys injections)
    kick                     the admin tool for players.kick, on the scenario's
                             server and player
    anything else            a plain answer

After a tool result it answers with the start of that result, so the rig can
read the outcome in the whisper or the chat. The scenario is a JSON file the
rig writes and may change between requests. Every request is appended to the
record file as one JSON line: the role (by its system prompt), the system
prompt, the tool definitions, the last user message and the messages since it,
and the Authorization header's last characters.

    python3 stub_llm.py --port 25190 --scenario scenario.json --record requests.jsonl
"""

from __future__ import annotations

import argparse
import json
import re
import threading
import time
import uuid
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path

INJECTION = re.compile(r"restore grid (\d+)", re.IGNORECASE)


def role_of(system: str) -> str:
    return (
        "player"
        if "support assistant of a Space Engineers server" in system
        else "admin"
    )


def tool_named(tools: list[dict], *parts: str) -> str | None:
    """The first tool whose name holds every part"""
    for tool in tools:
        name = tool["function"]["name"]
        if all(p in name for p in parts):
            return name
    return None


def plan(body: dict, scenario: dict) -> tuple[str | None, dict | None, str]:
    """What to answer: (tool name, arguments, text). A tool call has no text."""
    messages = body["messages"]
    tools = body.get("tools") or []
    last_user = max(i for i, m in enumerate(messages) if m["role"] == "user")
    asked = messages[last_user]["content"] or ""
    # The player's or admin's own words, after the [host] lines
    text = "\n".join(
        l for l in asked.splitlines() if not l.startswith("[host]")
    ).lower()
    results = [m for m in messages[last_user + 1 :] if m["role"] == "tool"]
    last = results[-1]["content"] if results else None

    if "check my grids" in text:
        if not results:
            return "my_grids", {}, ""
        if len(results) == 1:
            found = INJECTION.search(last or "")
            if found:
                return "my_grid_restore", {"gridId": int(found.group(1))}, ""
            return None, None, "No grid name asked me for anything. " + last[:200]
        return None, None, "Done what the grid name said: " + last[:300]

    for phrase, key in (
        ("restore my other ship", "other_ship"),
        ("restore my ship", "loss_ship"),
    ):
        if phrase in text:
            if not results:
                return "my_grid_restore", {"gridId": scenario[key]}, ""
            return None, None, "Restore result: " + last[:300]

    if "kick" in text:
        name = tool_named(tools, "players", "kick")
        if not results and name:
            return (
                name,
                {"uniqueName": scenario["server"], "steamId": scenario["player"]},
                "",
            )
        if not name:
            return None, None, "I have no kick tool."
        return None, None, "Kick result: " + last[:300]

    if "hello" in text:
        return None, None, "Hello from the stub assistant."
    return None, None, "The stub has no script for that."


def chunks(model: str, tool: str | None, arguments: dict | None, text: str):
    base = {
        "id": "stub-" + uuid.uuid4().hex[:8],
        "object": "chat.completion.chunk",
        "model": model,
    }

    def chunk(delta: dict, finish: str | None = None) -> dict:
        return {
            **base,
            "choices": [{"index": 0, "delta": delta, "finish_reason": finish}],
        }

    yield chunk({"role": "assistant"})
    if tool:
        call = {
            "index": 0,
            "id": "call_" + uuid.uuid4().hex[:8],
            "type": "function",
            "function": {"name": tool, "arguments": ""},
        }
        yield chunk({"tool_calls": [call]})
        args = json.dumps(arguments)
        for i in range(0, len(args), 16):
            yield chunk(
                {
                    "tool_calls": [
                        {"index": 0, "function": {"arguments": args[i : i + 16]}}
                    ]
                }
            )
        yield chunk({}, "tool_calls")
    else:
        for i in range(0, len(text), 40):
            yield chunk({"content": text[i : i + 40]})
        yield chunk({}, "stop")
    yield {
        **base,
        "choices": [],
        "usage": {
            "prompt_tokens": 1000,
            "completion_tokens": 20,
            "prompt_tokens_details": {"cached_tokens": 900},
        },
    }


class Handler(BaseHTTPRequestHandler):
    server_version = "StubLLM/1"
    lock = threading.Lock()
    scenario_path: Path
    record_path: Path

    def log_message(self, *args) -> None:
        pass

    def do_GET(self) -> None:
        self._json(200, {"data": [{"id": "stub-model", "object": "model"}]})

    def do_POST(self) -> None:
        if not self.path.rstrip("/").endswith("/chat/completions"):
            self._json(404, {"error": {"message": "not found"}})
            return
        raw = self.rfile.read(int(self.headers.get("Content-Length", 0)))
        body = json.loads(raw)
        scenario = (
            json.loads(self.scenario_path.read_text())
            if self.scenario_path.exists()
            else {}
        )
        messages = body["messages"]
        system = (
            messages[0]["content"]
            if messages and messages[0]["role"] == "system"
            else ""
        )
        tool, arguments, text = plan(body, scenario)
        last_user = max(i for i, m in enumerate(messages) if m["role"] == "user")
        auth = self.headers.get("Authorization", "")
        record = {
            "time": time.time(),
            "role": role_of(system),
            "system": system,
            "tools": body.get("tools") or [],
            "user": messages[last_user]["content"],
            "after_user": messages[last_user + 1 :],
            "messages": len(messages),
            "auth_tail": auth[-6:] if auth else "",
            "answer": {"tool": tool, "arguments": arguments, "text": text},
        }
        with self.lock, open(self.record_path, "a") as out:
            out.write(json.dumps(record) + "\n")

        self.send_response(200)
        self.send_header("Content-Type", "text/event-stream")
        self.send_header("Cache-Control", "no-cache")
        self.end_headers()
        for event in chunks(body.get("model", "stub-model"), tool, arguments, text):
            self.wfile.write(b"data: " + json.dumps(event).encode() + b"\n\n")
            self.wfile.flush()
        self.wfile.write(b"data: [DONE]\n\n")
        self.wfile.flush()

    def _json(self, status: int, value: dict) -> None:
        data = json.dumps(value).encode()
        self.send_response(status)
        self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(data)))
        self.end_headers()
        self.wfile.write(data)


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("--port", type=int, required=True)
    parser.add_argument("--scenario", type=Path, required=True)
    parser.add_argument("--record", type=Path, required=True)
    args = parser.parse_args()
    Handler.scenario_path = args.scenario
    Handler.record_path = args.record
    ThreadingHTTPServer(("127.0.0.1", args.port), Handler).serve_forever()


if __name__ == "__main__":
    main()
