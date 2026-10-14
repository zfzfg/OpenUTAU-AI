# OpenUtau AI: native assistant and regular MCP

OpenUtau now exposes one editor automation service to an independent stdio MCP server and to a native Avalonia assistant. The editor remains the owner of the live song. Music edits are visible in the editor and use its undo history.

## Start

Build a portable package using the commands below, then run `OpenUtau.exe` from the published directory. Show **AI assistant / KI-Assistent** from the Tools menu. The sidebar can be hidden without stopping external MCP connections. Its settings let you select an installed provider CLI and ACP adapter, configure file access, choose external connection rights, and copy a token-free MCP configuration. The [native assistant guide](NATIVE_SIDEBAR.md) describes its controls and lifecycle.

The integrated provider uses your existing CLI authentication. Codex needs an installed `codex` and `codex-acp`; Claude needs `claude` and `claude-agent-acp`; Grok uses its native ACP transport. Programs are not downloaded at app startup. See [provider setup and verified versions](PROVIDERS.md). Local file and shell permissions offered by a provider remain that provider's permissions.

The native modes are `read_only`, `preview` and `apply`. Preview mode creates an immutable review card. **Apply / Übernehmen** commits that exact proposal after checking its grant and revision again. Rejection, cancellation and reconnect withdraw pending work. A committed edit is undone through guarded undo rather than by restoring a song file.

For a new song, select project scope and describe language, voice, tempo, length and melody. Installed voices and their phonemizers can be listed through tools. Existing lyrics can be mapped to selected notes; explicit `+` extenders represent melismas. Pronunciation depends on the chosen voicebank and phonemizer. An instrumental can be imported as a Wave part.

## External clients

No sidebar, provider CLI, provider account or agent host is needed for external MCP. The client starts the bundled adapter itself. OpenUtau must be running; ambiguity between multiple editors requires an explicit `instanceId`.

Use the **Copy MCP configuration** action to obtain actual installed paths. For a client with JSON MCP configuration:

```json
{
  "mcpServers": {
    "openutau": {
      "command": "C:\\OpenUtau\\AI\\node.exe",
      "args": ["C:\\OpenUtau\\AI\\mcp-server.js", "--stdio"]
    }
  }
}
```

Replace these illustrative paths with the copied absolute paths. For Codex, the equivalent is:

```powershell
codex mcp add openutau -- 'C:\OpenUtau\AI\node.exe' 'C:\OpenUtau\AI\mcp-server.js' --stdio
codex mcp list
```

The adapter's tool list is available even when OpenUtau is closed. It returns `APP_NOT_RUNNING` for editor access. A project replacement creates a new `projectSessionId`; read and explicitly bind the new project before sending another write. Remote cloud clients need a local executor that can launch this stdio process.

## Files and audio

The current project directory is the default allowed folder. Untitled projects use a private application workspace. Release additional input/export folders in assistant settings. Register project, MIDI and audio inputs with `openutau_register_input_file`; resolve saves/exports with `openutau_resolve_output_target`. Existing outputs require explicit `overwrite: true`. Handles expire after 15 minutes and do not survive a project replacement.

Render calls return a job ID immediately. Poll `openutau_get_job`; only `succeeded` means verified audio is available. Artifacts contain an absolute local path, duration, source project/revision and range. Cancelled or failed jobs never claim completed WAV output. Native audio cards use the central playback manager, so previews and project playback stop one another. Temporary previews are bounded; user exports are retained.

## Build and test

Development requires .NET SDK **10.0.402** (or its latest patch) and Node **24.19.0**. The production package bundles Node, so end users do not need a Node installation.

```powershell
cd tools/openutau-ai
npm ci
npm run check:contracts
npm run typecheck
npm test
npm run package -- --rid win-x64 --out ../../OpenUtau/AI
cd ../..
dotnet build OpenUtau.slnx
dotnet run --project OpenUtau.Test/OpenUtau.Test.csproj
dotnet run --project OpenUtau.UiTest/OpenUtau.UiTest.csproj
dotnet publish OpenUtau/OpenUtau.csproj -c Release -r win-x64 --self-contained true -o artifacts/openutau-ai-win-x64
```

The same package script supports `win-arm64`, `osx-x64`, `osx-arm64`, `linux-x64` and `linux-arm64`. It verifies the pinned Node archive hash and inventories bundled licenses. CI tests Windows, macOS and Linux; actual execution results for this implementation are in [TEST_REPORT.md](TEST_REPORT.md).

An explicit development override can set `OPENUTAU_AI_DIR` to the built `dist` directory and `OPENUTAU_AI_NODE_PATH` to an existing absolute Node path. Provider paths are configurable in the native settings. No personal reference checkout is a runtime dependency.

## Verification and limits

Deterministic tests cover the real MCP SDK, editor API, rollback, permissions, fake ACP providers, native focus/layout and a complete vocal song with a deterministic singer/renderer. Real Grok ACP initialization was verified. Real paid musical conversations, Codex with its missing local ACP adapter, Claude with its missing local CLI, and listening against a real installed voicebank remain separate acceptance work. See [TEST_REPORT.md](TEST_REPORT.md) for the exact boundary; fixture audio does not prove voicebank audio quality.

Protocol references: [editor API](EDITOR_API_V1.md), [MCP tools](MCP_TOOLS_V1.md), [agent host](AGENT_HOST_V1.md), [architecture](ARCHITECTURE.md), [provenance and licenses](PROVENANCE.md).
