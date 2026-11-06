"""Starts the sandbox server and checks it end to end over MCP. Prints PASS or FAIL per check.

Usage: python3 smoke.py   (Linux with bubblewrap; builds nothing, run `dotnet build` first)
"""

import json
import os
import pathlib
import secrets
import socket
import subprocess
import sys
import tempfile
import time
import urllib.request

HERE = pathlib.Path(__file__).resolve().parent
DLL = next(
    (HERE.parent / "QuasarSandbox" / "bin" / "Debug").glob("net*/QuasarSandbox.dll")
)
TOKEN = secrets.token_hex(16)


def free_port():
    with socket.socket() as s:
        s.bind(("127.0.0.1", 0))
        return s.getsockname()[1]


PORT = free_port()
ROOT = tempfile.mkdtemp(prefix="sandbox-smoke-")
URL = f"http://127.0.0.1:{PORT}/mcp"
failed = []


def rpc(method, params=None, token=TOKEN, conversation="smoke-1"):
    if method == "tools/call" and conversation:
        params = dict(params, _meta={"quasar.assistant/conversation": conversation})
    body = json.dumps(
        {"jsonrpc": "2.0", "id": 1, "method": method, "params": params or {}}
    ).encode()
    request = urllib.request.Request(
        URL,
        body,
        {"Content-Type": "application/json", "Authorization": f"Bearer {token}"},
    )
    with urllib.request.urlopen(request, timeout=120) as response:
        return json.load(response)["result"]


def run(language, code, conversation="smoke-1", **extra):
    result = rpc(
        "tools/call",
        {"name": "run_code", "arguments": dict(language=language, code=code, **extra)},
        conversation=conversation,
    )
    return (
        result.get("structuredContent") or {},
        result["content"][0]["text"],
        result["isError"],
    )


def check(name, ok, detail=""):
    print(("PASS " if ok else "FAIL ") + name + ("" if ok else f": {detail}"))
    if not ok:
        failed.append(name)


env = dict(
    os.environ,
    Sandbox__Token=TOKEN,
    Sandbox__WorkRoot=ROOT,
    Sandbox__MemoryMb="256",
    Sandbox__DiskMb="8",
    Sandbox__OutputKb="16",
    Urls=f"http://127.0.0.1:{PORT}",
)
server = subprocess.Popen(
    ["dotnet", str(DLL)], env=env, stdout=subprocess.DEVNULL, stderr=subprocess.PIPE
)
try:
    for _ in range(100):
        try:
            rpc("ping")
            break
        except Exception:
            time.sleep(0.2)
    try:
        rpc("ping", token="wrong")
        check("wrong token refused", False, "accepted")
    except urllib.error.HTTPError as error:
        check("wrong token refused", error.code == 401, error.code)

    names = [tool["name"] for tool in rpc("tools/list")["tools"]]
    check("tools listed", names == ["run_code", "read_file"], names)

    out, text, _ = run(
        "python", "import sys; print('hi'); print('oops', file=sys.stderr); sys.exit(3)"
    )
    check(
        "python output and exit code",
        out.get("stdout") == "hi\n"
        and out.get("stderr") == "oops\n"
        and out.get("exitCode") == 3,
        text,
    )

    out, text, _ = run(
        "csharp",
        'Console.WriteLine($"sum {Enumerable.Range(1, 4).Sum()}"); File.WriteAllText("cs.txt", "from C#"); return 5;',
    )
    check(
        "csharp output, file and exit code",
        out.get("stdout") == "sum 10\n"
        and out.get("exitCode") == 5
        and any(f["path"] == "cs.txt" for f in out.get("changed", [])),
        text,
    )

    out, text, _ = run("csharp", "int x = ;")
    check(
        "csharp compile error",
        out.get("exitCode") == 1 and "Compilation failed" in out.get("stderr", ""),
        text,
    )

    out, text, _ = run(
        "python",
        "print(open('data/in.txt').read()); open('result.csv','w').write('a,b\\n1,2\\n')",
        files=[{"path": "data/in.txt", "content": "input!"}],
    )
    check(
        "input file read, output file listed",
        "input!" in out.get("stdout", "")
        and [f["path"] for f in out.get("changed", [])] == ["result.csv"],
        text,
    )

    out, text, _ = run("python", "import os; print(sorted(os.listdir('.')))")
    check(
        "workspace kept within the conversation",
        "cs.txt" in out.get("stdout", "") and "result.csv" in out.get("stdout", ""),
        text,
    )

    out, text, _ = run(
        "python", "import os; print(os.listdir('.'))", conversation="smoke-2"
    )
    check("other conversation sees nothing", out.get("stdout") == "[]\n", text)

    read = rpc(
        "tools/call",
        {"name": "read_file", "arguments": {"path": "result.csv"}},
        conversation="smoke-1",
    )
    check(
        "read_file returns the file", read["content"][0]["text"] == "a,b\n1,2\n", read
    )
    read = rpc(
        "tools/call",
        {"name": "read_file", "arguments": {"path": "../../etc/passwd"}},
        conversation="smoke-1",
    )
    check("read_file refuses paths outside", read["isError"], read)

    out, text, _ = run(
        "python",
        "import socket\ntry:\n socket.create_connection(('1.1.1.1', 53), 3); print('open')\nexcept OSError as e: print('blocked')",
    )
    check("no network (python)", out.get("stdout") == "blocked\n", text)
    out, text, _ = run(
        "csharp",
        'try { await new System.Net.Http.HttpClient().GetStringAsync("http://1.1.1.1"); Console.WriteLine("open"); } catch (Exception) { Console.WriteLine("blocked"); }',
    )
    check("no network (csharp)", out.get("stdout") == "blocked\n", text)

    home = os.path.expanduser("~")
    out, text, _ = run(
        "python",
        f"import os\nfor p in ['{home}', '/etc/passwd', '{ROOT}']:\n print(p, os.path.exists(p))\ntry:\n open('/usr/x','w')\nexcept OSError: print('usr read-only')",
    )
    check(
        "host files hidden, /usr read-only",
        "True" not in out.get("stdout", "")
        and "usr read-only" in out.get("stdout", ""),
        text,
    )

    out, text, _ = run("python", "x = bytearray(400 * 1024 * 1024); print('allocated')")
    check(
        "memory limit (python)",
        "allocated" not in out.get("stdout", "") and out.get("exitCode") != 0,
        text,
    )
    out, text, _ = run(
        "csharp",
        'var x = new byte[400 * 1024 * 1024]; x[^1] = 1; Console.WriteLine("allocated");',
    )
    check(
        "memory limit (csharp)",
        "allocated" not in out.get("stdout", "") and out.get("exitCode") != 0,
        text,
    )

    out, text, _ = run(
        "python",
        "open('big','wb').write(b'0' * 20 * 1024 * 1024)",
        conversation="smoke-disk",
    )
    check(
        "disk limit",
        out.get("exitCode") != 0 and "No space" in out.get("stderr", ""),
        text,
    )

    out, text, _ = run("python", "print('x' * 100000)")
    check(
        "output cut at the limit",
        out.get("stdoutCut") is True and len(out.get("stdout", "")) <= 16 * 1024,
        text[:200],
    )

    started = time.time()
    out, text, _ = run("python", "import time; time.sleep(60)", timeoutSeconds=2)
    check(
        "caller's timeout",
        out.get("timedOut") is True and time.time() - started < 20,
        text,
    )

    out, text, _ = run(
        "python",
        "import os\nn=0\ntry:\n while True:\n  if os.fork() == 0:\n   import time; time.sleep(30)\n  n+=1\nexcept OSError: print('stopped at', n)",
        timeoutSeconds=5,
    )
    check(
        "fork bomb stops",
        out.get("timedOut") or "stopped at" in out.get("stdout", ""),
        text,
    )

    out, text, _ = run("python", "print('one-off')", conversation=None)
    check(
        "run without a conversation",
        out.get("stdout") == "one-off\n" and "already gone" in text,
        text,
    )
finally:
    server.terminate()
    server.wait(10)
    subprocess.run(["rm", "-rf", ROOT])

print(f"{len(failed)} failed" if failed else "all passed")
sys.exit(1 if failed else 0)
