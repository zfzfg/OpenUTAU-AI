# OpenUtau AI workspace

```powershell
npm ci
npm run check:contracts
npm run typecheck
npm run build
npm test
npm run package -- --rid win-x64
```

`dist/mcp-server.js` is the standard MCP entry; `dist/agent-host.js` is the native sidebar's private JSON-RPC entry; `dist/assistant-tools.js` is its optional authenticated question helper. All bundled entries use ESM and run on Node24. They need no source checkout or installed npm dependencies after packaging.

`npm run package -- --rid <RID> --out <absolute-AI-directory>` fetches only the pinned official Node archive on the build machine, verifies the checked-in checksum, and copies the runtime, JS, JSON contracts, license notices and manifest. Cached archives are also verified. Runtime startup performs no network installation. Supported RIDs: `win-x64`, `win-arm64`, `osx-x64`, `osx-arm64`, `linux-x64`, `linux-arm64`.

The optional provider adapters are intentionally outside the standard MCP dependency tree; configure installed executable/JS entry paths through OpenUtau settings. Normal tests never use real provider binaries. `scripts/probe-provider.mjs` is an explicit inference-free inventory command. `test/editor-roundtrip.mjs` is invoked by the .NET integration suite against actual Kestrel/editor services.

Precise public and private contracts: [MCP tools](../../docs/ai/MCP_TOOLS_V1.md), [agent host](../../docs/ai/AGENT_HOST_V1.md), [providers](../../docs/ai/PROVIDERS.md), [provenance](../../docs/ai/PROVENANCE.md).
