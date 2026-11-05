# Quasar Assistant

An admin assistant for [Quasar](https://github.com/CometWorks/quasar), the control panel of Space Engineers
dedicated servers and clusters. Admins chat with it in a drawer on every Quasar page or on its own page. It
answers from Quasar's operations: it reads server status, logs, settings and players, and it changes things
or proposes changes as the admins allow.

It is a Quasar UI plugin (id `quasar.assistant`). It runs inside Quasar and talks to the model providers the
admins set up. No provider or model ships with it.

## What it does

- **Models.** Each backend is one model at one provider: an OpenAI-compatible API (OpenRouter, OpenAI,
  llama.cpp, vLLM) or the Anthropic Messages API. The admin assistant tries its backends in a set order. A
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
- **Data.** Tool results have passwords, tokens and keys replaced by `<redacted>` and are cut to 16 KiB.
  Conversations are kept per person and deleted 30 days after their last message.
- **Caching.** Every request of a role starts with the same bytes: the system prompt, the admins' notes and
  the tools in name order. The time, the asker and the page go into the user turn as `[host]` lines, so the
  providers' prompt caches hit.

## Pages

| Page | Who | What |
|---|---|---|
| Drawer on every page | `CanUseAssistant` | A launcher button and a chat panel that starts with the page and server as context. |
| `/assistant` | `CanUseAssistant` | Your conversations and a full-size chat. |
| `/assistant/approvals` | `CanUseAssistant` | Pending and decided proposals. |
| `/assistant/settings` | `CanManageSecurity` | Backends and API keys, the backend order, modes, limits, notifications and usage. |

The plugin registers no operations and no endpoints, so nothing an agent or a script can call reaches its
settings.

## Data folder

Everything lives in `plugin-data/quasar.assistant/` in the Quasar data folder:

| File | Contents |
|---|---|
| `settings.json` | Backends (without keys), roles, modes, limits. |
| `keys.json` | API keys, encrypted with Quasar's Data Protection keys. A saved key is never shown again. |
| `conversations/<asker>/<id>.json` | One file per conversation. |
| `proposals.json` | The plugin's proposal records; Quasar's audit log holds the proposals themselves. |
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

The tests run the agent loop against an in-process stub server that speaks both streaming formats, with fake
Quasar services. They never call a real model.

## Layout

```text
src/QuasarAssistant/          services and Razor UI
  Llm/                        message model, the two API formats, the client, failover
  Agent/                      tools and modes, the run loop, proposals, the system prompt
  Settings/                   settings and encrypted API keys
  Storage/                    conversations, proposals, metrics
  Components/, Pages/         drawer, chat, proposal card, pages
  wwwroot/assistant.css
src/QuasarAssistant.Quasar/   the IQuasarPlugin adapter
tests/QuasarAssistant.Tests/  xunit tests with the stub model server
```

UI conventions are in [Native Quasar UI](docs/NativeUi.md).

## Publishing

1. Build and test against the Quasar version the plugin targets.
2. Copy `quasar-hub.xml` to `quasar-hub/Plugins/QuasarAssistant.xml` and set `Commit` to the exact commit.
3. Open a QuasarHub pull request.
