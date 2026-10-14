using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using OpenUtau.Core;
using OpenUtau.Core.Automation;
using OpenUtau.Core.Ustx;
using Xunit;

namespace OpenUtau.Test.Core.Automation {
    [CollectionDefinition("Editor automation", DisableParallelization = true)]
    public sealed class AutomationCollection { }

    [Collection("Editor automation")]
    public sealed class EditorAutomationTest : IDisposable {
        sealed class InlineDispatcher(DocManager doc) : IAutomationDispatcher {
            public Task<T> InvokeAsync<T>(Func<T> action, CancellationToken cancellationToken = default) {
                cancellationToken.ThrowIfCancellationRequested(); doc.SetMainThreadForTest(Thread.CurrentThread);
                return Task.FromResult(action());
            }
        }
        readonly DocManager doc = (DocManager)Activator.CreateInstance(typeof(DocManager), nonPublic: true)!;
        readonly Thread previousUiThread = OpenUtau.Core.Util.ThreadGuard.UiThreadForTest;
        readonly EditorAutomationService service;
        readonly UVoicePart part;
        readonly UNote note;
        readonly string partId, noteId;
        static readonly JsonSerializerOptions Options = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
        static JsonElement Json(object value) => JsonSerializer.SerializeToElement(value, Options);
        static JsonElement Result(object value) => Json(value);
        public EditorAutomationTest() {
            doc.SetMainThreadForTest(Thread.CurrentThread);
            part = new UVoicePart { trackNo = 0, duration = 1920 };
            note = doc.Project.CreateNote(60, 0, 480); note.lyric = "a"; part.notes.Add(note);
            doc.Project.parts.Add(part);
            service = new(doc, new InlineDispatcher(doc)); partId = service.Id(part); noteId = service.Id(note);
        }
        object Batch(params object[] operations) => new { projectSessionId = service.ProjectSessionId, expectedEditRevision = service.EditRevision,
            operationId = Guid.NewGuid().ToString("N"), label = "Test edit", operations };
        async Task<JsonElement> Apply(params object[] operations) => Result(await service.InvokeAsync("apply_edit_batch", Json(Batch(operations))));
        public void Dispose() { service.Dispose(); OpenUtau.Core.Util.ThreadGuard.SetUiThread(previousUiThread); }

        [Fact]
        public async Task PreviewIsInertIncludingOverrideAndHistory() {
            var before = service.EditRevision; var duration = part.duration; var history = doc.LatestUndoGroup;
            await service.InvokeAsync("preview_edit", Json(Batch(new { type = "set_lyrics", partId, items = new[] { new { noteId, lyric = "hello" } } },
                new { type = "add_notes", partId, notes = new[] { new { positionTicks = 5000, durationTicks = 480, tone = 64 } } })));
            Assert.Equal("a", note.lyric); Assert.Single(part.notes); Assert.Empty(note.phonemeOverrides);
            Assert.Equal(before, service.EditRevision); Assert.Equal(duration, part.duration); Assert.Same(history, doc.LatestUndoGroup); Assert.False(doc.HasOpenUndoGroup);
        }

        [Fact]
        public async Task LaterValidationFailureAppliesNothing() {
            var error = await Assert.ThrowsAsync<AutomationException>(() => service.InvokeAsync("apply_edit_batch", Json(Batch(
                new { type = "set_lyrics", partId, items = new[] { new { noteId, lyric = "hello" } } },
                new { type = "update_notes", partId, items = new[] { new { noteId, durationTicks = -1 } } }))));
            Assert.Equal("INVALID_ARGUMENT", error.Code); Assert.Equal("a", note.lyric); Assert.Equal(0, service.EditRevision); Assert.False(doc.GetUndoState(out _));
        }

        [Fact]
        public async Task FailureDuringExecuteRestoresDocumentAndPreservesPreviousHistory() {
            await Apply(new { type = "set_project_info", name = "Before" });
            var undo = doc.LatestUndoGroup; long revision = service.EditRevision;
            service.DuringCommandForTest = () => throw new InvalidOperationException("Injected command failure");
            await Assert.ThrowsAsync<AutomationException>(() => service.InvokeAsync("apply_edit_batch", Json(Batch(
                new { type = "set_lyrics", partId, items = new[] { new { noteId, lyric = "changed" } } },
                new { type = "add_notes", partId, notes = new[] { new { positionTicks = 5000, durationTicks = 1000, tone = 64 } } }))));
            Assert.Equal("a", note.lyric); Assert.Single(part.notes); Assert.Equal(1920, part.duration);
            Assert.Equal(revision, service.EditRevision); Assert.Same(undo, doc.LatestUndoGroup); Assert.False(doc.HasOpenUndoGroup);
            service.DuringCommandForTest = null; doc.Undo(); Assert.Equal("New Project", doc.Project.name);
        }

        [Fact]
        public async Task BusyKeepsUserUndoGroupOpen() {
            doc.StartUndoGroup("User gesture");
            var error = await Assert.ThrowsAsync<AutomationException>(() => service.InvokeAsync("apply_edit_batch", Json(Batch(new { type = "set_project_info", name = "Wrong" }))));
            Assert.Equal("EDITOR_BUSY", error.Code); Assert.True(doc.HasOpenUndoGroup);
            doc.ExecuteCmd(new ChangeNoteLyricCommand(part, note, "user")); doc.EndUndoGroup(); doc.Undo(); Assert.Equal("a", note.lyric);
        }

        [Fact]
        public async Task BatchUndoRedoPreservesIdentityAndSortedNotesAndDuration() {
            var result = await Apply(new { type = "update_notes", partId, items = new[] { new { noteId, positionTicks = 960, lyric = "b" } } },
                new { type = "add_notes", partId, notes = new[] {
                    new { clientRef = "long", positionTicks = 0, durationTicks = 7000, tone = 62 },
                    new { clientRef = "short", positionTicks = 720, durationTicks = 120, tone = 65 } } });
            string longId = result.GetProperty("clientRefs").GetProperty("long").GetString()!;
            Assert.Equal(new[] { 0, 720, 960 }, part.notes.Select(n => n.position)); Assert.Equal(7000, part.Duration); Assert.Equal(noteId, service.Id(note));
            var longNote = part.notes.First(); Assert.Equal(longId, service.Id(longNote));
            doc.Undo(); Assert.Single(part.notes); Assert.Same(note, part.notes.Single()); Assert.Equal("a", note.lyric); Assert.Equal(0, note.position); Assert.Equal(1920, part.Duration);
            doc.Redo(); Assert.Equal(3, part.notes.Count); Assert.Same(longNote, part.notes.First()); Assert.Equal(longId, service.Id(longNote)); Assert.Equal(7000, part.Duration);
        }

        [Fact]
        public async Task ReferencesBuildTracksPartsAndNotesWithinOneBatch() {
            var result = await Apply(new { type = "add_track", clientRef = "chorus", name = "Chorus" },
                new { type = "add_part", clientRef = "phrase", trackId = "chorus", positionTicks = 1920, durationTicks = 960 },
                new { type = "add_notes", partId = "@phrase", notes = new[] { new { clientRef = "first", positionTicks = 0, durationTicks = 480, tone = 67, lyric = "la" } } },
                new { type = "set_lyrics", partId = "phrase", items = new[] { new { noteId = "first", lyric = "hello" } } });
            Assert.Equal(2, doc.Project.tracks.Count); Assert.Equal(2, doc.Project.parts.Count);
            Assert.Equal("hello", ((UVoicePart)doc.Project.parts[1]).notes.Single().lyric);
            Assert.Equal(3, result.GetProperty("clientRefs").EnumerateObject().Count());
            doc.Undo(); Assert.Single(doc.Project.tracks); Assert.Single(doc.Project.parts);
        }

        [Fact]
        public async Task IdempotenceReturnsCommitWithoutApplyingAgainAndRejectsDifferentPayload() {
            var request = Json(Batch(new { type = "set_lyrics", partId, items = new[] { new { noteId, lyric = "once" } } }));
            var first = Result(await service.InvokeAsync("apply_edit_batch", request));
            long revision = service.EditRevision; var undo = doc.LatestUndoGroup;
            var repeated = Result(await service.InvokeAsync("apply_edit_batch", request));
            Assert.Equal(first.GetRawText(), repeated.GetRawText()); Assert.Equal(revision, service.EditRevision); Assert.Same(undo, doc.LatestUndoGroup);
            var altered = Json(new { projectSessionId = service.ProjectSessionId, expectedEditRevision = 0, operationId = request.GetProperty("operationId").GetString(), operations = new[] { new { type = "set_project_info", name = "other" } } });
            var error = await Assert.ThrowsAsync<AutomationException>(() => service.InvokeAsync("apply_edit_batch", altered)); Assert.Equal("IDEMPOTENCY_CONFLICT", error.Code);
        }

        [Fact]
        public async Task SnapshotPaginationRemainsAtOriginalRevisionAfterEdit() {
            await Apply(new { type = "add_notes", partId, notes = new[] { new { positionTicks = 480, durationTicks = 480, tone = 62, lyric = "old" } } });
            var first = Result(await service.InvokeAsync("get_notes", Json(new { partId, limit = 1 })));
            long snapshotRevision = first.GetProperty("editRevision").GetInt64();
            var secondNoteId = service.Id(part.notes.Last());
            await Apply(new { type = "set_lyrics", partId, items = new[] { new { noteId = secondNoteId, lyric = "new" } } });
            var second = Result(await service.InvokeAsync("get_notes", Json(new { partId, limit = 1, cursor = first.GetProperty("nextCursor").GetString() })));
            Assert.Equal(snapshotRevision, second.GetProperty("editRevision").GetInt64());
            Assert.Equal("old", second.GetProperty("items")[0].GetProperty("lyric").GetString());
        }

        [Fact]
        public async Task ReloadChangesSessionAndInvalidatesCursorAndOldWrite() {
            var oldSession = service.ProjectSessionId; var request = Json(Batch(new { type = "set_project_info", name = "old" }));
            var clone = (UVoicePart)part.Clone(); Assert.NotEqual(service.Id(part), service.Id(clone));
            doc.ExecuteCmd(new LoadProjectNotification(doc.Project)); Assert.NotEqual(oldSession, service.ProjectSessionId); Assert.NotEqual(noteId, service.Id(note));
            var error = await Assert.ThrowsAsync<AutomationException>(() => service.InvokeAsync("apply_edit_batch", request)); Assert.Equal("PROJECT_CHANGED", error.Code);
        }

        [Fact]
        public async Task PhonemizationCompletionDoesNotIncrementEditRevisionAndOldIndicesBecomeStale() {
            part.phonemesRevision = 1; note.phonemeIndexes = new[] { 0 };
            var state = Result(service.GetPhonemeState(part)); Assert.Equal("ready", state.GetProperty("status").GetString());
            long generation = state.GetProperty("phonemeGeneration").GetInt64(); long revision = service.EditRevision;
            doc.ExecuteCmd(new PhonemizedNotification(part)); Assert.Equal(revision, service.EditRevision);
            await Apply(new { type = "set_lyrics", partId, items = new[] { new { noteId, lyric = "new" } } });
            var error = await Assert.ThrowsAsync<AutomationException>(() => service.InvokeAsync("apply_edit_batch", Json(Batch(new { type = "set_phoneme_overrides", partId, phonemeGeneration = generation, items = new[] { new { noteId, phonemeIndex = 0, offsetTicks = 20 } } }))));
            Assert.Equal("PHONEME_GENERATION_CONFLICT", error.Code); Assert.Empty(note.phonemeOverrides);
        }

        [Fact]
        public async Task PitchHintsVibratoAndMeterAreUndoableAndReturnActualValues() {
            await Apply(new { type = "set_tempo_map", tempos = new[] { new { positionTicks = 0, bpm = 100 }, new { positionTicks = 1920, bpm = 120 } } },
                new { type = "set_time_signature_map", timeSignatures = new[] { new { barPosition = 0, numerator = 3, denominator = 4 } } },
                new { type = "set_phonetic_hints", partId, items = new[] { new { noteId, lyric = "hello", phoneticHint = "h e l o" } } },
                new { type = "set_pitch", partId, items = new[] { new { noteId, points = new[] { new { xMs = -20, yCents = 0 }, new { xMs = 60, yCents = 100 } } } } },
                new { type = "set_vibrato", partId, items = new[] { new { noteId, lengthPercent = 50, periodMs = 180, depthCents = 40 } } });
            Assert.Equal("hello [h e l o]", note.lyric); Assert.Equal(10, note.pitch.data[1].Y); Assert.Equal(50, note.vibrato.length); Assert.Equal(3, doc.Project.timeSignatures[0].beatPerBar);
            var read = Result(await service.InvokeAsync("get_notes", Json(new { partId })));
            Assert.Equal("hello", read.GetProperty("items")[0].GetProperty("lyric").GetString()); Assert.Equal("h e l o", read.GetProperty("items")[0].GetProperty("phoneticHint").GetString());
            doc.Undo(); Assert.Equal("a", note.lyric); Assert.Single(doc.Project.tempos); Assert.Equal(4, doc.Project.timeSignatures[0].beatPerBar);
        }

        [Fact]
        public async Task GuardedRevertRejectsInterveningUserChanges() {
            var applied = await Apply(new { type = "set_project_info", name = "AI" });
            doc.StartUndoGroup("User"); doc.ExecuteCmd(new ChangeNoteLyricCommand(part, note, "user")); doc.EndUndoGroup();
            var error = await Assert.ThrowsAsync<AutomationException>(() => service.InvokeAsync("revert_operation", Json(new { projectSessionId = service.ProjectSessionId, expectedEditRevision = service.EditRevision, operationId = Guid.NewGuid().ToString(), targetOperationId = applied.GetProperty("operationId").GetString() })));
            Assert.Equal("REVISION_CONFLICT", error.Code); Assert.Equal("user", note.lyric);
        }

        [Fact]
        public async Task MidiImportPreservesPriorUndoHistoryAndIsIdempotent() {
            string path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".mid");
            try {
                var source = new UProject(); var phrase = new UVoicePart { trackNo = 0, duration = 1920 };
                phrase.notes.Add(source.CreateNote(67, 0, 480)); source.parts.Add(phrase); OpenUtau.Core.Format.MidiWriter.Save(path, source);
                await Apply(new { type = "set_project_info", name = "Existing" }); var previousUndo = doc.LatestUndoGroup;
                var args = Json(new { projectSessionId = service.ProjectSessionId, expectedEditRevision = service.EditRevision, operationId = Guid.NewGuid().ToString(), fileHandle = "test", importTempo = true });
                await service.ImportMidiAsync(path, args); Assert.Equal(2, doc.Project.parts.Count);
                await service.ImportMidiAsync(path, args); Assert.Equal(2, doc.Project.parts.Count);
                doc.Undo(); Assert.Single(doc.Project.parts); Assert.Same(previousUndo, doc.LatestUndoGroup); doc.Undo(); Assert.Equal("New Project", doc.Project.name);
            } finally { File.Delete(path); }
        }

        [Fact]
        public void LyricMappingKeepsExplicitMelismasAndRejectsGuesswork() {
            Assert.Equal(new[] { "hel", "lo", "+", "world" }, LyricMapping.Split(" hel\nlo  + world "));
            Assert.Equal("hello", LyricMapping.VisibleLyric("hello [h e l o]"));
        }

        [Theory]
        [InlineData(1000)]
        [InlineData(5000)]
        [InlineData(10000)]
        public async Task LargeReadsArePaginatedWithoutMutableReferences(int count) {
            part.notes.Clear();
            for (int i = 0; i < count; i++) part.notes.Add(doc.Project.CreateNote(60, i * 120, 120));
            string? cursor = null; int total = 0;
            do {
                var args = new Dictionary<string, object> { ["partId"] = partId, ["limit"] = 500 };
                if (cursor != null) args["cursor"] = cursor;
                var page = Result(await service.InvokeAsync("get_notes", Json(args))); total += page.GetProperty("items").GetArrayLength();
                cursor = page.GetProperty("nextCursor").GetString();
            } while (cursor != null);
            Assert.Equal(count, total);
        }

        [Fact]
        public void ContractValidatorRejectsUnknownFields() {
            var validator = new AutomationContractValidator("[{\"name\":\"openutau_test\",\"inputSchema\":{\"type\":\"object\",\"additionalProperties\":false,\"properties\":{\"name\":{\"type\":\"string\"}},\"required\":[\"name\"]}}]");
            validator.ValidateToolArguments("test", Json(new { name = "valid" }));
            var error = Assert.Throws<AutomationException>(() => validator.ValidateToolArguments("test", Json(new { name = "valid", unexpected = 1 })));
            Assert.Equal("INVALID_ARGUMENT", error.Code);
        }

        sealed class SkipPhonemizerCommand(UVoicePart part, UNote note) : NoteCommand(part, note) {
            public override ValidateOptions ValidateOptions => new() { Part = part, SkipTiming = true, SkipPhonemizer = true, SkipPhoneme = true };
            public override void Execute() => note.tuning = 20;
            public override void Unexecute() => note.tuning = 0;
            public override string ToString() => "Tune without phonemizer";
        }

        [Fact]
        public void PitchAndMixerChangesDoNotWaitForNonexistentPhonemizerResponse() {
            part.phonemesRevision = 1; Assert.True(service.ArePhonemesReady());
            doc.StartUndoGroup("Pitch"); doc.ExecuteCmd(new SkipPhonemizerCommand(part, note)); doc.EndUndoGroup();
            Assert.True(service.ArePhonemesReady()); Assert.Equal(1, service.EditRevision);
            doc.ExecuteCmd(new VolumeChangeNotification(0, -3)); Assert.True(service.ArePhonemesReady());
            Assert.Equal(2, service.EditRevision);
        }

        [Fact]
        public async Task MusicalPreviewReturnsDetachedBeforeAndAfter() {
            var preview = Result(await service.InvokeAsync("preview_edit", Json(Batch(new { type = "update_notes", partId, items = new[] { new { noteId, lyric = "hello", durationTicks = 960, tone = 64 } } }))));
            var change = preview.GetProperty("noteChanges")[0];
            Assert.Equal("a", change.GetProperty("before").GetProperty("lyric").GetString()); Assert.Equal("hello", change.GetProperty("after").GetProperty("lyric").GetString());
            Assert.Equal(480, change.GetProperty("before").GetProperty("durationTicks").GetInt32()); Assert.Equal(960, change.GetProperty("after").GetProperty("durationTicks").GetInt32());
            Assert.Equal("a", note.lyric); Assert.Equal(480, note.duration);
        }

        [Fact]
        public async Task RevertUsesNewOperationIdAndIsIdempotent() {
            var applied = await Apply(new { type = "set_project_info", name = "AI" });
            var args = Json(new { projectSessionId = service.ProjectSessionId, expectedEditRevision = service.EditRevision, operationId = Guid.NewGuid().ToString(), targetOperationId = applied.GetProperty("operationId").GetString() });
            var first = Result(await service.InvokeAsync("revert_operation", args));
            var second = Result(await service.InvokeAsync("revert_operation", args));
            Assert.Equal(first.GetRawText(), second.GetRawText()); Assert.Equal("New Project", doc.Project.name);
        }

        [Fact]
        public async Task ExpiredAndReloadedSnapshotCursorsAreRejected() {
            await Apply(new { type = "add_notes", partId, notes = new[] { new { positionTicks = 480, durationTicks = 480, tone = 62 } } });
            var page = Result(await service.InvokeAsync("get_notes", Json(new { partId, limit = 1 })));
            doc.ExecuteCmd(new LoadProjectNotification(doc.Project));
            var error = await Assert.ThrowsAsync<AutomationException>(() => service.InvokeAsync("get_notes", Json(new { partId, limit = 1, cursor = page.GetProperty("nextCursor").GetString() })));
            Assert.Equal("SNAPSHOT_EXPIRED", error.Code);
        }

        sealed class DeferredDispatcher(DocManager doc) : IAutomationDispatcher {
            public readonly TaskCompletionSource Enqueued = new(TaskCreationOptions.RunContinuationsAsynchronously);
            public Action? Execute;
            public Task<T> InvokeAsync<T>(Func<T> action, CancellationToken cancellationToken = default) {
                var result = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
                Execute = () => { try { cancellationToken.ThrowIfCancellationRequested(); doc.SetMainThreadForTest(Thread.CurrentThread); result.SetResult(action()); } catch (Exception e) { result.SetException(e); } };
                Enqueued.TrySetResult(); return result.Task;
            }
        }

        [Fact]
        public async Task WorkerMutationOnlyReportsSuccessAfterAwaitedUiCommit() {
            var dispatch = new DeferredDispatcher(doc);
            using var editor = new EditorAutomationService(doc, dispatch);
            var arguments = Json(new { projectSessionId = editor.ProjectSessionId, expectedEditRevision = 0, operationId = Guid.NewGuid().ToString(), operations = new[] { new { type = "set_project_info", name = "Awaited" } } });
            var mutation = Task.Run(() => editor.InvokeAsync("apply_edit_batch", arguments));
            await dispatch.Enqueued.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.False(mutation.IsCompleted); Assert.Equal("New Project", doc.Project.name);
            dispatch.Execute!();
            var result = Result(await mutation); Assert.Equal("committed", result.GetProperty("status").GetString()); Assert.Equal("Awaited", doc.Project.name); Assert.False(doc.HasOpenUndoGroup);
        }

        [Fact]
        public async Task ChangedImportInputBeforeCommitCreatesNoEditAndRecordsFailure() {
            string path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".mid");
            try {
                var source = new UProject(); var phrase = new UVoicePart { trackNo = 0, duration = 1920 }; phrase.notes.Add(source.CreateNote(67, 0, 480)); source.parts.Add(phrase);
                OpenUtau.Core.Format.MidiWriter.Save(path, source);
                string id = Guid.NewGuid().ToString(); var args = Json(new { projectSessionId = service.ProjectSessionId, expectedEditRevision = service.EditRevision, operationId = id });
                var error = await Assert.ThrowsAsync<AutomationException>(() => service.ImportMidiAsync(path, args, default, () => throw new AutomationException("REVISION_CONFLICT", "Fixture file changed")));
                Assert.Equal("REVISION_CONFLICT", error.Code); Assert.Single(doc.Project.parts); Assert.Single(doc.Project.tracks); Assert.Equal(0, service.EditRevision);
                Assert.Equal("failed", Result(service.Journal.Get(id)).GetProperty("status").GetString());
            } finally { File.Delete(path); }
        }

        [Fact]
        public async Task OrdinaryUndoPreservesUnrelatedLaterTransientMixerPitchAndNotes() {
            await Apply(new { type = "set_lyrics", partId, items = new[] { new { noteId, lyric = "AI" } } },
                new { type = "add_notes", partId, notes = new[] { new { positionTicks = 480, durationTicks = 960, tone = 67 } } });
            var userNote = doc.Project.CreateNote(72, 9000, 480);
            doc.ApplyTransient(new UCommand[] { new AddNoteCommand(part, userNote), new ChangeNoteTuningCommand(part, note, 25) });
            doc.Project.tracks[0].Volume = -7; doc.ExecuteCmd(new VolumeChangeNotification(0, -7));
            doc.Project.tracks[0].Pan = .4; doc.ExecuteCmd(new PanChangeNotification(0, .4));
            int userDuration = part.duration;
            doc.Undo();
            Assert.Equal("a", note.lyric); Assert.Equal(25, note.tuning); Assert.Contains(userNote, part.notes);
            Assert.Equal(2, part.notes.Count); Assert.Equal(userDuration, part.duration); Assert.Equal(-7, doc.Project.tracks[0].Volume); Assert.Equal(.4, doc.Project.tracks[0].Pan);
            doc.Redo(); Assert.Equal("AI", note.lyric); Assert.Contains(userNote, part.notes); Assert.Equal(25, note.tuning); Assert.Equal(-7, doc.Project.tracks[0].Volume);
        }

        [Fact]
        public async Task UndoTrackCreationKeepsLaterTransientUserPartAndItsTrackIdentity() {
            await Apply(new { type = "add_track", clientRef = "ai-track", name = "AI track" },
                new { type = "add_part", clientRef = "ai-part", trackId = "ai-track", durationTicks = 960, positionTicks = 0 });
            var track = doc.Project.tracks[1]; string trackId = service.Id(track);
            var userPart = new UVoicePart { trackNo = 1, position = 960, duration = 960, name = "User recording" };
            doc.ApplyTransient(new[] { new AddPartCommand(doc.Project, userPart) }, new ValidateOptions { Part = userPart, SkipTiming = true, SkipPhonemizer = true, SkipPhoneme = true }, preRender: false);
            doc.Undo();
            Assert.Contains(userPart, doc.Project.parts); Assert.Equal(2, doc.Project.parts.Count);
            Assert.Contains(track, doc.Project.tracks); Assert.Same(track, doc.Project.tracks[userPart.trackNo]); Assert.Equal(trackId, service.Id(track));
            Assert.DoesNotContain(doc.Project.parts, p => p != userPart && p.trackNo == userPart.trackNo);
            doc.Redo(); Assert.Contains(userPart, doc.Project.parts); Assert.Equal(3, doc.Project.parts.Count); Assert.Equal(2, doc.Project.tracks.Count);
        }

        [Fact]
        public async Task RepeatedFailedWriteRetainsErrorEnvelopeAndDoesNotBecomeSuccessful() {
            var args = Json(Batch(new { type = "set_project_info", name = "Must not commit" },
                new { type = "update_notes", partId, items = new[] { new { noteId, durationTicks = -1 } } }));
            await Assert.ThrowsAsync<AutomationException>(() => service.InvokeAsync("apply_edit_batch", args));
            var repeated = Result(await service.InvokeAsync("apply_edit_batch", args));
            Assert.False(repeated.GetProperty("ok").GetBoolean()); Assert.Equal("failed", repeated.GetProperty("status").GetString());
            Assert.Equal("INVALID_ARGUMENT", repeated.GetProperty("error").GetProperty("code").GetString());
            Assert.Equal("New Project", doc.Project.name); Assert.Equal(0, service.EditRevision); Assert.False(doc.GetUndoState(out _));
        }
    }
}
