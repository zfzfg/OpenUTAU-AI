# Provenance and licensing

Read-only inventory on **14 October 2026** confirmed these exact local reference states:

| Reference | Git commit | Package version | Working state preserved |
|---|---|---|---|
| All your Companions | `b0455fde8a206ee4e47ec0e34aa3ca4fe7637b86` | `0.3.3` | Clean. |
| CLI-to-API-endpoint | `0e1dfb210e0afa997203d24e0f21960965a6a95f` | `0.1.0` | Existing untracked `docs/next-PLAN.md` retained. |

Their paths were read only. No install, build, reset, commit or source change was performed in either reference checkout. Companion's `AGENTS.md` imports `CLAUDE.md`; those instructions were read for understanding, while release/reset rules were not applied to this read-only task.

The new TypeScript MCP adapter, native process host, helper question service and contracts are an independent implementation. Architectural observations informed provider separation, new/load MCP injection, model/config normalization, exact-once request cards and verified process termination. Reference source files included Companion `acp-backend.ts`, `acp.ts`, provider-specific backends/capabilities, `acp-dispatch.ts`, `ask-user-protocol.ts`, and Gateway `src/acp/process.ts`/package inventory. No reference file or source fragment was imported or copied into the OpenUtau runtime. OpenUtau does not depend on a personal reference checkout at runtime.

Companion carries **FSL-1.1-MIT** notices. Gateway is a private **UNLICENSED** package. Those sources have not been relicensed as OpenUtau code. The independent implementation uses published [MCP](https://modelcontextprotocol.io/specification/2026-07-28/server/tools) and [ACP v1](https://agentclientprotocol.com/protocol/v1/session-setup) contracts plus observed extension shapes; no Companion webview, checkpoint/worktree rollback or gateway continuation endpoint was reused.

The official MCP TypeScript server/client packages **2.3.1**, Ajv **8.20.0**, esbuild **0.28.2**, TypeScript **7.0.2** and all transitives are pinned by the npm lockfile. Production packages include MCP server/core, Zod, Ajv and its parsing/schema dependencies. Their own licenses are copied into `AI/licenses`; bundle legal comments remain attached. The package writes `THIRD_PARTY_NOTICES.md` with versions/licenses. Node **24.19.0** is redistributed with its full `NODE_LICENSE.txt` and component notices. SHA-256 archive pins for all supported RIDs were taken from [official Node SHASUMS256](https://nodejs.org/dist/v24.19.0/SHASUMS256.txt).

Optional provider adapters are not part of the base dependency tree or bundled assets. Their chosen setup versions, engines, transitive CLI/SDK dependencies and explicit CLI-selection variables are recorded in `scripts/node-runtime-lock.json` and [provider documentation](PROVIDERS.md). Provider authentication, subscriptions and licenses remain separate.
