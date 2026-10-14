# AI implementation architecture

## Ownership and connections

`OpenUtau.Core.Automation.EditorAutomationService` owns domain reads and bounded edit planning. It operates on the existing `DocManager` through an awaited `IAutomationDispatcher`. It knows neither HTTP, Node nor Avalonia controls.

The desktop `AutomationLifecycleService` owns the core service, event-based selection context, Kestrel API, access grants, file capabilities, proposals and render jobs. Startup occurs after `MainWindow.InitProject`. The API remains alive while the sidebar is hidden. An accepted close awaits native process cleanup and API shutdown. Cancelling the ordinary unsaved-changes dialog keeps the editor and automation running.

`openutau-mcp --stdio` is a separately owned process using the official TypeScript MCP SDK. It discovers reachable editor descriptors and connects to the local API. It never launches an AI provider or edits `.ustx` independently. External MCP processes belong to their clients and are not terminated by OpenUtau.

`openutau-agent-host --stdio` uses a separate, versioned private JSON-RPC protocol. It owns selected ACP provider processes and an optional question helper. Each native session injects the ordinary OpenUtau MCP server into ACP New and Load. Its own grant reference is mandatory and cannot fall back to general external rights.

## Changes and derived processing

Runtime identities use object references rather than indices, names or `UPart.Id`. Moving and undoing retain identity; duplication produces new identity; project load resets the registry and project session. Edit revisions are independent of rendering revisions. The phoneme tracker exposes requested/completed generations and invalidates index-based overrides until the current response is ready.

Dry-run builds detached state without creating mutating legacy commands or loading audio. A validated bounded batch is applied as an exception-safe `AutomationBatchCommand`. It restores musical values into the original target objects and rebuilds SortedSets after position edits. Its captured before/after states support one editor undo group, rollback and stable identities. This deliberately pays the cost of a musical snapshot to make heterogeneous batches atomic; batches are capped rather than kept open for an entire song.

Schema validation runs both in MCP and at the editor boundary. Grants are checked again inside the actual UI callback, including delayed import and core calls. Concurrent requests cannot close an active user undo group. Native preview proposals retain their original payload, hash, revision, grant and expiry. Only the app-side approval method can commit them.

MIDI and audio imports are parsed off-thread and committed through the same command path. The registered file fingerprint is checked again before commit. Saves serialize the guarded current state, write a temporary file and atomically replace the approved target. `Ustx.LoadFromText` ensures opening uses the exact text checked by the automation layer; Wave paths embedded in a project must independently remain inside authorized folders.

Rendering captures a separate project, copied expression descriptors, time axis and immutable phrase inputs on the UI thread. Workers build isolated render phrases, reuse the existing `RenderEngine` and write WAVs to temporary destinations. Linked cancellation stops render work. The job reports success only after the file has a valid format, duration and payload and the output target is committed. Artifacts name their original revision; later edits make them stale. A project change cancels previous jobs. User exports are not swept with temporary previews.

## Local security boundary

Kestrel binds only `127.0.0.1` on an OS-assigned port. Browser origins, non-loopback hosts, invalid credentials, unsupported versions and requests above 2 MiB are rejected. Health reveals readiness and process identity, never the song. No eval, shell or arbitrary write-file endpoint exists.

The per-user runtime folder is `%LOCALAPPDATA%/OpenUtau/automation` on Windows. POSIX uses `$XDG_RUNTIME_DIR`, then `$XDG_DATA_HOME`, then `~/.local/share`, with `OpenUtau/automation` appended. Descriptors and native grant references are written atomically with current-user Windows ACLs or POSIX 0700/0600 permissions. Descriptors include a cryptographically random bootstrap credential, process ID, exact start identity, instance ID, API version and loopback URL.

The descriptor credential can create an external connection grant according to the editor's configured mode. The native assistant receives a separate session credential bound to its project and process generation. `clientId` is a correlation field, not proof of authorization. Operation and job ownership are bound to the authenticated grant. File capabilities resolve links and parents and check purpose, expiry, project session, content fingerprint and overwrite intent.

These credentials protect the local protocol, not against every program running under the same OS user. Provider-native file and shell privileges are a separate boundary and remain visible as unknown unless the provider proves stronger restrictions.

## UI and persistence

The native sidebar occupies the outer MainGrid's third column; the editor carousel and piano roll retain their original layout. Hidden/responsive columns have zero effective width while preserving desired width. Local assistant controls override editor-wide focus-disabled styles. Keyboard and pointer guards keep chat text editing outside project shortcuts.

The assistant MVVM layer batches stream events on the Avalonia dispatcher, virtualizes conversation history, separates normalized display history from provider resume IDs and displays typed tool/job results. English and German strings use existing theme and language resources. History and settings live in per-user application data, not `.ustx` or a portable package folder; no provider secrets are stored in them.

## Delivery

`contracts/automation/v1` is generated from the contract generator and checked for freshness. The TS workspace has an exact npm lockfile. The package script pins Node by RID and SHA-256, bundles separate MCP/host/helper entrypoints and schemas, inventories licenses and excludes provider fallback binaries. The desktop checks the package manifest and artifact hashes before using it. `Microsoft.AspNetCore.App` is included by the self-contained .NET publisher. Unsupported AI runtime RIDs can still run the desktop editor.
