# OpenUtau MCP tools, editor API v1

The standalone `AI/node[.exe] AI/mcp-server.js --stdio` entry uses the official TypeScript MCP SDK `@modelcontextprotocol/server` **2.3.1**, pinned in `tools/openutau-ai/package-lock.json`. It negotiates the SDK's supported MCP versions; the current release supports the **2026-07-28** specification, with older-client compatibility handled by the SDK. MCP and editor API version numbers are separate. See [official SDK](https://github.com/modelcontextprotocol/typescript-sdk) and [tool specification](https://modelcontextprotocol.io/specification/2026-07-28/server/tools).

The tool list is available without a running editor, sidebar, agent host, installed AI CLI, network or provider login. Stdout contains only MCP frames; failures are structured tool results and diagnostics use stderr. Every result includes compatible JSON text content and `structuredContent`. A domain failure sets `isError: true`, `ok: false`, and `error.code/message`. Protocol framing/handshake errors remain MCP errors.

`contracts/automation/v1/tools.json` is the machine-readable catalog, including strict input schemas, output envelopes and annotations. `edit.schema.json` enumerates the 19 musical edit operations. The generator reads the saved wire/edit schemas to derive TS types and the C# `generated/AutomationWire.g.cs` request/result/error/descriptor/grant records; Core's musical state DTOs supply the editor semantics. Regenerate with `npm run contracts` and verify with `npm run check:contracts`. Annotations describe behavior; the editor validates actual rights and arguments.

| Tool | Main arguments and effect |
|---|---|
| `openutau_list_instances` | Reachable processes, instance IDs and readiness; credentials are omitted. |
| `openutau_get_project` | Optional `instanceId`; session/revision, resolution, tempo, meter, tracks and parts. |
| `openutau_get_selection` | Actual current selection, including detached piano roll. |
| `openutau_get_capabilities` | Supported operations, renderer/expression abilities, access and size limits. |
| `openutau_list_singers` | Optional `query`, `limit`, `cursor`. |
| `openutau_list_phonemizers` | Optional `limit`, `cursor`; unknown capabilities are explicit. |
| `openutau_get_notes` | `partId`, optional `fromTicks/toTicks`, `include`, `limit/cursor`. |
| `openutau_get_phonemes` | `partId`, optional `noteIds`, `limit/cursor`; exact generation and stale status. |
| `openutau_get_diagnostics` | Optional `partId`, `limit/cursor`; actionable singer/alias/processing problems. |
| `openutau_preview_edit` | An edit batch; returns a musical diff without mutation. |
| `openutau_apply_edit_batch` | An edit batch; one guarded undo transaction or a native approval proposal. |
| `openutau_register_input_file` | Absolute `path`, `purpose: project/midi/audio`; session-bound input handle. |
| `openutau_resolve_output_target` | Absolute `path`, `purpose: project/audio`, explicit `overwrite`; output handle. |
| `openutau_new_project` | Write guards, optional `name/discardChanges`; separate native lifecycle. |
| `openutau_open_project` | Write guards, `fileHandle`, optional `discardChanges`; new session identity. |
| `openutau_import_midi` | Write guards, `fileHandle`, optional `trackId/positionTicks/importTempo/preview`. |
| `openutau_import_audio` | Write guards, `fileHandle`, optional `trackId/positionTicks/preview`. |
| `openutau_wait_for_phonemes` | `projectSessionId`, `expectedEditRevision`, `partId`, `timeoutMs: 0–30000`. |
| `openutau_start_render` | Write guards, `kind: preview/stems/mixdown`, optional part/tracks/tick range; job ID. |
| `openutau_get_job` | `jobId`; progress, confirmed status, errors and verified artifacts. |
| `openutau_cancel_job` | `jobId`; idempotent request, distinct from confirmed cancellation. |
| `openutau_get_operation` | `operationId`; recover the real outcome after an uncertain connection. |
| `openutau_revert_operation` | Write guards, `targetOperationId`; only the compatible latest owned transaction. |
| `openutau_play_range` | `projectSessionId`, optional tick range or `artifactId`; shared audio coordinator. |
| `openutau_stop_playback` | Stop project and preview playback. |
| `openutau_save_project` | Write guards, `outputHandle`, explicit `overwrite`. |
| `openutau_export_audio` | Write guards, `kind`, `outputHandle`, optional tracks/part/range, explicit `overwrite`; job ID. |

Every tool accepts an optional `instanceId`. Multiple editors require an explicit selection; no newest-process heuristic is used. Write guards are `projectSessionId`, `expectedEditRevision`, and an immutable unique `operationId`. Identical retries return the journaled outcome; changing the payload under the same ID fails. An uncertain HTTP completion is `OPERATION_STATE_UNKNOWN`, so read the journal before resubmitting.

```json
{
  "projectSessionId": "READ_FROM_GET_PROJECT",
  "expectedEditRevision": 7,
  "operationId": "UNIQUE_ID",
  "label": "Phrase lyrics",
  "operations": [
    {"type":"set_lyrics","partId":"READ_PART_ID","items":[
      {"noteId":"READ_NOTE_ID","lyric":"hello"},
      {"noteId":"READ_NEXT_NOTE_ID","lyric":"+"}
    ]}
  ]
}
```

Track/part/note identities are runtime IDs within a project session. Newly created entities can use unique `clientRef` strings, referenced in subsequent operations' normal ID fields; the result maps them to runtime IDs. Reload requires new reads. Note positions are ticks relative to the part; part positions are ticks relative to the project. Resolution comes from `get_project`. Pitch points use `xMs/yCents`; vibrato uses explicit percent/ms/cents fields. Overrides require a completed `phonemeGeneration`, and timing deltas explicitly use ticks or milliseconds.

Pagination returns `snapshotId`, `items`, `nextCursor`, revision, expiry and project session. Follow-up pages must repeat the same filter and belong to the immutable snapshot. An expired snapshot produces `SNAPSHOT_EXPIRED`; writes from it remain revision guarded. Limits are obtained from capabilities.

Only user-released roots support file handles. Paths outside them produce `FILE_ACCESS_DENIED`; grant additional roots in OpenUtau settings or a native picker. Existing output files require explicit overwrite. Handles expire and are invalidated by a project switch; link/reparse resolution and fingerprints are checked by the editor.

Discovery reads private per-user descriptors under `%LOCALAPPDATA%/OpenUtau/automation`, `$XDG_RUNTIME_DIR/OpenUtau/automation`, `$XDG_DATA_HOME/OpenUtau/automation`, or `~/.local/share/OpenUtau/automation`. It checks POSIX ownership/modes or the actual Windows DACL, refuses symlinks, validates loopback URLs, and compares health instance/start identity. Duplicate descriptor IDs are excluded. Explicitly selected connections cache the grant; authentication remains checked by the editor on each call. Untargeted calls repeat discovery to detect ambiguity. Reading `get_project` explicitly binds a replacement project after `PROJECT_CHANGED`; no write is automatically retried. Native sessions use `OPENUTAU_ACCESS_GRANT_REF`, check its private contents on initial read/change and its fingerprint/existence/expiry on calls, and fail closed when that reference is invalid. They never fall back to external editing rights. This protection does not isolate other processes with the same user rights.

Stable error codes are in `errors.json`. Expected conflicts include `PROJECT_CHANGED`, `REVISION_CONFLICT`, `PHONEME_GENERATION_CONFLICT`, `EDITOR_BUSY`, `IDEMPOTENCY_CONFLICT` and `SNAPSHOT_EXPIRED`. Renders report typed `MISSING_SINGER`, `MISSING_ALIAS`, `PHONEMIZER_FAILED` or `RENDER_FAILED`; a text response is not proof of completed audio.
