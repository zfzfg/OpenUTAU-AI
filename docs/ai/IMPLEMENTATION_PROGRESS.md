# Implementation progress

Date: 14 October 2026. Source: `OPENUTAU_KI_UMSETZUNGSPLAN.md`. This checklist separates implemented behavior from external acceptance that cannot be established by fake tests.

| Phase | Delivered work | Evidence / remaining acceptance |
|---|---|---|
| P0 | Read-only source/reference inventory, contracts, SDK pins, provenance | Baseline .NET build, 628 existing core tests (2 existing live skips), 10 UI tests passed |
| P1 | Object identities, edit and phoneme revisions, copied reads, immutable pagination, selection context | Core identity/session/pagination/load tests and 1,000 / 5,000 / 10,000 note cases |
| P2 | All planned edit operations, inert preview with musical before/after, atomic batch command, rollback, undo/redo, journal, client references | Injected execute failure, validation failure, busy user group, conflict, idempotency and sorted-duration tests |
| P3 | Authenticated Kestrel, protected discovery, connection grants, official MCP adapter and complete catalog | Actual standard SDK subprocess against the real editor API; no sidebar or provider involved |
| P4 | Lifecycle, file handles, undoable MIDI/audio import, tuning/expressions, phoneme coordination, isolated render/export jobs | Two-part vocal song, tempo, melisma and Wave fixture; overrides, undo/redo, save/reload and valid nonzero WAV; cancel and renderer failures |
| P5 | Private versioned IPC, ACP provider abstraction, Codex launch/injection, model/effort catalogs, questions/permissions/cancel | Fake ACP tests; real Codex tool conversation still needs its missing local ACP adapter and authorized account |
| P6 | Native sidebar, focus/layout/theme/strings, composer, context, tool/review/audio cards | Headless UI tests and screenshot review; real voicebank listening remains separate |
| P7 | Claude and Grok backends, resume/history, request withdrawal, owned shutdown and reconnect | Fake tests for required providers; Grok real ACP initialize passed; Claude CLI missing; real musical turns not claimed |
| P8 | Bundled pinned Node, JS/schemas/licenses/manifest, self-contained app, CI and configuration copy, complete docs | Local Windows portable package verification; other RID execution delegated to CI and not claimed as locally tested |
| P9 | Integrated regression, real SDK song roundtrip, render fixture, load/failure/permission/UI checks | Exact final results recorded in TEST_REPORT; remaining live-provider/audio/platform boundaries explicitly open |

The implementation includes both the native interface and the independent MCP server. Remaining external acceptance is not represented as completed by a prototype, fake provider or a successful handshake alone.
