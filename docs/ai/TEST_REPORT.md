# Verification report

Date: **14 October 2026**. Local execution platform: **Windows x64**. SDK: **.NET 10.0.402**, runtime **10.0.13**, Node **24.19.0**. No provider inference requests, global AI configuration changes, provider installations, tags or releases were performed.

## Final gates

| Check | Result |
|---|---|
| `dotnet build OpenUtau.slnx` | Passed, zero errors |
| `dotnet run --project OpenUtau.Test/OpenUtau.Test.csproj` | **670 total, 668 passed, 0 failed, 2 existing live skips**, 57.099 seconds |
| `dotnet run --project OpenUtau.UiTest/OpenUtau.UiTest.csproj` | **19 passed, 0 failed, 0 skipped**, 18.394 seconds |
| New editor/API/render tests within the main suite | **42 passed**: 33 core/song cases and 9 transport/file/access cases |
| `npm ci` | Passed; exact lockfile; dependency audit reported 0 vulnerabilities |
| `npm run check:contracts`, `typecheck`, `build` | Passed, including generated C#/TS wire contracts |
| `npm test` | **27 passed, 0 failed, 0 skipped** |
| Node runtime packaging | All six configured RID packages built and their pinned archive hashes verified |
| Self-contained `dotnet publish ... -r win-x64` | Passed; includes .NET, Windows Desktop and ASP.NET Core 10.0.13 runtimes |
| Published Windows AI entrypoint smoke | **27 MCP tools, 2 question-helper tools and private host initialize** passed using the bundled Node runtime |
| Windows AI package with spaces/Unicode in its path | Passed MCP/helper/host smoke |
| `git diff --check` | Passed |

The baseline before implementation was 628 core tests (the same two live skips) and 10 UI tests. Existing compiler/analyzer warnings remain; the fresh baseline had 1,850 warnings. No test skips or test exclusions were introduced for the AI implementation. Full-suite execution exposed a shared UI-thread marker in independently owned test documents; the fixture dispatchers now declare and restore their owner thread, retaining the affinity assertions. Final full-suite results above include that correction.

The two existing skips require external fixtures: `MergeAdjacentPhrasesLiveTest.MergeToggleFlipsGroupingInsidePadding` needs a DiffSinger voicebank, and `DawRealPluginTest.RealPluginCompletesTheHandshakeAndPullsAudio` needs a running DAW bridge host.

## What the end-to-end tests prove

The actual official MCP SDK launches the actual stdio adapter, discovers an actual Kestrel service, reads its project, previews and commits a song with two parts, mixed note durations and a `+` melisma, verifies operation idempotence and immutable pagination, obtains an ordinary output handle, saves `.ustx`, and verifies its reloaded content. This test does not use a sidebar, agent host or provider CLI.

The song/render fixture uses the real editor commands, phoneme/override state, undo/redo, save/load, isolated render snapshot, renderer pipeline, job manager and WAV writer. It includes two vocal parts, a tempo change, melisma, an instrumental Wave part and persistent overrides. The output is valid stereo 44.1-kHz WAV with duration and nonzero PCM. Renderer failure, missing singer/alias, cancellation and project replacement produce truthful terminal results without a claimed finished artifact. The deterministic singer/renderer fixtures prove the workflow rather than real voicebank audio quality.

The HTTP tests also cover real credentials, browser-origin rejection, protocol/schema validation, client/operation ownership, exactly-once native proposals, revocation, stale project writes, canonical file access, fingerprints, explicit overwrite, atomic saving and Wave paths embedded in untrusted project content. A render request is tested after its HTTP JSON document has been disposed, proving that the job owns its input copy.

The native integration launches the real C# pipe client and Node host, uses a fake ACP provider and the real MCP SDK/editor API, then checks permission correlation, confirmed cancellation and cancelling the normal unsaved-changes close dialog. Additional headless tests cover composer shortcuts/IME preedit, pointer focus, responsive zero-width collapse, live batch/undo refresh, 2,000-message virtualization, bounded atomic history with metadata, and language/theme changes during streaming.

## Visual and load checks

English/Light, German/Dark and Custom-theme screenshots were rendered with the existing Avalonia headless Skia harness and visually inspected. Native message/review cards, controls and composer are readable. The German mode label was shortened to fit. A 25-note musical review pages as 12/12/1 instead of presenting a raw JSON diff. Screenshots are generated under `OpenUtau.UiTest/bin/Debug/net10.0-windows/ui-screenshots/`; representative files are `AssistantEnglishLight.png`, `AssistantGermanDarkReview.png` and `AssistantCustomThemeReview.png`.

Synthetic read tests exercise **1,000, 5,000 and 10,000 notes** with immutable pagination and bounded batches. These are regression/load checks, not a measured real-time UI guarantee. Production streaming uses a 40-ms UI batching window; actual provider latency, long-running real-bank phonemization and manual playback responsiveness remain outside the fixture timings.

## Live provider and platform boundary

| Provider | Observed local evidence | Remaining acceptance |
|---|---|---|
| Codex | CLI **0.160.0** found; backend/fake tests passed | ACP adapter absent locally; authenticated real musical tool turn not run |
| Claude | Backend/fake tests passed | CLI absent locally; authenticated real musical tool turn not run |
| Grok | CLI **1.0.52** (`0920c8169e27`); real ACP v1 `initialize` succeeded | No inference/model request or real musical turn was sent; account/tool-round acceptance remains open |
| Gemini / Antigravity | Explicit `probe` extension point | Disabled for editor control pending a demonstrated tool round |
| Muse | Explicit unsupported extension point | Disabled for editor control pending a demonstrated tool round |

Other RIDs (`win-arm64`, both macOS architectures, both Linux architectures) were **packaged and hash-checked**, not executed locally. Their builds/tests are configured in CI. Human listening with a real installed singer, real operating-system IME behavior, and actual 100–200% desktop DPI remain manual acceptance items. A successful handshake or fake response is not recorded as live musical acceptance.

## Deliverables

The complete portable Windows application is built at `artifacts/openutau-ai-win-x64/`, with `AI/` containing its own Node, MCP, host, helper, contracts, checksum manifest and license inventory. The packaged entrypoints were executed from that published tree. The archive and checksum are produced alongside it. Source, tests, CI, contracts, provenance and all user/protocol documentation are committed; binaries and local verification output are intentionally not added to Git.

Remaining acceptance is explicit: provide the missing installed provider components and an authorized provider account for real musical turns; test the other operating systems and perform real voicebank/OS input/display checks. The implementation does not claim that those external checks have passed.
