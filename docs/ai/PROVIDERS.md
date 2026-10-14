# Provider inventory and acceptance

Verification date: **14 October 2026**. Normal automated tests use local fake ACP processes and the real MCP SDK, with no account, model inference or provider installation. Tests exercise Codex, Claude and Grok backend launch, v1 handshake/new/load, MCP injection, catalogs, Unicode streaming, tool results, question/permission correlation, cancellation and broken streams.

| Provider | Implementation | Local observed installation | Live evidence |
|---|---|---|---|
| Codex | Installed Codex plus external `codex-acp`, explicit `CODEX_PATH` | `codex-cli 0.160.0`; adapter not on PATH | Version-only probe; no live ACP session/tool round. |
| Claude | Installed Claude plus external `claude-agent-acp`, explicit `CLAUDE_CODE_EXECUTABLE` | CLI missing | Fake acceptance; no live account/tool round. |
| Grok | Native `grok agent stdio` | `grok 1.0.52 (0920c8169e27)` | Real inference-free `initialize` returned ACP 1 and load/list/resume/close capabilities. No real model prompt/tool round. |
| Gemini / Antigravity | Reserved backend/capability position, `probe` | Not accepted | Editor-control sessions remain disabled until a verified tool round. |
| Muse | Reserved capability position, `no` host MCP | Not accepted | Not offered as an OpenUtau control provider. |

The fetched package registry on that date reported `@agentclientprotocol/codex-acp 2.2.2` and `@agentclientprotocol/claude-agent-acp 0.90.0`; these optional setup versions are pinned in `scripts/node-runtime-lock.json`. They are **not** bundled or installed automatically. Codex's package declares `@openai/codex ^0.160.1`; this local Codex version is below that declared dependency floor, so it is not presented as a verified adapter pair. Claude's adapter declares Node >=22 and Claude Agent SDK 0.3.296. Node **24.19.0** is bundled for the regular MCP and native host entries.

Configure an intentionally installed CLI path and adapter JS/native entry in the assistant settings. Provider authentication remains in the CLI; disconnecting OpenUtau does not sign out. Adapter startup receives the resolved CLI environment variable to prevent accidental selection of a bundled fallback executable. Provider-specific assets/dynamic imports remain the installed adapter's responsibility; OpenUtau does not reduce an optional adapter to an incomplete esbuild bundle.

Authoritative source repositories: [Codex ACP](https://github.com/agentclientprotocol/codex-acp), [Claude ACP](https://github.com/agentclientprotocol/claude-agent-acp), [ACP v1](https://agentclientprotocol.com/protocol/v1/session-setup). Codex registration uses the current [official OpenAI MCP configuration documentation](https://developers.openai.com/codex/mcp). The installation probe distinguishes missing CLI, missing adapter, found program and connection-time protocol/auth/quota errors. “Found” does not claim authenticated model access.

Model and effort IDs are never hard-coded. They are read from the created/loaded session and validated for that provider/model. Musical `read_only/preview/apply` is enforced by OpenUtau grants. Provider-native filesystem, terminal, browser and shell tools have separate capabilities and permissions; MCP injection alone does not isolate them.

For explicit inference-free inventory, after building the workspace:

```powershell
node scripts/probe-provider.mjs --provider codex
node scripts/probe-provider.mjs --provider claude
node scripts/probe-provider.mjs --provider grok --initialize-only
```

Live musical acceptance still requires an available authenticated account and a deliberately authorized short prompt through the native sidebar. Confirm actual project read/edit/render tool results, resume and cancel independently for each provider. No such paid/live inference was performed by the normal tests or version probes.
