# Editor automation API v1

The OpenUtau process owns the live project. `EditorAutomationService` in `OpenUtau.Core/Automation` implements the musical operations. The Desktop lifecycle service adds authenticated loopback access, file handles, proposals, project loading/saving, playback and render jobs. Both the native assistant and external MCP clients use these services.

The canonical public argument contracts are [`tools.json`](../../contracts/automation/v1/tools.json) and [`edit.schema.json`](../../contracts/automation/v1/edit.schema.json). The HTTP host validates the same tool schemas with `AutomationContractValidator` before invoking the editor. Unknown operations and invalid musical values are rejected. The API has no generic evaluation, shell, script or arbitrary-project-field operation.

## Calling and threading

Requests to `POST /automation/v1/invoke` contain `apiVersion: "1"`, `requestId`, `clientId`, `operation` and `arguments`. They authenticate with the connection's bearer credential; `clientId` is checked against that credential. Tool names may use the `openutau_` prefix. Credentials are issued through the protected discovery connection and never belong in project files or copied MCP configuration.

Core's integration interface is:

```csharp
new EditorAutomationService(DocManager document, IAutomationDispatcher dispatcher);
Task<object> InvokeAsync(string operation, JsonElement arguments,
    CancellationToken cancellationToken = default);
```

`IAutomationDispatcher.InvokeAsync<T>(Func<T>, CancellationToken)` must await the complete UI action, including exceptions and the undo commit. Desktop uses Avalonia's dispatcher. Core does not post individual commands and return before they run. It does not use synchronous waits on the UI thread. `ProjectSessionId`, `EditRevision`, `Identities`, `Journal` and `Changed` are exposed at the Desktop integration boundary. Direct model-facing helpers (`FindPart`, `ArePhonemesReady`, `GetPhonemeState`) must be called on the dispatcher.

## Sessions, references and revisions

Every `LoadProjectNotification` creates a new random `projectSessionId`, including reloading the same project object. Edit revisions start at zero for a newly loaded session. Runtime IDs (`track_…`, `part_…`, `note_…`) use object identity, rather than list indices, note positions or `UPart.Id`. Moving a note and undoing/redoing its batch preserves its ID. Cloning creates a different ID. Reloading invalidates the old ID context.

`editRevision` tracks musical commands, undo/redo and relevant transient mixer notifications. Existing `DocManager.Revision` retains its pipeline purpose. Phonemizer completion does not increment the edit revision. Revisions are monotonic within a project session; they are not promised to equal the number of undo groups because normal editor gestures can publish multiple commands.

Every write supplies:

```json
{
  "projectSessionId": "project_…",
  "expectedEditRevision": 12,
  "operationId": "a-new-unique-id",
  "label": "Edit the verse",
  "operations": []
}
```

`runId` optionally groups a song-building workflow for the client. It does not open a long-running undo group. A stale session fails with `PROJECT_CHANGED`; a stale revision fails with `REVISION_CONFLICT`. The response includes the current project context where useful. Clients must reread and replan after a conflict.

## Reads and pagination

`get_project` returns name, comment, file path, key, resolution, tempo/meter maps, tracks and part summaries. Notes use separate paginated reads. Positions on notes are relative to the part; positions on parts and tempo events are relative to the project. Ticks use the project's resolution. Meter changes use zero-based `barPosition`.

`get_notes` accepts `partId`, optional half-open `fromTicks`/`toTicks`, `limit` and `cursor`. It selects notes intersecting the range. `get_phonemes` accepts `partId` and optional `noteIds`. Singer search uses `query`. Singer, phonemizer, note, phoneme and diagnostic pages use this common response shape:

```json
{
  "projectSessionId": "project_…",
  "editRevision": 12,
  "snapshotId": "…",
  "items": [],
  "nextCursor": "…",
  "total": 1234,
  "expiresAt": "…"
}
```

Every page is a copy of one immutable snapshot. Subsequent pages retain the first page's revision even if the live project changes. Keep the same filters on subsequent calls; only `cursor` and `limit` may change. A changed filter is invalid. A project change, expiration or eviction returns `SNAPSHOT_EXPIRED`. Restart from the first page. Snapshots expire after 120 seconds, retain at most 32 snapshots and collectively retain at most 32 MiB of serialized content. Page sizes are 1–500, default 200. These limits are reported in capabilities.

Note DTOs contain `noteId`, `positionTicks`, `durationTicks`, `tone`, `tuningCents`, visible `lyric`, separate `phoneticHint`, pitch, vibrato, persistent overrides and expressions. Returned arrays and objects contain no live model references. Reads currently include all these note details; the optional public `include` argument is retained in the snapshot filter, but does not reduce the returned fields.

Phoneme DTOs contain the owning note ID, generated index, raw/current/mapped alias, part-relative tick timing, preutter/overlap in milliseconds and diagnostic state. Diagnostics are paginated `items` with codes such as `MISSING_SINGER`, `INVALID_NOTE`, `MISSING_ALIAS` and `PHONEMIZER_FAILED`.

## Musical edits

A batch contains 1–128 operations and at most 5,000 note/expression items. Track count is capped at 256. Schema-specific array limits also apply. Lyric/name text fields are limited to 8,192 characters. Input validation rejects negative positions, durations below 10 ticks, non-finite numbers, invalid MIDI pitches, invalid maps and integer tick overflow. The part is expanded to cover the maximum note end, regardless of the incoming note order.

| Operation | Musical fields |
|---|---|
| `set_project_info` | `name`, `comment`, `key` (0–11) |
| `set_tempo_map` | `tempos: [{positionTicks, bpm}]`; starts at tick zero, unique positions; 10–1,000 BPM |
| `set_time_signature_map` | `timeSignatures: [{barPosition, numerator, denominator}]`; starts at bar zero; numerator 1–32, denominator 1/2/4/8/16/32 |
| `add_track`, `update_track` | `name`, `color`, `singerId`, `phonemizerId`, `volumeDb` (-60–24), `pan` (-1–1), `mute`, `solo`; update uses `trackId` |
| `remove_track` | `trackId`; explicitly removes that track and its parts |
| `add_part` | `trackId`, `positionTicks`, `durationTicks`, optional `name`, `kind: "voice"` |
| `update_part`, `remove_part` | `partId`; update optionally sets name, track, position and vocal-part duration |
| `add_notes` | `partId`, `notes: [{positionTicks, durationTicks, tone, lyric?, tuningCents?, clientRef?}]` |
| `update_notes` | `partId`, `items: [{noteId, positionTicks?, durationTicks?, tone?, lyric?, tuningCents?}]` |
| `remove_notes` | `partId`, `noteIds` |
| `set_lyrics` | `partId`, explicit `items: [{noteId, lyric}]` |
| `set_phonetic_hints` | `partId`, `items: [{noteId, lyric?, phoneticHint, alphabet?}]`; alphabet is the selected phonemizer's own alphabet |
| `set_phoneme_overrides` | `partId`, `phonemeGeneration`, `items` with note ID/index and alias and/or timing overrides |
| `clear_phoneme_overrides` | `partId`, `phonemeGeneration`, `noteIds` |
| `set_pitch` | `partId`, note items containing `snapFirst?` and 2–256 `points: [{xMs, yCents, shape?}]` |
| `set_vibrato` | `partId`, note items with length, fade and shift percentages, `periodMs`, `depthCents`, drift/volume linkage percentages |
| `set_expressions` | `partId`, items with renderer-supported `abbr`; note value/index or part-relative curve `points` |

Add operations may supply unique `clientRef` strings (up to 128 characters). Later operations in the same batch can use a client reference in the usual ID field, optionally prefixed with `@`. The commit returns `clientRefs`, mapping every supplied reference to its new runtime ID.

Pitch point times are milliseconds relative to the note. Pitch point values are cents relative to the note tone; Core converts them into the existing internal pitch units. Vibrato uses the existing `UVibrato` bounds and coupled fade limits. Commit results return actual note DTOs, including controlled fade clamps. Expressions are checked against the track's renderer and descriptor limits. Standard curves replace that abbreviation's curve. Masked curves use the existing five-tick grid and return their actual snapped runs in `appliedPartCurves`.

The visible lyric and hint are separate API values; a supported hint is persisted in OpenUtau's `lyric [hint]` representation. `alphabet` defaults to `phonemizer`. Universal IPA/Arpabet translation is not claimed. Core's deterministic `LyricMapping.Split` helper uses whitespace tokens and keeps explicit `+` melisma/extender tokens; it does not infer correct pronunciation or language-specific syllables. The public schema uses explicit note/lyric mappings.

## Preview, commit, undo and failure

`preview_edit` prepares the complete batch on detached musical state. It creates no real command and does not validate the live document, run a phonemizer, construct a plugin instance, change note duration/overrides, touch undo history or increment the edit revision. The result contains operation summaries, track/part/note counts, `trackChanges`, `partChanges` and `noteChanges` with detached before/after musical values, part and track labels. Added and removed entities use null before/after respectively. Part changes include expression curves and masked runs.

`apply_edit_batch` validates all operations before mutation. An active user undo group returns `EDITOR_BUSY` and remains open. Commit creates one `AutomationBatchCommand`, applies it inside one deferred-validation undo group and returns only after validation and history commit. The command removes notes from their sorted set before changing their keys, and preserves original model object references through undo/redo. Undo applies only the transition's musical fields and entities, preserving unrelated later transient notes, pitch and mixer changes. Desktop view models explicitly refresh in response to this batch command.

The command snapshots the musical state and restores it if execution throws. Aborting the group restores its state, closes the group and preserves previously committed undo/redo history. Deferred validation runs before history insertion. A pre-render subscriber failure after commit cannot turn the already committed edit into a failed request. Derived validation failures after restoration are logged while the aborted transaction is closed.

Journal states include `queued`, `validating`, `committed`, `failed`, `cancelled_before_commit` and `reverted`. The Desktop proposal layer adds `awaiting_approval` and `rejected`. The process journal retains at most 4,096 entries and does not replay them after app restart. Reusing the same operation ID/payload returns its recorded result. A different payload or project under that ID fails with `IDEMPOTENCY_CONFLICT`. Query `get_operation` after a timeout instead of assuming nothing happened. The Desktop additionally checks authenticated operation ownership.

`revert_operation` supplies a new `operationId` and the original `targetOperationId`. It only undoes a target whose session, edit revision and actual latest undo-group identity still match. Any intervening user/automation edit fails with `REVISION_CONFLICT`. Repeating the revert returns its recorded result without undoing a second step.

## Phoneme generations

Per-part state contains `requestedGeneration`, `completedGeneration`, `phonemeGeneration`, `sourceEditRevision` and `status` (`ready`, `pending` or `stale`). Generation numbers are opaque monotonic runtime values, not phoneme indices or native timestamp values.

Relevant lyric, timing, singer, phonemizer, tempo and meter writes invalidate old indices immediately. Actual native phonemizer requests are also observed, including requests caused by undo/full validation. Only accepted responses for the native current request complete a generation. Native request timestamps are strictly increasing per part. Pitch-only commands that skip the phonemizer and mixer notifications do not wait for responses they never request. Rollback never reuses an earlier generation.

`wait_for_phonemes` binds `partId`, `projectSessionId` and `expectedEditRevision`; `timeoutMs` is 0–30,000, default 5,000. It periodically checks on the UI dispatcher without blocking the UI. It returns the current ready/pending/stale state when ready or when its bounded wait ends. A changed project/revision fails explicitly.

Override tools require the current completed `phonemeGeneration` and a generated index actually owned by the specified note. A preceding relevant change in the same batch is rejected; commit lyrics/timing first, await phonemes and then submit overrides. Classic aliases are checked against the selected singer's mapped Oto. Timing deltas use the same preutter, overlap, attack and release bounds as the existing editor commands, without constructing mutating override commands during preview.

## MIDI and audio import

The Desktop resolves session-bound approved file handles; Core never accepts a raw path from the public API. MIDI parsing happens outside the UI thread using the existing nativeless MIDI parser, on an independent project. The prepared musical batch adds tracks/parts/notes and optionally replaces tempo/meter maps (`importTempo: true`). A target `trackId` can map imported parts to an existing track. `positionTicks` places the imported arrangement. Imports are limited to 5,000 notes and 32 MIDI tracks; larger arrangements must be split. SMPTE time division is rejected.

Audio metadata is read off-thread. Positive audio up to two hours is imported as a wave part, optionally on an existing track. A preview creates no live part or audio-load worker. Apply loads the owned wave part and adds it in an undoable batch. MIDI/audio imports preserve earlier undo history and never issue a load-project notification. Both support `preview: true`, operation idempotency and revision checks. Before commit the Desktop revalidates the file handle/fingerprint through Core's optional `validateInputBeforeCommit` callback.

## Evidence and boundaries

The automated Core tests cover inert preview, conflict/busy handling, injected execution rollback, undo/redo/object IDs, same-batch references, operation retries, snapshot pagination, reload, phoneme generation changes, awaited worker-to-UI commit, guarded reverts, import preservation and 1,000/5,000/10,000-note reads. Song fixtures exercise the actual lifecycle/save/reload/render/export paths, including two vocal parts, a tempo change, a melisma, an imported wave, persistent overrides and a nonempty stereo 44.1-kHz WAV.

The singer, phonemizer responses and PCM renderer in those song tests are deterministic fixtures. They validate transaction and render-result correctness, not the audio quality of an installed voice. Real singer/audio listening and logged-in provider checks are recorded separately in the overall test report. Core musical state snapshots currently copy the complete document for each edit plan/undo step; very large editing workloads therefore need measured memory and UI-commit budgets before claiming realtime guarantees.
