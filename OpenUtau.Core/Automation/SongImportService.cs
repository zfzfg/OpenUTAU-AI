using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Melanchall.DryWetMidi.Core;
using Melanchall.DryWetMidi.Interaction;
using OpenUtau.Core.Ustx;

namespace OpenUtau.Core.Automation {
    public sealed partial class EditorAutomationService {
        public Task<object> ImportMidiAsync(string path, JsonElement arguments, CancellationToken cancellationToken = default, Action? validateInputBeforeCommit = null) =>
            RunImportAsync(arguments, entry => ImportMidiCoreAsync(path, arguments, entry, cancellationToken, validateInputBeforeCommit), cancellationToken);
        public Task<object> ImportAudioAsync(string path, JsonElement arguments, CancellationToken cancellationToken = default, Action? validateInputBeforeCommit = null) =>
            RunImportAsync(arguments, entry => ImportAudioCoreAsync(path, arguments, entry, cancellationToken, validateInputBeforeCommit), cancellationToken);
        async Task<object> RunImportAsync(JsonElement args, Func<AutomationOperationJournal.Entry?, Task<object>> action, CancellationToken ct) {
            var prepared = await dispatcher.InvokeAsync(() => {
                if (args.Bool("preview")) { Guard(args); return (entry: (AutomationOperationJournal.Entry?)null, repeat: (object?)null); }
                var entry = Journal.Register(args.String("operationId"), args, args.String("projectSessionId"));
                if (entry.Status != "queued") return (entry: (AutomationOperationJournal.Entry?)entry, repeat: entry.Result ?? Journal.Get(entry.Id));
                entry.Status = "validating";
                return (entry: (AutomationOperationJournal.Entry?)entry, repeat: (object?)null);
            }, ct).ConfigureAwait(false);
            if (prepared.repeat != null) return prepared.repeat;
            try { return await action(prepared.entry).ConfigureAwait(false); }
            catch (Exception ex) {
                if (prepared.entry != null) await dispatcher.InvokeMaintenanceAsync(() => {
                    prepared.entry.Status = ex is OperationCanceledException ? "cancelled_before_commit" : "failed";
                    prepared.entry.Result = new { ok = false, operationId = prepared.entry.Id, status = prepared.entry.Status, error = new { code = ex is AutomationException ae ? ae.Code : ex is OperationCanceledException ? "CANCELLED" : "INVALID_ARGUMENT", message = ex.Message } };
                    return true;
                }).ConfigureAwait(false);
                throw;
            }
        }
        /// <summary>The Desktop validates its file handle before passing the path here.</summary>
        async Task<object> ImportMidiCoreAsync(string path, JsonElement arguments, AutomationOperationJournal.Entry? entry, CancellationToken cancellationToken, Action? validateInputBeforeCommit) {
            var sourceProject = await dispatcher.InvokeAsync(() => {
                Guard(arguments);
                return new UProject();
            }, cancellationToken).ConfigureAwait(false);
            var imported = await Task.Run(() => {
                cancellationToken.ThrowIfCancellationRequested();
                var file = MidiFile.Read(path, Format.MidiWriter.BaseReadingSettings());
                if (file.TimeDivision is not TicksPerQuarterNoteTimeDivision ppq) throw Arguments.Invalid("SMPTE MIDI time division is unsupported.");
                var parts = Format.MidiWriter.Load(path, sourceProject);
                var map = file.GetTempoMap();
                return (parts, tempos: Format.MidiWriter.ParseTempos(map, ppq.TicksPerQuarterNote),
                    signatures: Format.MidiWriter.ParseTimeSignatures(map, ppq.TicksPerQuarterNote));
            }, cancellationToken).ConfigureAwait(false);
            if (imported.parts.Count == 0) throw Arguments.Invalid("MIDI contains no note tracks.");
            if (imported.parts.Sum(p => p.notes.Count) > 5000 || imported.parts.Count > 32) throw new AutomationException("LIMIT_EXCEEDED", "A MIDI import is limited to 5000 notes and 32 tracks. Split larger arrangements first.");
            var operations = new List<object>();
            if (arguments.Bool("importTempo", false)) {
                operations.Add(new { type = "set_tempo_map", tempos = imported.tempos.Select(t => new { positionTicks = t.position, bpm = t.bpm }).ToArray() });
                operations.Add(new { type = "set_time_signature_map", timeSignatures = imported.signatures.Select(t => new { barPosition = t.barPosition, numerator = t.beatPerBar, denominator = t.beatUnit }).ToArray() });
            }
            int offset = Arguments.Nonnegative(arguments.Int("positionTicks", 0), "positionTicks");
            for (int i = 0; i < imported.parts.Count; i++) {
                var part = imported.parts[i]; string trackRef = "midi_track_" + i, partRef = "midi_part_" + i;
                string trackId;
                if (arguments.TryGetProperty("trackId", out _)) trackId = arguments.String("trackId");
                else {
                    var track = new Dictionary<string, object> { ["type"] = "add_track", ["clientRef"] = trackRef, ["name"] = part.name == "New Part" ? Path.GetFileNameWithoutExtension(path) + " " + (i + 1) : part.name };
                    if (arguments.TryGetProperty("singerId", out _)) track["singerId"] = arguments.String("singerId");
                    if (arguments.TryGetProperty("phonemizerId", out _)) track["phonemizerId"] = arguments.String("phonemizerId");
                    operations.Add(track); trackId = trackRef;
                }
                operations.Add(new { type = "add_part", trackId, clientRef = partRef, name = part.name,
                    positionTicks = offset, durationTicks = Math.Max(sourceProject.resolution, part.notes.Max(n => n.End)) });
                operations.Add(new { type = "add_notes", partId = partRef,
                    notes = part.notes.Select((n, index) => new { clientRef = "midi_note_" + i + "_" + index, positionTicks = n.position, durationTicks = Math.Max(10, n.duration), tone = n.tone, lyric = n.lyric, tuningCents = n.tuning }).ToArray() });
            }
            var payload = arguments.EnumerateObject().Where(p => p.Name is not ("preview" or "operations")).ToDictionary(p => p.Name, p => (object)p.Value.Clone());
            payload["operations"] = operations;
            payload["label"] = arguments.String("label", "Import MIDI");
            var batch = JsonSerializer.SerializeToElement(payload);
            if (arguments.Bool("preview")) return await dispatcher.InvokeAsync(() => { validateInputBeforeCommit?.Invoke(); return Preview(batch); }, cancellationToken).ConfigureAwait(false);
            return await dispatcher.InvokeAsync(() => {
                Guard(arguments);
                if (doc.HasOpenUndoGroup) throw new AutomationException("EDITOR_BUSY", "Finish the editor gesture before importing MIDI.");
                var plan = new AutomationEditPlanner(doc.Project, Identities, revisions); plan.Plan(batch); validateInputBeforeCommit?.Invoke(); plan.PrepareCommit();
                cancellationToken.ThrowIfCancellationRequested();
                long revision = EditRevision;
                try { return CommitPlan(plan, batch, entry!); }
                catch {
                    if (doc.HasOpenUndoGroup && entry!.UndoGroup != null) {
                        try { doc.AbortUndoGroup(); } finally { revisions.RestoreFailedRevision(revision); }
                    }
                    throw;
                }
            }, cancellationToken).ConfigureAwait(false);
        }

        async Task<object> ImportAudioCoreAsync(string path, JsonElement arguments, AutomationOperationJournal.Entry? entry, CancellationToken cancellationToken, Action? validateInputBeforeCommit) {
            var metadata = await Task.Run(() => {
                using var stream = Format.Wave.OpenFile(path);
                if (stream.TotalTime.TotalMilliseconds <= 0 || stream.TotalTime.TotalHours > 2) throw Arguments.Invalid("Audio duration must be positive and at most two hours.");
                return (duration: stream.TotalTime.TotalMilliseconds, channels: stream.WaveFormat.Channels, rate: stream.WaveFormat.SampleRate);
            }, cancellationToken).ConfigureAwait(false);
            return await dispatcher.InvokeAsync(() => {
                Guard(arguments);
                var plan = new AutomationEditPlanner(doc.Project, Identities, revisions);
                string trackId;
                if (arguments.TryGetProperty("trackId", out _)) trackId = arguments.String("trackId");
                else {
                    var scaffold = JsonSerializer.SerializeToElement(new { operations = new[] { new { type = "add_track", clientRef = "audio_track", name = Path.GetFileNameWithoutExtension(path) } } });
                    plan.Plan(scaffold); trackId = "audio_track";
                }
                var track = plan.ClientRefs.TryGetValue(trackId.TrimStart('@'), out var newTrack) ? (UTrack)newTrack : plan.After.Project.tracks.FirstOrDefault(t => Id(plan.After.Target(t)) == trackId) ?? throw new AutomationException("ENTITY_NOT_FOUND", "Target audio track does not exist.");
                var wave = new UWavePart { FilePath = path, trackNo = plan.After.Project.tracks.IndexOf(track),
                    position = Arguments.Nonnegative(arguments.Int("positionTicks", 0), "positionTicks"), fileDurationMs = metadata.duration,
                    channels = metadata.channels, sampleRate = metadata.rate };
                wave.Validate(default, plan.After.Project, track);
                plan.After.Project.parts.Add(wave);
                var target = new UWavePart { FilePath = path, position = wave.position, trackNo = wave.trackNo, fileDurationMs = metadata.duration };
                plan.After.Targets.Add(wave, target); plan.ClientRefs["audio_part"] = wave;
                plan.Changes.Add(new { type = "import_audio", durationMs = metadata.duration, channels = metadata.channels, sampleRate = metadata.rate, name = wave.name });
                validateInputBeforeCommit?.Invoke();
                if (arguments.Bool("preview")) return (object)new { status = "preview", projectSessionId = ProjectSessionId, editRevision = EditRevision, changes = plan.Changes.ToArray() };
                if (doc.HasOpenUndoGroup) throw new AutomationException("EDITOR_BUSY", "Finish the current editor gesture before importing audio.");
                cancellationToken.ThrowIfCancellationRequested();
                long previousRevision = EditRevision;
                try {
                    entry!.Status = "validating";
                    target.Load(plan.After.Project);
                    return CommitPlan(plan, arguments, entry);
                } catch (Exception ex) {
                    if (doc.HasOpenUndoGroup && entry!.UndoGroup != null) {
                        try { doc.AbortUndoGroup(); } finally { revisions.RestoreFailedRevision(previousRevision); }
                    }
                    entry!.Status = "failed"; entry.Result = new { ok = false, operationId = entry.Id, status = "failed",
                        error = new { code = ex is AutomationException error ? error.Code : "INVALID_ARGUMENT", message = ex.Message } }; throw;
                }
            }, cancellationToken).ConfigureAwait(false);
        }
    }
}
