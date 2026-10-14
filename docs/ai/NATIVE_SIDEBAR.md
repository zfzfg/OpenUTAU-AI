# Native assistant

Open **Tools → AI assistant** to show the native panel beside the tracks and piano roll. The menu stays reachable on the welcome page. Drag the divider to resize the panel; the preferred width is remembered separately from its effective width. At window widths below 805 logical pixels the panel collapses, and reappears when enough editor space is available. Hiding it leaves the conversation and external MCP service running.

Choose a provider and an editor mode:

- **Read only** permits project inspection and conversation.
- **Preview** turns musical writes into editor proposals. Review the returned musical values, then apply or reject them.
- **Apply allowed edits** permits normal writes inside the explicitly chosen musical scope.

The scope selector distinguishes the current/selected vocal part from the entire project. Part scope requires an actual selected or open part; it does not silently become project scope. Selected note IDs are attached as context, while the server authorization covers the displayed part scope. Switching provider, mode or scope disconnects the old session and revokes its musical grant. Connect again to issue a new grant.

The CLI account is the one already signed in through the selected CLI. The provider and adapter must be installed explicitly. Settings can select an executable or adapter JavaScript entry, a different Node runtime, and an agent-host file. No command shell or visible terminal is launched. Available models and reasoning efforts come from the verified provider session; a changed selection is displayed only after confirmation. CLI file and shell permissions remain provider-specific and are distinct from OpenUtau's editor grants.

The composer preserves normal text shortcuts. Enter sends, Shift+Enter adds a line, and active IME preedit is committed before sending. All assistant actions can take keyboard focus. Text selection, Delete, Space and undo inside the composer do not route to the editor's parts or playback shortcuts.

Tool cards use returned tool results. Render jobs are polled to a terminal result; audio cards appear only for actual editor artifacts. Starting a preview uses the editor's shared audio output, stopping project playback while retaining the cursor. Project playback replaces the preview on that same output. A preview can be stopped explicitly. Project changes stop previews and invalidate old grants and pending proposals.

Review cards display detached before/after note values in pages of twelve changes. **Show in editor** uses the existing piano-roll and part notification path, including lazy piano-roll initialization. **Undo this edit** sends a new revision-guarded revert operation targeting the original operation ID; it does not restore a song file checkpoint.

Settings also expose local automation enablement, the external MCP access mode, approved import/export folders, and local history retention. **Copy MCP configuration** produces token-free JSON using the actual selected installed runtime paths. It never changes global client configuration. Disabling local automation revokes grants and closes the local endpoint; an explicit native connection can enable it again.

Display history and the draft are stored outside `.ustx` files in the user's assistant app-data directory. Writes are atomic, retain one previous version, and bound message count, individual text size and total UTF-8 file size to 8 MiB. The directory is restricted to the current user. Clearing history removes those two local files. Confirmed model/effort metadata and tool operation, job and artifact references are retained for diagnostics; restored messages are noninteractive and never replay pending cards or writes. Native provider resume IDs are kept separately from display messages; **Resume provider session** requires a verified saved ID for the same provider.

The private transport is bounded NDJSON JSON-RPC v1. Every owned host generation negotiates its version and receives a fresh protected grant reference. Permission and question cards are correlated to the session, turn and generation, accept only the offered option IDs, and are withdrawn on cancellation, completion, process exit or disconnect. Stop waits for the real turn to end. A replacement host starts only after disposal confirms the old owned process has exited.

The normal save/close prompt remains authoritative: cancelling it keeps the provider, history and discovery alive. Accepted shutdown first disposes the assistant and local automation, then closes the window, including a detached piano roll.

## Development and validation

Production resolves `AI/node.exe` (Windows) or `AI/node`, and `AI/agent-host.js` below the application directory. Explicit development overrides are `OPENUTAU_AI_DIR` and `OPENUTAU_AI_NODE_PATH`. Package manifests and artifact hashes are checked before starting a bundled host. UI test data is isolated with `OPENUTAU_ASSISTANT_DATA_DIR` and `OPENUTAU_AUTOMATION_DIR`.

`AssistantSidebarTest` covers native layout/collapse, pointer and keyboard focus, Shift+Enter/IME, 2,000-message virtualization, true result cards, permission withdrawal, live batch/undo refresh, atomic history, live English/German and Light/Dark/custom themes, and an account-free end-to-end C# host → fake ACP → real MCP → editor roundtrip. That roundtrip also checks permission responses, Stop, and cancelling the editor save prompt. Build the TypeScript workspace before running the native host integration test. Fake provider tests do not establish live account authentication, real provider inference or audio quality.

Screenshots are emitted by the existing headless harness under the UI test binary's `ui-screenshots` directory, including `AssistantEnglishLight`, `AssistantGermanDarkReview`, `AssistantCustomThemeReview` and the real native host fixture. Actual operating-system IME integration and display scaling still need the platform's manual desktop acceptance, in addition to these headless checks.
