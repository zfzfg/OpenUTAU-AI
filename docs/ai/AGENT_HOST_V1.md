# Native assistant host, contract v1

`AI/node[.exe] AI/agent-host.js --stdio` is a private JSON-RPC 2.0, newline-delimited UTF-8 process. It is separate from the standard music MCP entry. Requests have a string/integer ID; notifications have no ID. The generated `contracts/automation/v1/agent-host.schema.json` describes requests and events. Frame size is limited to 2 MiB; broken JSON, unterminated EOF and oversized frames fail visibly. UTF-8 chunk boundaries are preserved.

The native C# bridge calls `initialize` once with `{apiVersion:"1", appVersion?, processGeneration:<positive integer>}`. The reply contains `apiVersion`, `hostVersion`, `processGeneration`, capabilities and actual Node/platform/architecture. The app owns that generation and mints editor grants for it. All session events contain `sessionId`, `processGeneration`, `eventId`, and the active `turnId` when relevant.

| UI request | Parameters / result |
|---|---|
| `providers.list` | `{}` → provider IDs/names and unprobed capability states. |
| `providers.probe` | `{providerId,cliPath?,adapterPath?}` → install/version status; no inference/authentication request. |
| `session.create` | `{providerId,cwd,instanceId,projectSessionId,accessGrantRef,clientId?,cliPath?,adapterPath?,modelId?,effortId?,mode?,mcpServers?}`. |
| `session.load` | Same parameters, plus the actual `nativeSessionId`; requires advertised ACP load support. |
| `session.list` | `{sessionId?,cursor?}`; native listing only when advertised, otherwise active local sessions are identified as such. |
| `session.prompt` | `{sessionId,text,context?}`; one active turn. The request completes on turn completion while events stream. |
| `session.cancel` | Notification `{sessionId,turnId?}`; a request form is also supported for tests/confirmation. |
| `session.setModel` | `{sessionId,modelId,effortId?}`; only values in the actual catalog. |
| `session.setEffort` | `{sessionId,effortId}`; only actual per-model options and a supported control method. |
| `session.setMode` | `{sessionId,mode,accessGrantRef?}`; app must have changed the authoritative grant; a different reference requires reconnect/load. |
| `session.steer` | Returns `UNSUPPORTED_CAPABILITY` until a provider implementation is actually verified. |
| `permission.respond` | `{sessionId,turnId,processGeneration,requestId,optionId}`. |
| `question.respond` | Same correlation fields, and `optionId?` / text `answer?`. |
| `session.dispose`, `shutdown` | Idempotent owned cleanup; credentials are revoked by the app. |

Create/load returns `{sessionId,nativeSessionId,processGeneration,state,models,currentModelId,efforts,currentEffortId?,capabilities}`. Models use `{modelId,name,description?,efforts?}`; efforts use `{id,name}`. They come from real ACP model/config responses. Codex composite model/effort IDs are grouped without inventing options. Claude config options are read by category/ID. A model switch is shown only after a successful provider response; unsupported choices fail rather than silently selecting a fallback.

State events are `disconnected`, `starting`, `ready`, `working`, `waiting_user`, `cancelling`, or `failed`. Other events: `providerState`, `capabilities`, `models`, `messageDelta {text}`, `messageCompleted {text}`, `toolStarted/toolUpdated/toolCompleted {toolCallId,title,status,content?,result?}`, `planUpdated {entries}`, `usageUpdated {usage}`, `error {code,message,recoverable}`, `turnCompleted {stopReason}`, `processExited {code,signal}`. A completed tool card uses actual ACP result content, including structured MCP results; `ok:false` is marked failed.

Permission and question events contain a unique host `requestId`, correlation fields, title, offered options and question text where appropriate. Responses preserve permission option IDs. Stale/duplicate responses fail with `REQUEST_STALE`; invalid choices fail with `INVALID_ARGUMENT`. Cancel, EOF, dispose and turn completion withdraw cards exactly once. Missing native filesystem/terminal RPC capabilities are explicitly advertised as false; this does not disable the CLI's own tools.

The ACP transport explicitly negotiates **version 1**, matching [ACP v1 session setup](https://agentclientprotocol.com/protocol/v1/session-setup) and [cancellation](https://agentclientprotocol.com/protocol/v1/cancellation). Session-new **and** load receive precisely one `openutau` stdio MCP entry, with absolute packaged Node/script paths and private grant references in child environment entries. Repeated injection is stable; reserved-name collisions and duplicate foreign names fail. No global provider configuration is edited.

Codex/Claude also receive `openutau-assistant`, an optional standard MCP helper exposing `ask_user` and `read_answer`. A host-owned loopback question service authenticates it with an ephemeral private reference file. `ask_user` returns a pending question ID immediately; `read_answer` waits at most 30 seconds and is cancellable. This helper is absent from regular external music-tool use. Grok's `x.ai/ask_user_question` extension is normalized into individual correlated question cards and returns its native accepted/cancelled outcome.

Cancel sends ACP `session/cancel` **without an RPC ID**, withdraws pending cards and waits three seconds for actual turn completion. It then terminates only the owned PID/tree or POSIX process group and confirms exit. A failed termination blocks replacement. Processes are started with argument arrays, no shell and hidden Windows windows. Standard npm `.cmd` wrappers are resolved to concrete JS entries rather than executed as arbitrary shell text. Child diagnostics are consumed without recording potentially sensitive provider transcripts.

The app persists display history/drafts separately from native resume IDs. Loading passes new project context and a new grant; old writes are never replayed by the host. The same edit API and server-side grant policy is used externally and natively. Native CLI file/shell isolation remains **unknown** unless separately verified.
