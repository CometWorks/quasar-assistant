# Quasar Sandbox

An optional MCP server that runs Python or C# for the assistant, with no network. Admins add it as a remote MCP server to a role, and the role's agent can then compute, convert or check things in code: parse a log excerpt, total up a CSV, test a regular expression.

It is a separate program, not part of the plugin, and runs on Linux only.

## What a run gets

- **Tools.** `run_code` takes `language` (`python` or `csharp`), `code`, optional input `files` (text or base64) and `timeoutSeconds`. It returns the exit code, stdout, stderr and the files the run created or changed. `read_file` reads a file of the workspace 12 KiB at a time.
- **Workspace.** It is per conversation: the plugin sends the conversation in `_meta`, and the files stay there between runs. A workspace unused for 24 hours is deleted. A call without a conversation gets a fresh workspace that is gone right after the call.
- **Python** is the host's `python3` with its standard library and whatever packages are installed system-wide, run in isolated mode.
- **C#** is one file with top-level statements, compiled with Roslyn inside the sandbox against the .NET base library. There are no NuGet packages. A C# run gets 256 MB extra memory for the compiler.

## Isolation

Each run is a [bubblewrap](https://github.com/containers/bubblewrap) sandbox:

- **Namespaces.** Every namespace is unshared: no network, its own PIDs, no host processes, all capabilities dropped.
- **Files.** It sees only `/usr`, the .NET runtime and this server's folder, all read-only, plus its own tmpfs folders. Host files, other workspaces and the server's configuration are not there.
- **Workspace.** It is copied into a tmpfs of the disk limit. The run's files come back to the server as a tar stream, which the server reads with a size cap and unpacks as plain files only, never links.
- **Limits.** These apply per run:

  | Limit | Default | How it is enforced |
  |---|---|---|
  | Timeout | the caller's, up to 90 s (30 s if none is given) | `timeout` inside the sandbox; the server kills the whole sandbox 30 s later at the latest |
  | Memory | 512 MB | a cgroup (`systemd-run --user --scope`, with no swap) and an address-space limit per process |
  | Disk | 64 MB | the size of the tmpfs workspace |
  | Output | 64 KiB each of stdout and stderr | sized tmpfs files; the rest is cut |
  | Processes and threads | 64 | the cgroup |
  | CPU | one core | the cgroup |
  | Parallel runs | 2 | the server; runs of the same conversation take turns |

Without a working `systemd-run --user`, only the address space of each process is limited, not the number of processes or their sum. The server then refuses to start unless `Sandbox:RequireCgroups` is `false`.

## Running it

Needs Linux with `bwrap`, `python3`, a user systemd session for the user it runs as, and the .NET 10 runtime (ASP.NET Core).

```bash
dotnet publish sandbox/QuasarSandbox -c Release -o /opt/quasar-sandbox
sudo loginctl enable-linger quasar   # the user it runs as, so its systemd user session outlives logins
Sandbox__Token="$(openssl rand -hex 24)" dotnet /opt/quasar-sandbox/QuasarSandbox.dll
```

The settings are in `appsettings.json` under `Sandbox`, or in environment variables such as `Sandbox__MemoryMb`. It listens on `http://127.0.0.1:8765` (`Urls`). Keep it on loopback or a private network; the token is the only access check.

Quasar on Windows can use a sandbox on a Linux machine or VM over HTTP.

## In the assistant

On the assistant settings page, add an MCP server to the admin assistant or the player agent:

- **URL:** `http://127.0.0.1:8765/mcp`
- **Header:** `Authorization: Bearer <token>`

Mark `run_code` and `read_file` read-only. Otherwise every run becomes a proposal an admin has to approve. A run can't change anything in Quasar or reach the network; it only changes its own workspace.

## Testing

`tests/smoke.py` starts the server with small limits and prints PASS or FAIL for each check: output and exit codes, the C# compiler, input and output files, workspaces per conversation, `read_file` paths, no network, hidden host files, memory, disk, output, timeout, a fork bomb and a run without a conversation.

```bash
dotnet build sandbox/QuasarSandbox && python3 sandbox/tests/smoke.py
```
