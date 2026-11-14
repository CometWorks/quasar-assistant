# Quasar Assistant

An assistant for [Quasar](https://github.com/CometWorks/quasar), the control panel of Space Engineers
dedicated servers and clusters. Admins chat with it in a drawer on every Quasar page, on its own page and in
the admin Discord channel. It answers from Quasar's operations: it reads server status, logs, settings and
players, and it changes things or proposes changes as the admins allow. Players ask it with `!ask` in game and
`/ask` on Discord about the server and their own grids, and it can restore a lost grid. A monitor watches the
servers and clusters and calls a model only when something goes wrong.

It is a Quasar UI plugin (id `quasar.assistant`). It runs inside Quasar and talks to the model providers the
admins set up. No provider or model ships with it.

## What it does

- **Models.** Each backend is one model at one provider: an OpenAI-compatible API (OpenRouter, OpenAI,
  llama.cpp, vLLM) or the Anthropic Messages API. The admin assistant and the player agent each try their
  own backends in a set order. A
  backend that fails (connection error, HTTP 401, 402, 403, 404, 408, 429 or 5xx, an error inside the stream,
  no data within its first-byte timeout, a stream that stalls) cools down, for twice as long after each failure
  in a row up to a maximum, or for the time its `Retry-After` asks. The same request goes to the next backend
  at once. A turn cut off by `max_tokens` before any answer goes to the next backend too, without a cooldown.
  These are the rules of Clangy, our Discord support bot.
- **Tools.** The assistant's tools are the operations registered in Quasar. The admins set a mode per area
  of operations, and per operation if needed: `off`, `read`, `propose` or `apply`. Reads default to `read`,
  changes to `propose`. Operations that Quasar closes to agents (security, RBAC, updates, credentials, the
  assistant's own settings and others) are never offered.
- **Permissions.** Each call runs as Quasar's assistant principal acting for the person who asked. Quasar
  checks its deny list, the assistant's role and the asker's own policies, and writes the audit log. The
  assistant never does more than the asker could do by hand.
- **Proposals.** In `propose` mode a change becomes a proposal in the audit log. Any admin whose permissions
  allow the operation approves or rejects it, as a card in the conversation or on the Approvals page. The
  outcome goes back into the conversation. Admins who could approve also get a web push notice, unless the
  settings turn it off. Proposals expire after 30 days.
- **Knowledge.** The Quasar book, the admins' handbook from the Quasar repository, ships with the plugin.
  Admins add Markdown skills per role, such as a FAQ for players. The agents find and read them with
  `skills_list`, `docs_search` and `skill_read`; none of it is in the prompt until they do. Players get the
  book only when the admins allow it.
- **Lore.** Each server and cluster can have a lore, the world's story, as free text or as a skill folder in a
  Git repository. The agents read it as `lore/…` pages with the same tools, so players' lore questions get
  answers from it. See [Lore](#lore).
- **MCP servers.** Each role can have remote MCP servers (streamable HTTP; no local servers). Their tools
  appear as `mcp_<server>_<tool>`. Every call goes through a Quasar operation, so it is checked and audited:
  `quasar.assistant/mcp.read` and `mcp.call` for the admin assistant and `mcp.monitor.read` and `mcp.monitor.call`
  for the monitor (server control by default), and `mcp.player.read` and `mcp.player.call` for the player agent
  (`CanActAsPlayer`, which only the player agent meets). Each handler accepts only its own role's agent. Tools
  the admins mark read-only follow the read mode; any other MCP tool is always a proposal and can't be undone.
- **Code execution.** With the optional [Quasar Sandbox](sandbox/README.md) set up, a role can run Python and
  C# with no network as the MCP server `sandbox`. Each role has a switch for it: on for the admin assistant and
  the monitor, off for the player agent by default. Its two tools only change the sandbox's own workspace, so
  they follow the read mode, and its token is stored encrypted like the API keys.
- **Data.** Tool results have passwords, tokens and keys replaced by `<redacted>` and are cut to 16 KiB.
  Conversations are kept per person and deleted 30 days after their last message.
- **Caching.** Every request of a role starts with the same bytes: the system prompt, the admins' notes and
  the tools in name order. The time, the asker and the page go into the user turn as `[host]` lines, so the
  providers' prompt caches hit.
- **Cost.** Quasar sets no spending limit for the admin assistant; set one at the model provider. In a test with
  Claude Sonnet 5.5 on 2026-11-06, 13 questions (8 from a player in game, 5 from an admin) each took 3 to 8
  seconds and 1 to 3 model calls. 87% of the input tokens were read from the cache, over 90% in most runs once it
  was warm, and the 13 answers cost about USD 0.12 together. Long conversations and large log reads cost more.

## Player support

The plugin declares two chat commands: `ask` for players (`!ask` in game on every server, `/ask` on Discord)
and `assistant` for admins (`/assistant` on Discord). Quasar carries them: it registers them in game and on
Discord, authenticates the caller, runs `!link`, builds the player identity and delivers the replies. The
plugin only answers.

Player support is off until an admin turns it on. For each `!ask` or `/ask`, in this order, and without a
model run before the last step:

1. Off: the player is told the assistant isn't enabled.
2. Limits: each player has up to 24 questions by default and starts with all of them. Each question uses one,
   and one comes back every hour, never more than 24. So a few questions in a row are fine, but not a steady
   stream. They are counted by Steam ID (or Discord ID without a link), and the admins set the maximum, how many
   come back and how often. A player with none left gets a fixed reply saying when the next one comes back.
3. Privacy notice: on the first request and once a month, the admins' text (by default it names the
   providers of the player role's backends).
4. The player agent runs in its own pool (2 at once, 60 s queue wait by default) with the `my.*` tools the
   modes allow, `ask_admin` and the skill tools. A Discord user without a link gets no `my.*` tools.
5. The answer is whispered in game or posted on Discord.

Quasar builds the player agent from the game session or the Discord link, and the `my.*` operations only
reach that player's own things. Quasar provides `my.server.info` and `my.grid.locate`. The plugin provides
the Grid Backups ones, `my.grids`, `my.grid.backups`, `my.grid.losses` and `my.grid.restore`, under its own ID
(`quasar.assistant/my.grid.restore`). They talk to the Grid Backups plugin on the servers through Quasar's
companion channel and ask Quasar which cluster node has the player, a grid or a position. Without a
server name they use the one the player asked from. Changes the player agent makes or proposes count against 3 actions a day.
When every backend fails, the player hears that the assistant is unavailable and the admins get a hand-over.
When an admin approves or rejects one of the player's proposals, or it expires, the player is told at the
start of their next request, before the model runs. Quasar can't message a player outside a request, so
nothing reaches them sooner. `ask_admin` hands the conversation to the admins: it is listed on the Approvals page, pushed to the admins
who control servers and posted in the admin Discord channel. A player's conversation follows them across
cluster nodes and starts over after two hours without a message.

## Lore

An admin with `CanEditServers` sets a lore on the **Lore** page (Settings), through the REST API or with
`scripts/quasar-lore.py`. Without a lore nothing changes.

- **Where.** One lore per server or cluster, keyed by its unique name (`cluster:` in front is optional). A
  cluster's lore holds on all its nodes: chat from a node names the cluster. A server converted to a cluster
  keeps its lore, as the name stays. `/ask` on Discord names no server, so it gets no lore.
- **Free text.** Up to 200,000 characters, one page `lore/world`.
- **Git skill.** One URL: a GitHub or GitLab tree URL, a Forgejo or Gitea `src/branch|tag|commit` URL of the
  skill folder, or a clone URL with `#ref:path`. Only https. Every `.md` file under the folder becomes a page
  (`SKILL.md` gives `lore/SKILL`); at most 100 files and 1 MiB together.
- **Fetching.** With the `git` command (the Docker image has it): a shallow, partial fetch with a sparse
  checkout, so only the skill folder is downloaded. When the lore is saved, when an admin presses Refresh
  (`POST …/refresh`) and every 6 hours. The ref in the URL decides the revision: a branch follows its head, a
  tag or commit stays put. The fetched commit is shown.
- **Private repositories.** An access token, stored encrypted like the API keys and never shown again. Git gets
  it in an HTTP header through its environment, never on the command line, and it is cut from error texts.
  Credentials in the URL are refused; the audit log shows the token as `<redacted>`.
- **When a fetch fails** the error shows on the Lore page and in the API (`error`), and goes to Quasar's log.
  The assistant keeps the last good copy, or answers without lore if there never was one.
- **How the agents use it.** Not in the prompt: a `[host]` line tells the agent that the server has lore, and
  it lists, searches and reads the `lore/` pages with `skills_list`, `docs_search` and `skill_read` on demand,
  so large lore costs only what is read. Each request only sees the lore of the server it came from; the admin
  assistant sees the lore of the server its page shows.
- **What players can see.** Players can make the agent read out anything in a lore, so a lore holds nothing
  secret. Pages in a folder named `admin` of a Git skill are for the admin assistant only: the player agent
  can't list, search or read them. The lore operations are on the agents' deny list, so no agent can read the
  settings, the tokens or another server's lore.

API, the same operations as the page (`quasar.assistant/lore.list`, `lore.set`, `lore.refresh`, audited):

| Method | Route | Body |
|---|---|---|
| GET | `/api/v1/assistant/lore` | |
| PUT | `/api/v1/assistant/lore/{target}` | `{"kind": "text", "text": "..."}` or `{"kind": "git", "gitUrl": "...", "token": "..."}` (`token` left out keeps it, `""` removes it) |
| DELETE | `/api/v1/assistant/lore/{target}` | |
| POST | `/api/v1/assistant/lore/{target}/refresh` | |

```bash
export QUASAR_URL=http://127.0.0.1:8080
scripts/quasar-lore.py set-text octahedron lore.md
scripts/quasar-lore.py set-git cluster:octahedron https://github.com/owner/repo/tree/main/skills/lore --git-token-env LORE_TOKEN
scripts/quasar-lore.py list
```

The script takes `--url`/`QUASAR_URL` and a bearer token from `QUASAR_API_TOKEN` like `Quasar cluster`. Quasar's
service principals carry cluster scopes only, so in practice it runs on Quasar's trusted network, such as on the
Quasar host. It exits with 3 when a lore was saved but its skill couldn't be fetched.

## Grid restores

The model only names the grid. The admins' rules fill in the placement (the original position by default),
the distance from other players' grids (2000 m), the search radius for a free spot (10000 m) and whether to
empty inventories. Before anything is restored:

- Quota: 1 restore a day, 2 a week and 4 in 30 days per player by default (0: none, empty: no limit),
  counted from Quasar's audit log. An undone restore doesn't count.
- Cause: from Grid Backups' loss log. The restore runs at once only when the tool's mode is `apply` and the
  cause is one the admins allow (`destroyed` and `cleanup` by default). Otherwise it becomes a proposal with
  the reason, the cause and the quota used.
- When Grid Backups finds no free spot, the restore becomes a proposal so an admin can place it by hand. A
  refusal (the grid still exists, the player isn't an owner) goes back to the player.

The approval card shows the grid, the cause, the backup it will use and the placement.

## Monitor

Off until an admin turns it on. Every minute by default it checks, without a model, the standalone servers and
clusters whose goal is on:

- a server stopped, crashed or faulted, or unhealthy, for 5 minutes;
- a cluster whose Gateway can't be read, or that isn't healthy, for 5 minutes;
- a running server below sim speed 0.5 for 10 minutes, or without a world save for 60 minutes;
- every line printed by the admins' script, if one is set. The text before the first `: ` is the problem's key.
  A script that fails without printing anything, or runs past its timeout, is a problem too.

A new problem opens an incident and gets one model run in the monitor role. If the problem is still there after
the recheck time (60 minutes), it gets another run. When the checks no longer find it, the incident closes. Each
run looks for the cause and fixes it within the role's modes: reads by default, changes as proposals, and apply
only for operations the admins choose, such as `servers.restart`. Core runs the monitor's calls as its own agent,
`agent:monitor`. That agent acts for nobody, so only the monitor roles on Quasar's Security page limit it. The
result goes to the Monitor page, by web push to the admins who control servers, and to the admin Discord channel.

The token budget is set per UTC day, week or month and counts only uncached input and output tokens. Without
budget left, the incident is still recorded but no run starts, and the admins are told once per period. A run
stops before its next model turn once it has used up the rest. With prices set on the backends, the settings and
Monitor pages also show the budget and the spending in USD.

## Discord

`/assistant` answers only in the admin channel set in the settings, for Discord users mapped to a Quasar
account. There the admin assistant can read and propose, never apply: approvals stay in the web UI. Players
use `/ask`. New proposals and hand-overs can be posted to the admin channel with a link to the Approvals page.

## Pages

| Page | Who | What |
|---|---|---|
| Drawer on every page | `CanUseAssistant` | A launcher button and a chat panel that starts with the page and server as context. |
| `/assistant/chat` | `CanUseAssistant` | Your conversations and a full-size chat. |
| `/assistant/approvals` | `CanUseAssistant` | Players waiting for an admin, pending and decided proposals. Marking a hand-over handled needs `CanControlServers`. |
| `/assistant/monitor` | `CanUseAssistant` | The monitor's open and resolved incidents, each run's answer, tokens and cost, and the budget. |
| `/assistant/lore` | `CanEditServers` | The lore of each server and cluster: free text or a Git skill, its fetch state and errors, Refresh. |
| `/assistant/settings` | `CanManageSecurity` | Backends, their API keys and prices; per role the backend order, modes, limits, code execution, MCP servers and skills; the sandbox's URL and token; player support and restore rules; the monitor's checks, script and budget; notifications, Discord and usage. |

The chat shows the assistant's answers and the monitor's runs as Markdown: headings, lists, emphasis, code,
links and tables. HTML in an answer shows as text, and links open only http, https and mailto URLs and Quasar's
own pages.

The plugin's only endpoints are the lore API above. Its operations are the six MCP operations, which accept
nothing but the assistant's own agents, the Grid Backups player operations, and the three lore operations,
which agents can't call. Nothing an agent can call reaches its settings.

## Data folder

Everything lives in `plugin-data/quasar.assistant/` in the Quasar data folder:

| File | Contents |
|---|---|
| `settings.json` | Backends (without keys), roles, modes, limits. |
| `keys.json` | API keys, encrypted with Quasar's Data Protection keys. A saved key is never shown again. |
| `mcp-headers.json` | The MCP servers' extra headers and the sandbox token, encrypted the same way. |
| `conversations/<asker>/<id>.json` | One file per conversation. |
| `proposals.json` | The plugin's proposal records; Quasar's audit log holds the proposals themselves. |
| `handovers.json` | Players handed to the admins. |
| `player-outbox.json` | Decisions on players' proposals they haven't heard yet, kept as long as proposals. |
| `player-usage.json` | Per player: questions left and the last refill, action times of the last day, and when the privacy notice was sent. |
| `skills/admin/*.md`, `skills/player/*.md` | The admins' skills per role. The monitor reads the admin assistant's. |
| `lore.json` | Each server's and cluster's lore: the text or the Git URL, and the last good fetch (commit, time, pages) with the last error. |
| `lore-tokens.json` | Git access tokens, encrypted like the API keys. |
| `monitor.json` | The monitor's incidents and their runs; resolved ones go after the proposal expiry. |
| `metrics.jsonl` | One line per run: role, backends, fallovers, failures, turns, tokens (cached and processed), duration. No message text. |

## Building

The adapter and the UI project build against `Quasar.Plugin.Abstractions`. The QuasarHub installer passes
`QuasarPluginAbstractionsAssembly`, the DLL of the running Quasar. A local build uses a Quasar checkout next
to this repository (`../Quasar`), or the project named by `QuasarPluginAbstractionsProject`:

```bash
export QuasarPluginAbstractionsProject=/path/to/quasar/Quasar.Plugin.Abstractions/Quasar.Plugin.Abstractions.csproj
dotnet build QuasarAssistant.slnx
dotnet test tests/QuasarAssistant.Tests
```

The tests run the agent loop against an in-process stub server that speaks both streaming formats, and a stub
MCP server, with fake Quasar services. They never call a real model.

`tests/rig/` is the end-to-end test. `deploy.sh [user@host] [--keep]` builds Quasar and this plugin, copies
them and the Grid Backups working copy to a test host and starts `rig.py` there. It runs a dedicated server, a
headless game client, an isolated Quasar and a scripted stub model, and prints PASS or FAIL for seven steps:
`!ask`, an automatic restore, a proposal and its approval, an undo, the admin drawer, the cached prefix and a
grid name carrying an injection.

`chat_shots.py` takes screenshots of the chat with canned Markdown answers from a stub model, on the
Assistant page and in the drawer, dark and light, on a local Quasar with no game server. It is for before and
after shots of UI changes.

`smoke.py` asks a real model, by hand before a release. On a setup left running with `--keep`,
`smoke.py configure` turns the stub backend into a real one without a key. Someone pastes the key on the
settings page, then `smoke.py ask` puts 8 player and 5 admin questions to it. It checks that a destroyed grid
comes back, a deleted one and a kick only become proposals, and an injected grid name moves nothing, and writes
the answers with the tokens and cache hits of each run to `smoke.md`.

`sandbox/` holds the optional Quasar Sandbox, an MCP server that runs Python or C# with no network for the roles
with code execution on. It runs on Linux only; see [its README](sandbox/README.md). The plugin sends each MCP call's
conversation as `_meta` `quasar.assistant/conversation`, so the sandbox keeps a workspace per conversation.

`scripts/refresh-quasar-book.sh [commit]` refreshes the shipped Quasar book from a commit of the Quasar
repository and records it in `src/QuasarAssistant/QuasarBook/COMMIT`.

## Layout

```text
src/QuasarAssistant/          services and Razor UI
  Llm/                        message model, the two API formats, the client, failover
  Agent/                      tools and modes, the run loop, proposals, restores, hand-overs, the prompts
  Players/                    the ask and assistant commands, player limits
  GridBackups/                the player's Grid Backups operations
  Monitoring/                 the monitor's checks, script, incidents and token budget
  Mcp/                        the MCP client and the servers' tools
  Skills/                     the Quasar book and the admins' skills
  QuasarBook/                 the book's pages, pinned to a Quasar commit
  Settings/                   settings, encrypted API keys and headers, the page's forms
  Storage/                    conversations, proposals, hand-overs, metrics
  Components/, Pages/         drawer, chat, cards, settings editors, pages
  wwwroot/assistant.css
src/QuasarAssistant.Quasar/   the IQuasarPlugin adapter
scripts/                      refresh-quasar-book.sh
tests/QuasarAssistant.Tests/  xunit tests with the stub model and MCP servers
sandbox/                      the optional code-execution MCP server (Linux) and its smoke test
```

UI conventions are in [Native Quasar UI](docs/NativeUi.md).

## Publishing

1. Build and test against the Quasar version the plugin targets.
2. Copy `quasar-hub.xml` to `quasar-hub/Plugins/QuasarAssistant.xml` and set `Commit` to the exact commit.
3. Open a QuasarHub pull request.
