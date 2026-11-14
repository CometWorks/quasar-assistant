"""Screenshots of the chat with Markdown answers, for before and after shots of
UI changes. It runs a local Quasar on loopback with this plugin from its built
working copy and a stub model that answers with canned Markdown, asks a short
and a long question on the Assistant page and a long one in the drawer, in the
dark and the light theme, and saves the screenshots. No game server or client
is started, and no real model is called.

    uv run --no-project --with playwright python chat_shots.py OUT_DIR

Build first: Quasar published to CHAT_QUASAR (default: quasar-build next to
this repository) and this repository with
`dotnet build QuasarAssistant.slnx -c Release`. CHAT_PORT sets Quasar's port
(default 25198) and the stub takes the next one. CHAT_CHROMIUM names the
browser (default: chromium on the PATH). Quasar's data folder is
~/.se-test/chat-shots-q, wiped at each start.
"""

from __future__ import annotations

import json
import os
import shutil
import subprocess
import sys
import threading
import time
import urllib.request
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path

from playwright.sync_api import TimeoutError as PlaywrightTimeout
from playwright.sync_api import sync_playwright

from stub_llm import chunks

HERE = Path(__file__).resolve().parent
PLUGIN = HERE.parent.parent
QUASAR = Path(os.environ.get("CHAT_QUASAR", PLUGIN.parent / "quasar-build"))
PORT = int(os.environ.get("CHAT_PORT", "25198"))
STUB_PORT = PORT + 1
ROOT = Path.home() / ".se-test" / "chat-shots-q"
URL = f"http://127.0.0.1:{PORT}"
CHROMIUM = os.environ.get("CHAT_CHROMIUM", shutil.which("chromium"))

SHORT = "The server **Alpha** is running with `12` players online. Its last save was 4 minutes ago."

LONG = """\
## Server health

Alpha has been up for **3 days**, and the simulation speed is *fine* at `0.98`.
Two things need a look:

1. The world save takes 41 s, which is long for its size.
2. Three grids are over the block limit:
   - *Hammerhead* (owner `Kestrel`)
   - *Driller 7* and *Driller 8*

### Players

| Player | Grids | PCU | Last seen |
|:-------|------:|----:|-----------|
| Kestrel | 4 | 18,420 | online |
| Mira | 2 | 6,105 | 2 h ago |
| Ondo | 1 | 950 | 3 days ago |

To keep fewer backups, set this in the server's config:

```xml
<AutoSaveInMinutes>10</AutoSaveInMinutes>
<MaxBackupSaves>5</MaxBackupSaves>
```

> A restart applies it; the players get a warning first.

See [the Quasar book](https://github.com/CometWorks/quasar) for the backup options.
"""


class Stub(BaseHTTPRequestHandler):
    def log_message(self, *args) -> None:
        pass

    def do_POST(self) -> None:
        body = json.loads(self.rfile.read(int(self.headers.get("Content-Length", 0))))
        last = next(m for m in reversed(body["messages"]) if m["role"] == "user")
        text = LONG if "long" in str(last["content"]).lower() else SHORT
        self.send_response(200)
        self.send_header("Content-Type", "text/event-stream")
        self.end_headers()
        for event in chunks(body.get("model", "stub"), None, None, text):
            self.wfile.write(b"data: " + json.dumps(event).encode() + b"\n\n")
            self.wfile.flush()
        self.wfile.write(b"data: [DONE]\n\n")


def prepare() -> None:
    shutil.rmtree(ROOT, ignore_errors=True)
    settings = ROOT / "plugin-data" / "quasar.assistant" / "settings.json"
    settings.parent.mkdir(parents=True)
    backend = {
        "name": "stub",
        "kind": "OpenAi",
        "baseUrl": f"http://127.0.0.1:{STUB_PORT}/v1",
        "model": "stub-model",
    }
    settings.write_text(
        json.dumps({"backends": [backend], "roles": {"admin": {"backends": ["stub"]}}})
    )


def up() -> bool:
    try:
        with urllib.request.urlopen(URL + "/api/health", timeout=5) as response:
            return response.status == 200
    except Exception:  # noqa: BLE001 -- not listening yet
        return False


def ask(page, chat, question: str, answers: int) -> None:
    chat.locator("textarea").fill(question)
    chat.locator("textarea").press("Control+Enter")
    chat.locator(".assistant-message-answer").nth(answers - 1).wait_for()
    chat.locator(".assistant-status").wait_for(state="detached")
    page.wait_for_timeout(300)


def dismiss_setup(page) -> None:
    """Closes the first-start administrator setup dialog, if it shows"""
    cancel = page.get_by_role("button", name="Cancel", exact=True)
    try:
        cancel.wait_for(timeout=5000)
        cancel.click()
    except PlaywrightTimeout:
        pass


def main() -> int:
    out = Path(sys.argv[1]).resolve()
    out.mkdir(parents=True, exist_ok=True)
    prepare()
    stub = ThreadingHTTPServer(("127.0.0.1", STUB_PORT), Stub)
    threading.Thread(target=stub.serve_forever, daemon=True).start()
    env = os.environ | {
        "QUASAR_INSTALL_DIR": str(ROOT),
        "QUASAR_WEB_HOST": "127.0.0.1",
        "QUASAR_WEB_PORT": str(PORT),
        "QUASAR_OPEN_BROWSER_ON_START": "false",
        "QUASAR_HOST_BINARY": "/nonexistent/Quasar.Host",
        "QUASAR_DS_LAUNCH_UPDATE_MODE": "Skip",
        "QUASAR_UPDATES_ENABLED": "false",
        "QUASAR_UPDATES_AUTO_UPDATE_UI_PLUGINS": "false",
        "QUASAR_UI_PLUGIN_DIR": str(PLUGIN),
        "QUASAR_UI_PLUGIN_BUILD_CONFIGURATION": "Release",
    }
    log = open(out / "quasar.log", "w")
    quasar = subprocess.Popen(
        ["dotnet", str(QUASAR / "Quasar.dll"), "--foreground"],
        cwd=QUASAR,
        env=env,
        stdout=log,
        stderr=subprocess.STDOUT,
        start_new_session=True,
    )
    try:
        deadline = time.time() + 180
        while not up():
            if time.time() > deadline or quasar.poll() is not None:
                print(f"FAIL Quasar did not come up, see {out / 'quasar.log'}")
                return 1
            time.sleep(1)
        with sync_playwright() as p:
            browser = p.chromium.launch(executable_path=CHROMIUM)
            for scheme in ("dark", "light"):
                context = browser.new_context(
                    color_scheme=scheme, viewport={"width": 1280, "height": 1500}
                )
                page = context.new_page()
                page.goto(URL + "/assistant", wait_until="networkidle")
                chat = page.locator(".assistant-page-chat")
                ask(page, chat, "Short question: how is Alpha?", 1)
                ask(page, chat, "Long question: full health report", 2)
                page.screenshot(path=out / f"page-{scheme}.png")
                # The drawer on another page, with a new conversation
                page.goto(URL + "/settings/ui-plugins", wait_until="networkidle")
                dismiss_setup(page)
                page.locator(".assistant-launcher").click()
                drawer = page.locator(".assistant-drawer")
                ask(page, drawer, "Long question: full health report", 1)
                drawer.screenshot(path=out / f"drawer-{scheme}.png")
                context.close()
            browser.close()
        print(f"PASS screenshots in {out}")
        return 0
    finally:
        os.killpg(quasar.pid, 15)
        quasar.wait(30)
        stub.shutdown()


if __name__ == "__main__":
    sys.exit(main())
