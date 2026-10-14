using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using NAudio.Wave;
using OpenUtau.Api;
using OpenUtau.Automation;
using OpenUtau.Core;
using OpenUtau.Core.Automation;
using OpenUtau.Core.Render;
using OpenUtau.Core.Ustx;
using ReactiveUI.Builder;
using Xunit;

namespace OpenUtau.Test.Core.Automation {
    /// <summary>Real editor/lifecycle/render/export paths with deterministic audio, without provider accounts.</summary>
    [Collection("Editor automation")]
    public sealed class AutomationSongTest : IAsyncDisposable {
        sealed class SerializedDispatcher(DocManager doc) : IAutomationDispatcher {
            readonly object gate = new();
            public Task<T> InvokeAsync<T>(Func<T> action, CancellationToken cancellationToken = default) {
                lock (gate) {
                    cancellationToken.ThrowIfCancellationRequested(); doc.SetMainThreadForTest(Thread.CurrentThread);
                    return Task.FromResult(action());
                }
            }
        }
        sealed class FixtureSinger : USinger {
            public FixtureSinger() { found = true; loaded = true; }
            public override string Id => "automation-deterministic-singer";
            public override string Name => "Automation deterministic fixture";
            public override USingerType SingerType => USingerType.Classic;
            public override IList<USubbank> Subbanks => Array.Empty<USubbank>();
            public override bool TryGetOto(string alias, out UOto oto) { oto = alias == "missing" ? null! : UOto.OfDummy(alias); return oto != null; }
            public override bool TryGetMappedOto(string alias, int tone, string color, out UOto oto) => TryGetOto(alias, out oto);
        }
        sealed class FixtureRenderer : IRenderer {
            public bool Fail, Block;
            public readonly TaskCompletionSource Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
            public USingerType SingerType => USingerType.Classic;
            public bool SupportsExpression(UExpressionDescriptor descriptor) => true;
            public override string ToString() => Renderers.WORLDLINE_R;
            public RenderResult Layout(RenderPhrase phrase) => new() {
                leadingMs = phrase.leadingMs, positionMs = phrase.positionMs, estimatedLengthMs = phrase.leadingMs + phrase.durationMs,
            };
            public async Task<RenderResult> Render(RenderPhrase phrase, Progress progress, int trackNo,
                CancellationTokenSource cancellation, bool isPreRender = false, RenderPhraseEvents? renderEvents = null) {
                Started.TrySetResult();
                if (Block) await Task.Delay(Timeout.Infinite, cancellation.Token);
                if (Fail) throw new IOException("Fixture renderer exception");
                var result = Layout(phrase);
                result.samples = Enumerable.Range(0, Math.Max(1, (int)Math.Ceiling(result.estimatedLengthMs * 44.1)))
                    .Select(i => (float)(.12 * Math.Sin(i * 2 * Math.PI * 440 / 44100))).ToArray();
                return result;
            }
            public RenderPitchResult LoadRenderedPitch(RenderPhrase phrase) => null!;
            public UExpressionDescriptor[] GetSuggestedExpressions(USinger singer, URenderSettings settings) => [];
        }
        readonly DocManager doc = (DocManager)Activator.CreateInstance(typeof(DocManager), nonPublic: true)!;
        readonly Thread previousUiThread = OpenUtau.Core.Util.ThreadGuard.UiThreadForTest;
        readonly SerializedDispatcher dispatcher;
        readonly AutomationLifecycleService lifecycle;
        readonly FixtureSinger singer = new();
        readonly FixtureRenderer renderer = new();
        readonly string directory = Path.Combine(Path.GetTempPath(), "OpenUtau automation " + Guid.NewGuid().ToString("N"));
        readonly Action<UCommand>? previousSink;
        static readonly JsonSerializerOptions Options = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
        static readonly Lazy<bool> Reactive = new(() => { RxAppBuilder.CreateReactiveUIBuilder().WithCoreServices().BuildApp(); return true; });
        static JsonElement J(object value) => JsonSerializer.SerializeToElement(value, Options);
        public AutomationSongTest() {
            _ = Reactive.Value;
            Directory.CreateDirectory(directory);
            previousSink = DocManager.Inst.CommandSink;
            // Legacy renderer notifications target the app singleton; the fixture document is independently owned.
            DocManager.Inst.CommandSink = _ => { };
            SingerManager.Inst.Singers.Add(singer.Id, singer);
            var project = OpenUtau.Core.Format.Ustx.Create(); project.FilePath = Path.Combine(directory, "song.ustx");
            doc.TakeProjectForTest(project); dispatcher = new(doc); doc.SetMainThreadForTest(Thread.CurrentThread);
            project.tracks[0].Singer = singer;
            AttachRenderer(); lifecycle = new(doc, dispatcher); lifecycle.AllowDirectory(directory);
        }
        void AttachRenderer() {
            foreach (var track in doc.Project.tracks.Where(t => t.Singer != null)) {
                track.RendererSettings.renderer = renderer.ToString(); track.RendererSettings.Renderer = renderer;
            }
        }
        object Write(object[]? operations = null, object? extra = null) {
            var args = new Dictionary<string, object?> { ["projectSessionId"] = lifecycle.Editor.ProjectSessionId,
                ["expectedEditRevision"] = lifecycle.Editor.EditRevision, ["operationId"] = Guid.NewGuid().ToString("N") };
            if (operations != null) args["operations"] = operations;
            if (extra != null) foreach (var field in J(extra).EnumerateObject()) args[field.Name] = field.Value.Clone();
            return args;
        }
        async Task<JsonElement> Call(string op, object args) => J(await lifecycle.InvokeAsync(op, J(args)));
        async Task BuildSong() {
            string trackId = lifecycle.Editor.Id(doc.Project.tracks[0]);
            await Call("apply_edit_batch", Write(new object[] {
                new { type = "set_tempo_map", tempos = new[] { new { positionTicks = 0, bpm = 120 }, new { positionTicks = 1920, bpm = 90 } } },
                new { type = "add_part", trackId, clientRef = "verse", name = "Verse", positionTicks = 0, durationTicks = 1920 },
                new { type = "add_part", trackId, clientRef = "chorus", name = "Chorus", positionTicks = 1920, durationTicks = 1920 },
                new { type = "add_notes", partId = "verse", notes = new[] {
                    new { clientRef = "first", positionTicks = 0, durationTicks = 240, tone = 60, lyric = "la" },
                    new { clientRef = "melisma", positionTicks = 240, durationTicks = 480, tone = 64, lyric = "+" },
                    new { clientRef = "last", positionTicks = 720, durationTicks = 240, tone = 67, lyric = "u" } } },
                new { type = "add_notes", partId = "chorus", notes = new[] {
                    new { positionTicks = 0, durationTicks = 720, tone = 72, lyric = "a" },
                    new { positionTicks = 720, durationTicks = 240, tone = 69, lyric = "o" } } },
            }));
            await CompletePhonemes();
        }
        async Task CompletePhonemes() => await dispatcher.InvokeAsync(() => {
            AttachRenderer();
            foreach (var part in doc.Project.parts.OfType<UVoicePart>()) {
                var notes = part.notes.ToArray();
                var heads = notes.Select((n, i) => (n, i)).Where(p => p.n.Extends == null).ToArray();
                long timestamp = (long)typeof(UVoicePart).GetField("notesTimestamp", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(part)!;
                part.SetPhonemizerResponse(new PhonemizerResponse { part = part, timestamp = timestamp,
                    noteIndexes = heads.Select(p => p.i).ToArray(),
                    phonemes = heads.Select(p => new[] { new Phonemizer.Phoneme { phoneme = "A", position = part.position + p.n.position, index = 0 } }).ToArray() });
                part.Validate(new ValidateOptions { SkipTiming = true, SkipPhonemizer = true }, doc.Project, doc.Project.tracks[part.trackNo]);
                doc.ExecuteCmd(new PhonemizedNotification(part));
            }
            return true;
        });
        async Task<JsonElement> Job(string jobId) {
            for (int i = 0; i < 200; i++) {
                var state = await Call("get_job", new { jobId });
                if (state.GetProperty("status").GetString() is "succeeded" or "failed" or "cancelled") return state;
                await Task.Delay(20);
            }
            throw new TimeoutException("Fixture job did not finish within four seconds.");
        }
        static void WriteInstrumental(string path) {
            using var writer = new WaveFileWriter(path, new WaveFormat(44100, 16, 1));
            var samples = Enumerable.Range(0, 44100).Select(i => (float)(.04 * Math.Sin(i * 2 * Math.PI * 110 / 44100))).ToArray();
            writer.WriteSamples(samples, 0, samples.Length);
        }
        public async ValueTask DisposeAsync() {
            await lifecycle.DisposeAsync(); SingerManager.Inst.Singers.Remove(singer.Id); DocManager.Inst.CommandSink = previousSink;
            OpenUtau.Core.Util.ThreadGuard.SetUiThread(previousUiThread);
            // Delete only this explicitly created fixture directory.
            Directory.Delete(directory, recursive: true);
        }

        [Fact]
        public async Task TwoPartSongWithMelismaTempoAudioAndOverrideSavesReloadsAndExportsRealWav() {
            await BuildSong();
            string instrumental = Path.Combine(directory, "instrumental.wav"); WriteInstrumental(instrumental);
            var input = await Call("register_input_file", new { path = instrumental, purpose = "audio" });
            await Call("import_audio", Write(extra: new { fileHandle = input.GetProperty("fileHandle").GetString(), positionTicks = 480 }));
            await CompletePhonemes();
            var verse = doc.Project.parts.OfType<UVoicePart>().First(); var first = verse.notes.First();
            var state = J(lifecycle.Editor.GetPhonemeState(verse));
            await Call("apply_edit_batch", Write(new object[] { new { type = "set_phoneme_overrides", partId = lifecycle.Editor.Id(verse), phonemeGeneration = state.GetProperty("phonemeGeneration").GetInt64(),
                items = new[] { new { noteId = lifecycle.Editor.Id(first), phonemeIndex = 0, offsetTicks = 10, alias = "A" } } } }));
            doc.SetMainThreadForTest(Thread.CurrentThread); doc.Undo(); Assert.Empty(first.phonemeOverrides); doc.Redo(); Assert.Equal(10, first.phonemeOverrides.Single().offset);
            await CompletePhonemes();
            string projectPath = Path.Combine(directory, "saved song.ustx");
            var output = await Call("resolve_output_target", new { path = projectPath, purpose = "project" });
            await Call("save_project", Write(extra: new { outputHandle = output.GetProperty("outputHandle").GetString() }));
            Assert.True(File.Exists(projectPath));
            var session = lifecycle.Editor.ProjectSessionId;
            var projectInput = await Call("register_input_file", new { path = projectPath, purpose = "project" });
            await Call("open_project", Write(extra: new { fileHandle = projectInput.GetProperty("fileHandle").GetString() }));
            Assert.NotEqual(session, lifecycle.Editor.ProjectSessionId); Assert.Equal(2, doc.Project.parts.OfType<UVoicePart>().Count()); Assert.Single(doc.Project.parts.OfType<UWavePart>());
            Assert.Equal(2, doc.Project.tempos.Count); Assert.Contains(doc.Project.parts.OfType<UVoicePart>().SelectMany(p => p.notes), n => n.lyric == "+");
            Assert.Equal(10, doc.Project.parts.OfType<UVoicePart>().First().notes.First().phonemeOverrides.Single().offset);
            await CompletePhonemes();
            string wavPath = Path.Combine(directory, "mixdown.wav");
            var wavTarget = await Call("resolve_output_target", new { path = wavPath, purpose = "audio" });
            var started = await Call("export_audio", Write(extra: new { kind = "mixdown", fromTicks = 0, toTicks = 2880, outputHandle = wavTarget.GetProperty("outputHandle").GetString() }));
            var job = await Job(started.GetProperty("jobId").GetString()!);
            Assert.True(job.GetProperty("status").GetString() == "succeeded", job.GetRawText());
            Assert.Single(job.GetProperty("artifacts").EnumerateArray()); Assert.True(File.Exists(wavPath));
            using var audio = new WaveFileReader(wavPath);
            Assert.Equal(44100, audio.WaveFormat.SampleRate); Assert.Equal(2, audio.WaveFormat.Channels); Assert.True(audio.TotalTime.TotalSeconds > 2);
            var pcm = new byte[(int)Math.Min(audio.Length, 4000)]; audio.ReadExactly(pcm, 0, pcm.Length); Assert.Contains(pcm, sample => sample != 0);
        }

        [Fact]
        public async Task RendererFailureProducesFailedJobWithoutFinishedArtifact() {
            await BuildSong(); renderer.Fail = true;
            var started = await Call("start_render", Write(extra: new { kind = "preview" }));
            var job = await Job(started.GetProperty("jobId").GetString()!);
            Assert.Equal("failed", job.GetProperty("status").GetString()); Assert.Equal("RENDER_FAILED", job.GetProperty("error").GetProperty("code").GetString()); Assert.Empty(job.GetProperty("artifacts").EnumerateArray());
        }

        [Fact]
        public async Task CancelIsConfirmedAndIdempotentWithoutFinishedArtifact() {
            await BuildSong(); renderer.Block = true;
            var started = await Call("start_render", Write(extra: new { kind = "preview" })); string id = started.GetProperty("jobId").GetString()!;
            await renderer.Started.Task.WaitAsync(TimeSpan.FromSeconds(3));
            await Call("cancel_job", new { jobId = id }); await Call("cancel_job", new { jobId = id });
            var job = await Job(id); Assert.Equal("cancelled", job.GetProperty("status").GetString()); Assert.Empty(job.GetProperty("artifacts").EnumerateArray());
        }

        [Fact]
        public async Task MissingSingerAndAliasCannotProduceSuccessfulAudio() {
            await BuildSong(); doc.Project.tracks[0].Singer = null!;
            var error = await Assert.ThrowsAsync<AutomationException>(() => lifecycle.InvokeAsync("start_render", J(Write(extra: new { kind = "preview" })))); Assert.Equal("MISSING_SINGER", error.Code);
            doc.Project.tracks[0].Singer = singer;
            doc.Project.parts.OfType<UVoicePart>().First().phonemes.First().Error = true;
            var started = await Call("start_render", Write(extra: new { kind = "preview" }));
            var job = await Job(started.GetProperty("jobId").GetString()!);
            Assert.Equal("failed", job.GetProperty("status").GetString()); Assert.Equal("MISSING_ALIAS", job.GetProperty("error").GetProperty("code").GetString()); Assert.Empty(job.GetProperty("artifacts").EnumerateArray());
        }

        [Fact]
        public async Task ProjectSwitchDuringOwnedJobConfirmsCancellation() {
            await BuildSong(); renderer.Block = true;
            var started = await Call("start_render", Write(extra: new { kind = "preview" })); string id = started.GetProperty("jobId").GetString()!;
            await renderer.Started.Task.WaitAsync(TimeSpan.FromSeconds(3));
            await Call("new_project", Write(extra: new { discardChanges = true }));
            var job = await Job(id); Assert.Equal("cancelled", job.GetProperty("status").GetString()); Assert.True(job.GetProperty("stale").GetBoolean()); Assert.Empty(job.GetProperty("artifacts").EnumerateArray());
        }

        [Fact]
        public async Task RendererSpecificExpressionsAndCurvesAreUndoable() {
            await BuildSong(); var part = doc.Project.parts.OfType<UVoicePart>().First(); var note = part.notes.First();
            var result = await Call("apply_edit_batch", Write(new object[] { new { type = "set_expressions", partId = lifecycle.Editor.Id(part), items = new object[] {
                new { noteId = lifecycle.Editor.Id(note), abbr = "vol", phonemeIndex = 0, value = 120 },
                new { abbr = "dyn", points = new[] { new { positionTicks = 0, value = -20 }, new { positionTicks = 960, value = 0 } } },
                new { abbr = "pito", points = new[] { new { positionTicks = 2, value = 6000 }, new { positionTicks = 8, value = 6400 } } },
            } } }));
            Assert.Equal(120, note.phonemeExpressions.Single(e => e.abbr == "vol").value);
            Assert.Equal(new[] { 0, 960 }, part.curves.Single(c => c.abbr == "dyn").xs);
            Assert.Equal(new[] { 0, 10 }, part.maskedCurves.Single(c => c.abbr == "pito").ToSteps().Keys);
            Assert.NotEmpty(result.GetProperty("appliedPartCurves").EnumerateArray());
            doc.SetMainThreadForTest(Thread.CurrentThread); doc.Undo(); Assert.Empty(note.phonemeExpressions); Assert.Empty(part.curves); Assert.Empty(part.maskedCurves);
            doc.Redo(); Assert.Single(note.phonemeExpressions); Assert.Single(part.curves); Assert.Single(part.maskedCurves);
        }
    }
}
