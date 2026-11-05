# Quasar Assistant

An assistant for [Quasar](https://github.com/CometWorks/quasar), the control panel of Space Engineers
dedicated servers and clusters. Admins chat with it in a drawer on every Quasar page, on its own page and in
the admin Discord channel. It answers from Quasar's operations: it reads server status, logs, settings and
players, and it changes things or proposes changes as the admins allow. Players ask it with `!ask` in game and
`/ask` on Discord about the server and their own grids, and it can restore a lost grid.

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
- **MCP servers.** Each role can have remote MCP servers (streamable HTTP; no local servers). Their tools
  appear as `mcp_<server>_<tool>`. Every call goes through a Quasar operation, so it is checked and audited:
  `quasar.assistant/mcp.read` and `mcp.call` for the admin assistant (server control by default), and
  `mcp.player.read` and `mcp.player.call` for the player agent (`CanActAsPlayer`, which only the player agent
  meets). Each handler accepts only its own role's agent. Tools the admins mark read-only follow the read mode;
  any other MCP tool is always a proposal and can't be undone.
- **Data.** Tool results have passwords, tokens and keys replaced by `<redacted>` and are cut to 16 KiB.
  Conversations are kept per person and deleted 30 days after their last message.
- **Caching.** Every request of a role starts with the same bytes: the system prompt, the admins' notes and
  the tools in name order. The time, the asker and the page go into the user turn as `[host]` lines, so the
  providers' prompt caches hit.

## Player support

Off until an admin turns it on. For each `!ask` or `/ask`, in this order, and without a model run before the
last step:

1. Off: the player is told the assistant isn't enabled.
2. Limits: 10 requests an hour, 30 a day per player by default, counted by Steam ID (or Discord ID without a
   link). Over a limit the player gets a fixed reply.
3. Privacy notice: on the first request and once a month, the admins' text (by default it names the
   providers of the player role's backends).
4. The player agent runs in its own pool (2 at once, 60 s queue wait by default) with the `my.*` tools the
   modes allow, `ask_admin` and the skill tools. A Discord user without a link gets no `my.*` tools.
5. The answer is whispered in game or posted on Discord.

Quasar builds the player agent from the game session or the Discord link, and its `my.*` operations only
reach that player's own things. Changes the player agent makes or proposes count against 3 actions a day.
When every backend fails, the player hears that the assistant is unavailable and the admins get a hand-over.
When an admin approves or rejects one of the player's proposals, or it expires, the player is told at the
start of their next request, before the model runs. Quasar can't message a player outside a request, so
nothing reaches them sooner. `ask_admin` hands the conversation to the admins: it is listed on the Approvals page, pushed to the admins
who control servers and posted in the admin Discord channel. A player's conversation follows them across
cluster nodes and starts over after two hours without a message.

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

## Discord

`/assistant` answers only in the admin channel set in the settings, for Discord users mapped to a Quasar
account. There the admin assistant can read and propose, never apply: approvals stay in the web UI. Players
use `/ask`. New proposals and hand-overs can be posted to the admin channel with a link to the Approvals page.

## Pages

| Page | Who | What |
|---|---|---|
| Drawer on every page | `CanUseAssistant` | A launcher button and a chat panel that starts with the page and server as context. |
| `/assistant` | `CanUseAssistant` | Your conversations and a full-size chat. |
| `/assistant/approvals` | `CanUseAssistant` | Players waiting for an admin, pending and decided proposals. Marking a hand-over handled needs `CanControlServers`. |
| `/assistant/settings` | `CanManageSecurity` | Backends and API keys; per role the backend order, modes, limits, MCP servers and skills; player support and restore rules; notifications, Discord and usage. |

The plugin registers no endpoints and only the four MCP operations, which accept nothing but the assistant's
own agents. Nothing an agent or a script can call reaches its settings.

## Data folder

Everything lives in `plugin-data/quasar.assistant/` in the Quasar data folder:

| File | Contents |
|---|---|
| `settings.json` | Backends (without keys), roles, modes, limits. |
| `keys.json` | API keys, encrypted with Quasar's Data Protection keys. A saved key is never shown again. |
| `mcp-headers.json` | The MCP servers' extra headers, encrypted the same way. |
| `conversations/<asker>/<id>.json` | One file per conversation. |
| `proposals.json` | The plugin's proposal records; Quasar's audit log holds the proposals themselves. |
| `handovers.json` | Players handed to the admins. |
| `player-outbox.json` | Decisions on players' proposals they haven't heard yet, kept as long as proposals. |
| `player-usage.json` | Per player: request and action times of the last day, and when the privacy notice was sent. |
| `skills/admin/*.md`, `skills/player/*.md` | The admins' skills per role. |
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

`scripts/refresh-quasar-book.sh [commit]` refreshes the shipped Quasar book from a commit of the Quasar
repository and records it in `src/QuasarAssistant/QuasarBook/COMMIT`.

## Layout

```text
src/QuasarAssistant/          services and Razor UI
  Llm/                        message model, the two API formats, the client, failover
  Agent/                      tools and modes, the run loop, proposals, restores, hand-overs, the prompts
  Players/                    player requests, limits, Discord admin requests
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
```

UI conventions are in [Native Quasar UI](docs/NativeUi.md).

## Publishing

1. Build and test against the Quasar version the plugin targets.
2. Copy `quasar-hub.xml` to `quasar-hub/Plugins/QuasarAssistant.xml` and set `Commit` to the exact commit.
3. Open a QuasarHub pull request.
