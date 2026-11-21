"""The settings page and its API on a real Quasar (SE-1799), with screenshots for
before and after shots. It runs a Quasar on loopback with this plugin from its
built working copy, writes settings and a written skill as an older version left
them, and then:

- before: screenshots the settings page as one long page;
- after: screenshots each tab, checks that the old settings and the skill came
  through, drives the settings API (backends and keys, role settings, ZIP, file
  and Git skill sources, refresh, removal, bad requests), uploads a SKILL.md on
  the Support tab and checks that no API key is stored in plain text.

It prints PASS or FAIL per check and a summary line. No game server or model runs.

    uv run --no-project --with playwright python settings_rig.py before|after OUT_DIR

RIG_QUASAR is the published Quasar (default: quasar-build next to this
repository), RIG_PLUGIN the plugin working copy to load (default: this one), RIG_PORT its port (default 25270), RIG_ROOT its data folder
(default ~/.se-test/SE-1799-q, wiped at each start), RIG_GIT_REF the commit of
this repository on GitHub that holds tests/rig/fixture-skills (after only) and
RIG_CHROMIUM the browser (default: chromium on the PATH). Loopback is Quasar's
trusted network, so the rig is an admin without a token.
"""

from __future__ import annotations

import base64
import hashlib
import io
import json
import os
import shutil
import subprocess
import sys
import time
import urllib.error
import urllib.request
import zipfile
from pathlib import Path

from playwright.sync_api import TimeoutError as PlaywrightTimeout
from playwright.sync_api import sync_playwright

HERE = Path(__file__).resolve().parent
PLUGIN = Path(os.environ.get("RIG_PLUGIN", HERE.parent.parent))
FIXTURES = HERE / "fixture-skills"
QUASAR = Path(os.environ.get("RIG_QUASAR", PLUGIN.parent / "quasar-build"))
PORT = int(os.environ.get("RIG_PORT", "25270"))
ROOT = Path(os.environ.get("RIG_ROOT", Path.home() / ".se-test" / "SE-1799-q"))
URL = f"http://127.0.0.1:{PORT}"
API = URL + "/api/v1/assistant"
GIT_REF = os.environ.get("RIG_GIT_REF", "")
CHROMIUM = os.environ.get("RIG_CHROMIUM", shutil.which("chromium"))
SECRET = "sk-rig-not-a-real-key-5e1799"
DATA = ROOT / "plugin-data" / "quasar.assistant"

results: list[tuple[bool, str]] = []


def check(ok: bool, what: str, detail: object = "") -> bool:
    results.append((ok, what))
    print(
        f"{'PASS' if ok else 'FAIL'} {what}{'' if ok or detail == '' else f': {detail}'}",
        flush=True,
    )
    return ok


def call(method: str, path: str, body: object | None = None) -> tuple[int, object]:
    data = None if body is None else json.dumps(body).encode()
    request = urllib.request.Request(API + path, data=data, method=method)
    if data is not None:
        request.add_header("Content-Type", "application/json")
    try:
        with urllib.request.urlopen(request, timeout=200) as response:
            text = response.read().decode()
            return response.status, json.loads(text) if text else None
    except urllib.error.HTTPError as error:
        text = error.read().decode()
        try:
            return error.code, json.loads(text)
        except ValueError:
            return error.code, text


def prepare() -> None:
    """Settings and a written skill as 1.2.0 left them."""
    shutil.rmtree(ROOT, ignore_errors=True)
    DATA.mkdir(parents=True)
    backend = {
        "name": "stub",
        "kind": "OpenAi",
        "baseUrl": "http://127.0.0.1:9/v1",
        "model": "stub-model",
    }
    settings = {
        "backends": [backend],
        "roles": {
            "admin": {"backends": ["stub"], "roleText": "Admin notes from 1.2.0."},
            "player": {"backends": ["stub"], "areaModes": {"grids": "Read"}},
            "monitor": {"backends": ["stub"], "maxConcurrentRuns": 1},
        },
        "player": {"enabled": True, "limits": {"maxMessages": 12}},
        "monitor": {"enabled": False, "checkSeconds": 90},
    }
    (DATA / "settings.json").write_text(json.dumps(settings))
    (DATA / "skills" / "player").mkdir(parents=True)
    (DATA / "skills" / "player" / "faq.md").write_text("# FAQ\n\nWritten in 1.2.0.\n")


def up() -> bool:
    try:
        with urllib.request.urlopen(URL + "/api/health", timeout=5) as response:
            return response.status == 200
    except Exception:  # noqa: BLE001 -- not listening yet
        return False


def dismiss_setup(page) -> None:
    """Closes the first-start administrator setup dialog, if it shows"""
    cancel = page.get_by_role("button", name="Cancel", exact=True)
    try:
        cancel.wait_for(timeout=5000)
        cancel.click()
    except PlaywrightTimeout:
        pass


def fixture_zip() -> bytes:
    buffer = io.BytesIO()
    with zipfile.ZipFile(buffer, "w") as archive:
        for file in sorted(FIXTURES.rglob("*")):
            if file.is_file():
                archive.write(file, "bundle/" + file.relative_to(FIXTURES).as_posix())
        archive.writestr("bundle/logo.png", b"\x89PNG\x00\x00")
    return buffer.getvalue()


def api_checks() -> None:
    status, view = call("GET", "/settings")
    settings = view.get("settings", {}) if isinstance(view, dict) else {}
    check(
        status == 200
        and settings["roles"]["admin"]["roleText"] == "Admin notes from 1.2.0."
        and settings["roles"]["player"]["areaModes"] == {"grids": "Read"}
        and settings["player"]["enabled"] is True
        and settings["player"]["limits"]["maxMessages"] == 12
        and settings["monitor"]["checkSeconds"] == 90,
        "settings of 1.2.0 read back unchanged",
        view,
    )
    status, skills = call("GET", "/skills?role=player")
    check(
        status == 200 and skills[0]["written"] == ["faq"],
        "the written skill of 1.2.0 is still there",
        skills,
    )

    backends = settings["backends"] + [
        {
            "name": "claude",
            "kind": "Anthropic",
            "baseUrl": "https://api.anthropic.com/v1",
            "model": "claude-sonnet-5-5",
            "priority": 2,
        }
    ]
    status, view = call(
        "PUT",
        "/settings/backends",
        {"backends": backends, "apiKeys": {"claude": SECRET}},
    )
    check(
        status == 200 and view["apiKeys"] == ["claude"],
        "PUT backends sets the backends and a key",
        view,
    )
    status, view = call(
        "PUT",
        "/settings/roles/player",
        {
            "settings": {
                "backends": ["claude", "stub"],
                "roleText": "Be kind.",
                "maxTurns": 5,
            },
            "player": {"enabled": True, "privacyNotice": "Rig notice."},
        },
    )
    role = view["settings"]["roles"]["player"] if status == 200 else {}
    check(
        status == 200
        and role.get("backends") == ["claude", "stub"]
        and view["settings"]["player"]["privacyNotice"] == "Rig notice.",
        "PUT roles/player sets the role and player support",
        view,
    )
    status, view = call("PUT", "/settings/roles/admin", {"player": {"enabled": False}})
    check(
        status == 400 and "player role" in json.dumps(view),
        "player settings on the admin role answer 400",
        (status, view),
    )

    archive = fixture_zip()
    status, view = call(
        "PUT",
        "/skills/player/bundle",
        {
            "kind": "zip",
            "fileName": "bundle.zip",
            "content": base64.b64encode(archive).decode(),
        },
    )
    names = (
        sorted(skill["name"] for skill in view.get("skills", []))
        if status == 200
        else []
    )
    world = (
        next((s for s in view.get("skills", []) if s["name"] == "rig-world"), {})
        if status == 200
        else {}
    )
    check(
        status == 200
        and names == ["rig-support", "rig-world"]
        and view["revision"] == hashlib.sha256(archive).hexdigest()
        and world.get("files") == ["SKILL.md", "places.md"],
        "ZIP source resolves both skills, keeps places.md, leaves the PNG out, SHA-256 shown",
        view,
    )
    status, view = call(
        "PUT",
        "/skills/player/again",
        {"kind": "zip", "content": base64.b64encode(archive).decode()},
    )
    check(
        status == 400 and "already has a skill named" in json.dumps(view),
        "a second source with the same skill names answers 400",
        (status, view),
    )

    single = (FIXTURES / "rig-support" / "SKILL.md").read_bytes()
    status, view = call(
        "PUT",
        "/skills/admin/one",
        {"kind": "file", "content": base64.b64encode(single).decode()},
    )
    check(
        status == 200 and [s["name"] for s in view["skills"]] == ["rig-support"],
        "SKILL.md source named by its front matter",
        view,
    )

    if GIT_REF:
        source = {
            "kind": "git",
            "repository": "https://github.com/CometWorks/quasar-assistant.git",
            "ref": GIT_REF,
            "paths": ["tests/rig/fixture-skills/rig-world/SKILL.md"],
        }
        started = time.time()
        status, view = call("PUT", "/skills/monitor/github", source)
        check(
            status == 200
            and view.get("error") is None
            and view.get("revision") == GIT_REF
            and [(s["name"], s["files"]) for s in view["skills"]]
            == [("rig-world", ["SKILL.md", "places.md"])],
            f"Git source fetched at {GIT_REF[:7]} in {time.time() - started:.1f} s",
            view,
        )
        status, view = call("POST", "/skills/monitor/github/refresh")
        check(
            status == 200
            and view.get("revision") == GIT_REF
            and view.get("error") is None,
            "Git source refresh",
            view,
        )
        status, view = call(
            "PUT", "/skills/monitor/broken", source | {"paths": ["no/such/folder"]}
        )
        check(
            status == 200 and "no folder" in (view.get("error") or ""),
            "a missing path is saved with its error",
            view,
        )
        call("DELETE", "/skills/monitor/broken")
    else:
        check(False, "Git source", "RIG_GIT_REF not set")

    status, listed = call("GET", "/skills")
    counts = (
        {role["role"]: len(role["sources"]) for role in listed} if status == 200 else {}
    )
    check(
        counts == {"admin": 1, "player": 1, "monitor": 1 if GIT_REF else 0},
        "GET skills lists the sources per role",
        counts,
    )
    status, _ = call("DELETE", "/skills/admin/one")
    status2, _ = call("DELETE", "/skills/admin/one")
    check(
        (status, status2) == (200, 404),
        "DELETE removes a source, a second DELETE answers 404",
        (status, status2),
    )
    status, _ = call("POST", "/skills/admin/none/refresh")
    check(status == 404, "refresh of an unknown source answers 404", status)

    leaked = [
        str(p)
        for p in ROOT.rglob("*")
        if p.is_file() and SECRET.encode() in p.read_bytes()
    ]
    check(
        not leaked, "the API key is in no file in plain text (keys, audit log)", leaked
    )


def main() -> int:
    mode, out = sys.argv[1], Path(sys.argv[2]).resolve()
    out.mkdir(parents=True, exist_ok=True)
    prepare()
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
    (out / "quasar.pid").write_text(str(quasar.pid))
    try:
        deadline = time.time() + 180
        while not up():
            if time.time() > deadline or quasar.poll() is not None:
                return check(False, "Quasar came up", out / "quasar.log") and 1
            time.sleep(1)
        with sync_playwright() as p:
            browser = p.chromium.launch(executable_path=CHROMIUM)
            for scheme in ("dark", "light") if mode == "after" else ("dark",):
                context = browser.new_context(
                    color_scheme=scheme, viewport={"width": 1280, "height": 900}
                )
                page = context.new_page()
                page.goto(URL + "/assistant/settings", wait_until="networkidle")
                dismiss_setup(page)
                page.locator("h1", has_text="Assistant settings").wait_for()
                if mode == "before":
                    page.screenshot(path=out / f"settings-{scheme}.png", full_page=True)
                else:
                    for tab in ("Providers", "Admin", "Support", "Monitor"):
                        page.get_by_role("tab", name=tab).click()
                        page.wait_for_function(
                            "t => document.querySelector('[role=tab][aria-selected=true]')?.textContent === t",
                            arg=tab,
                        )
                        page.wait_for_timeout(300)
                        page.screenshot(
                            path=out / f"{tab.lower()}-{scheme}.png", full_page=True
                        )
                context.close()
            if mode == "after":
                api_checks()
                context = browser.new_context(
                    color_scheme="dark", viewport={"width": 1280, "height": 900}
                )
                page = context.new_page()
                page.goto(
                    URL + "/assistant/settings?tab=support", wait_until="networkidle"
                )
                dismiss_setup(page)
                check(
                    page.get_by_role("tab", name="Support").get_attribute(
                        "aria-selected"
                    )
                    == "true",
                    "?tab=support opens the Support tab",
                )
                page.get_by_role("button", name="Upload a SKILL.md").click()
                upload = page.locator("form", has_text="One SKILL.md")
                upload.get_by_label("Source name").fill("uploaded")
                # A name no other player source has; the bundle already brings rig-world.
                upload.locator("input[type=file]").set_input_files(
                    {
                        "name": "SKILL.md",
                        "mimeType": "text/markdown",
                        "buffer": b"---\nname: rig-upload\ndescription: Uploaded on the page.\n---\n# Upload\n",
                    }
                )
                upload.get_by_text("SKILL.md, ").wait_for()
                upload.get_by_role("button", name="Save").click()
                row = page.locator("tr").filter(
                    has=page.locator("th code", has_text="uploaded")
                )
                try:
                    row.wait_for(timeout=10000)
                    ok = "rig-upload" in row.inner_text()
                except PlaywrightTimeout:
                    ok = False
                check(
                    ok,
                    "a SKILL.md uploaded on the Support tab shows as a source",
                    page.locator(
                        ".assistant-notice, .assistant-error"
                    ).all_inner_texts(),
                )
                page.screenshot(path=out / "support-sources-dark.png", full_page=True)
                status, listed = call("GET", "/skills?role=player")
                check(
                    status == 200
                    and sorted(s["name"] for s in listed[0]["sources"])
                    == ["bundle", "uploaded"],
                    "the upload is in the API too",
                    listed,
                )
                context.close()
            browser.close()
        failed = [what for ok, what in results if not ok]
        print(
            f"SUMMARY {len(results) - len(failed)}/{len(results)} passed, screenshots in {out}"
        )
        return 1 if failed else 0
    finally:
        os.killpg(quasar.pid, 15)
        quasar.wait(30)


if __name__ == "__main__":
    sys.exit(main())
