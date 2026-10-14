using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using OpenUtau.Api;
using OpenUtau.Core.Ustx;

namespace OpenUtau.Core.Automation {
    /// <summary>The editor is the sole document owner. Every operation is awaited through its UI dispatcher.</summary>
    public sealed partial class EditorAutomationService : IDisposable {
        readonly DocManager doc;
        readonly IAutomationDispatcher dispatcher;
        readonly AutomationRevisionTracker revisions;
        internal Action? DuringCommandForTest { get; set; }
        readonly Dictionary<string, ReadSnapshot> snapshots = new();
        static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
        public AutomationIdentityRegistry Identities { get; } = new();
        public AutomationOperationJournal Journal { get; } = new();
        public string ProjectSessionId => revisions.ProjectSessionId;
        public long EditRevision => revisions.EditRevision;
        public event Action? Changed;
        sealed record ReadSnapshot(string Session, long Revision, string Filter, JsonElement[] Items, object? Derived, DateTime Expires);
        public EditorAutomationService(DocManager doc, IAutomationDispatcher dispatcher) {
            this.doc = doc; this.dispatcher = dispatcher; revisions = new(doc);
            revisions.Changed += OnChanged;
            revisions.ProjectChanged += OnProjectChanged;
        }
        void OnChanged() => Changed?.Invoke();
        void OnProjectChanged() { Identities.Reset(); snapshots.Clear(); }
        public void EnsureCurrent(string session, long revision) {
            if (session != ProjectSessionId) throw new AutomationException("PROJECT_CHANGED", "The project was replaced. Read and bind the new project before writing.", new { projectSessionId = ProjectSessionId, editRevision = EditRevision });
            if (revision != EditRevision) throw new AutomationException("REVISION_CONFLICT", "The project changed. Read it again and prepare a new edit.", new { projectSessionId = ProjectSessionId, editRevision = EditRevision });
        }
        void Guard(JsonElement arguments) => EnsureCurrent(arguments.String("projectSessionId"), arguments.Long("expectedEditRevision"));
        public Task<object> InvokeAsync(string operation, JsonElement arguments, CancellationToken cancellationToken = default) {
            if (operation.StartsWith("openutau_", StringComparison.Ordinal)) operation = operation[9..];
            if (operation == "wait_for_phonemes") return WaitForPhonemesAsync(arguments, cancellationToken);
            return dispatcher.InvokeAsync(() => Invoke(operation, arguments, cancellationToken), cancellationToken);
        }
        object Invoke(string operation, JsonElement arguments, CancellationToken ct) {
            ct.ThrowIfCancellationRequested();
            if (arguments.ValueKind != JsonValueKind.Object) throw Arguments.Invalid("arguments must be an object.");
            try {
                return operation switch {
                    "get_project" => GetProject(),
                    "get_capabilities" => GetCapabilities(),
                    "list_singers" => Page(operation, arguments, () => SingerManager.Inst.Singers.Values.Where(s => s.Found && s.Name.Contains(arguments.String("query", ""), StringComparison.OrdinalIgnoreCase)).OrderBy(s => s.Name).Select(s => (object)new { singerId = s.Id, name = s.Name, type = s.SingerType.ToString(), found = s.Found, loaded = s.Loaded }).ToArray()),
                    "list_phonemizers" => Page(operation, arguments, () => PhonemizerFactory.GetAll().Select(f => (object)new { phonemizerId = f.type.FullName, name = f.name, tag = f.tag, language = f.language, phoneticHintAlphabet = "unknown", capabilities = "unknown" }).ToArray()),
                    "get_notes" => GetNotes(arguments),
                    "get_phonemes" => GetPhonemes(arguments),
                    "get_diagnostics" => GetDiagnostics(arguments),
                    "preview_edit" => Preview(arguments),
                    "apply_edit_batch" => ApplyBatch(arguments, ct),
                    "get_operation" => Journal.Get(arguments.String("operationId")),
                    "revert_operation" => Revert(arguments),
                    _ => throw new AutomationException("UNSUPPORTED_CAPABILITY", "Unknown editor operation: " + operation),
                };
            } catch (OverflowException) { throw Arguments.Invalid("Musical time exceeds the supported integer tick range."); }
            catch (InvalidOperationException e) { throw Arguments.Invalid(e.Message); }
            catch (FormatException e) { throw Arguments.Invalid(e.Message); }
        }
        public string Id(object entity) => Identities.GetId(entity);
        public UPart FindPart(string id) => doc.Project.parts.FirstOrDefault(p => Id(p) == id) ?? throw new AutomationException("ENTITY_NOT_FOUND", "Part does not exist: " + id);
        public bool ArePhonemesReady(IEnumerable<string>? partIds = null) {
            var parts = partIds == null ? doc.Project.parts.OfType<UVoicePart>() : partIds.Select(FindPart).OfType<UVoicePart>();
            return parts.All(revisions.IsReady);
        }
        public object GetPhonemeState(UVoicePart part) => revisions.GetPhonemeState(part);
        object GetProject() {
            var p = doc.Project;
            return new { projectSessionId = ProjectSessionId, editRevision = EditRevision,
                name = p.name, comment = p.comment, filePath = p.FilePath, resolution = p.resolution, key = p.key,
                units = new { notePosition = "ticks relative to part", partPosition = "ticks relative to project", pitch = "cents", pitchPointTime = "milliseconds relative to note", vibratoPeriod = "milliseconds", expression = "descriptor dependent" },
                tempos = p.tempos.Select(t => new { positionTicks = t.position, bpm = t.bpm }).ToArray(),
                timeSignatures = p.timeSignatures.Select(t => new { barPosition = t.barPosition, numerator = t.beatPerBar, denominator = t.beatUnit }).ToArray(),
                tracks = p.tracks.Select(t => new { trackId = Id(t), index = p.tracks.IndexOf(t), name = t.TrackName, singerId = t.Singer?.Id,
                    phonemizerId = t.Phonemizer.GetType().FullName, renderer = t.RendererSettings.renderer, volumeDb = t.Volume, pan = t.Pan, mute = t.Mute, solo = t.Solo }).ToArray(),
                parts = p.parts.Select(part => new { partId = Id(part), trackId = Id(p.tracks[part.trackNo]), kind = part is UVoicePart ? "voice" : "wave", name = part.name,
                    positionTicks = part.position, durationTicks = part.Duration, noteCount = (part as UVoicePart)?.notes.Count ?? 0,
                    phonemes = part is UVoicePart voice ? GetPhonemeState(voice) : null }).ToArray() };
        }
        object GetCapabilities() => new { apiVersion = "1", projectSessionId = ProjectSessionId, editRevision = EditRevision,
            limits = new { operationsPerBatch = 128, itemsPerBatch = 5000, notesPerPage = 500, snapshotTtlSeconds = 120, snapshotCount = 32, snapshotBytes = 32 * 1024 * 1024, journalEntries = 4096 },
            editOperations = new[] { "set_project_info", "set_tempo_map", "set_time_signature_map", "add_track", "update_track", "remove_track", "add_part", "update_part", "remove_part", "add_notes", "update_notes", "remove_notes", "set_lyrics", "set_phonetic_hints", "set_phoneme_overrides", "clear_phoneme_overrides", "set_pitch", "set_vibrato", "set_expressions" },
            phoneticHints = new { format = "lyric [hint]", alphabet = "phonemizer", universalIpaConversion = false },
            tracks = doc.Project.tracks.Select(t => new { trackId = Id(t), renderer = t.RendererSettings.renderer,
                expressions = t.GetSupportedExps(doc.Project).Select(e => new { abbr = e.abbr, name = e.name, type = e.type.ToString(), min = e.min, max = e.max, defaultValue = e.CustomDefaultValue, options = e.options?.ToArray() }).ToArray() }).ToArray() };
        public AutomationNoteDto SnapshotNote(UNote n) => SnapshotNote(n, Id(n));
        static AutomationNoteDto SnapshotNote(UNote n, string id) => new(id, n.position, n.duration, n.tone, n.tuning, LyricMapping.VisibleLyric(n.lyric), LyricMapping.Hint(n.lyric),
            new { snapFirst = n.pitch.snapFirst, points = n.pitch.data.Select(p => new { xMs = p.X, yCents = p.Y * 10, shape = p.shape.ToString() }).ToArray() },
            new { lengthPercent = n.vibrato.length, periodMs = n.vibrato.period, depthCents = n.vibrato.depth, fadeInPercent = n.vibrato.@in, fadeOutPercent = n.vibrato.@out, shiftPercent = n.vibrato.shift, driftPercent = n.vibrato.drift, volumeLinkPercent = n.vibrato.volLink },
            n.phonemeOverrides.Select(o => (object)new { phonemeIndex = o.index, alias = o.phoneme, offsetTicks = o.offset, preutterDeltaMs = o.preutterDelta, overlapDeltaMs = o.overlapDelta, attackTimeDeltaMs = o.attackTimeDelta, releaseTimeDeltaMs = o.releaseTimeDelta }).ToArray(),
            n.phonemeExpressions.Select(e => (object)new { abbr = e.abbr, phonemeIndex = e.index, value = e.value }).ToArray());
        object GetNotes(JsonElement args) {
            if (args.TryGetProperty("cursor", out _)) return Page("get_notes", args, () => []);
            var part = FindPart(args.String("partId")) as UVoicePart ?? throw Arguments.Invalid("A vocal part is required.");
            int start = args.Int("fromTicks", 0), end = args.Int("toTicks", int.MaxValue);
            if (start < 0 || end <= start) throw Arguments.Invalid("Invalid tick range.");
            return Page("get_notes", args, () => part.notes.Where(n => n.position < end && n.End > start).Select(n => (object)SnapshotNote(n)).ToArray(), GetPhonemeState(part));
        }
        object GetPhonemes(JsonElement args) {
            if (args.TryGetProperty("cursor", out _)) return Page("get_phonemes", args, () => []);
            var p = FindPart(args.String("partId")) as UVoicePart ?? throw Arguments.Invalid("A vocal part is required.");
            var filter = args.TryGetProperty("noteIds", out _) ? args.Array("noteIds").Select(n => n.GetString()).ToHashSet() : null;
            return Page("get_phonemes", args, () => p.phonemes.Where(ph => ph.Parent != null && (filter == null || filter.Contains(Id(ph.Parent)))).Select(ph => (object)new {
                noteId = Id(ph.Parent), phonemeIndex = ph.index, alias = ph.phoneme, rawAlias = ph.rawPhoneme, mappedAlias = ph.phonemeMapped,
                positionTicks = ph.position, durationTicks = ph.Duration, preutterMs = ph.preutter, overlapMs = ph.overlap,
                error = ph.Error, errorMessage = ph.ErrorException?.Message,
            }).ToArray(), GetPhonemeState(p));
        }
        object GetDiagnostics(JsonElement args) {
            if (args.TryGetProperty("cursor", out _)) return Page("get_diagnostics", args, () => []);
            var diagnostics = new List<object>();
            var parts = args.TryGetProperty("partId", out _) ? new[] { FindPart(args.String("partId")) } : doc.Project.parts.ToArray();
            foreach (var p in parts.OfType<UVoicePart>()) {
                var track = doc.Project.tracks[p.trackNo];
                if (track.Singer == null || !track.Singer.Found) diagnostics.Add(new { code = "MISSING_SINGER", partId = Id(p), message = "Select an installed singer for this track." });
                foreach (var n in p.notes.Where(n => n.duration < 10 || n.OverlapError)) diagnostics.Add(new { code = "INVALID_NOTE", partId = Id(p), noteId = Id(n), message = n.OverlapError ? "Overlapping vocal notes." : "Note duration is below 10 ticks." });
                foreach (var ph in p.phonemes.Where(ph => ph.Error || ph.ErrorException != null)) diagnostics.Add(new { code = ph.ErrorException == null ? "MISSING_ALIAS" : "PHONEMIZER_FAILED", partId = Id(p), noteId = ph.Parent == null ? null : Id(ph.Parent), alias = ph.phoneme, message = ph.ErrorException?.Message ?? "Sample/alias is unavailable." });
            }
            return Page("get_diagnostics", args, () => diagnostics.ToArray());
        }
        object Page(string operation, JsonElement args, Func<object[]> factory, object? derived = null) {
            int limit = args.Int("limit", 200); if (limit < 1 || limit > 500) throw Arguments.Invalid("limit must be 1–500.");
            var filter = args.EnumerateObject().Where(p => p.Name is not ("cursor" or "limit" or "instanceId")).ToDictionary(p => p.Name, p => p.Value);
            string hash = operation + ":" + AutomationOperationJournal.Hash(JsonSerializer.SerializeToElement(filter));
            string id; int offset = 0; ReadSnapshot snapshot;
            if (args.TryGetProperty("cursor", out _)) {
                var tokens = args.String("cursor").Split(':');
                if (tokens.Length != 2 || !int.TryParse(tokens[1], out offset) || offset < 0 || !snapshots.TryGetValue(tokens[0], out snapshot!) || snapshot.Session != ProjectSessionId || snapshot.Expires <= DateTime.UtcNow) throw new AutomationException("SNAPSHOT_EXPIRED", "This snapshot expired or belongs to another project. Read the first page again.");
                id = tokens[0];
                if (snapshot.Filter != hash) throw Arguments.Invalid("Cursor filter does not match the original snapshot.");
            } else {
                foreach (var expired in snapshots.Where(p => p.Value.Expires <= DateTime.UtcNow).Select(p => p.Key).ToArray()) snapshots.Remove(expired);
                if (snapshots.Count >= 32) snapshots.Remove(snapshots.OrderBy(p => p.Value.Expires).First().Key);
                var values = factory().Select(item => JsonSerializer.SerializeToElement(item, JsonOptions)).ToArray();
                long bytes = values.Sum(v => (long)v.GetRawText().Length * 2);
                long current = snapshots.Values.Sum(s => s.Items.Sum(v => (long)v.GetRawText().Length * 2));
                if (bytes > 32 * 1024 * 1024) throw new AutomationException("LIMIT_EXCEEDED", "Snapshot exceeds 32 MiB. Use a narrower tick range.");
                while (current + bytes > 32 * 1024 * 1024 && snapshots.Count > 0) {
                    var key = snapshots.OrderBy(p => p.Value.Expires).First().Key; snapshots.Remove(key);
                    current = snapshots.Values.Sum(s => s.Items.Sum(v => (long)v.GetRawText().Length * 2));
                }
                id = Guid.NewGuid().ToString("N"); snapshot = new(ProjectSessionId, EditRevision, hash, values, derived, DateTime.UtcNow.AddMinutes(2)); snapshots.Add(id, snapshot);
            }
            if (offset > snapshot.Items.Length) throw Arguments.Invalid("Cursor offset exceeds the snapshot.");
            var page = snapshot.Items.Skip(offset).Take(limit).ToArray();
            return new { projectSessionId = snapshot.Session, editRevision = snapshot.Revision, snapshotId = id, items = page,
                nextCursor = offset + page.Length < snapshot.Items.Length ? id + ":" + (offset + page.Length) : null,
                total = snapshot.Items.Length, phonemes = snapshot.Derived, expiresAt = snapshot.Expires };
        }
        object Preview(JsonElement args) {
            Guard(args); var plan = new AutomationEditPlanner(doc.Project, Identities, revisions); plan.Plan(args);
            return new { status = "preview", projectSessionId = ProjectSessionId, editRevision = EditRevision,
                changes = plan.Changes.ToArray(), operations = args.GetProperty("operations").Clone(),
                trackChanges = PreviewEntityChanges(plan, plan.Before.Project.tracks.Cast<object>().ToArray(), plan.After.Project.tracks.Cast<object>().ToArray(), TrackValue, "track"),
                partChanges = PreviewEntityChanges(plan, plan.Before.Project.parts.Cast<object>().ToArray(), plan.After.Project.parts.Cast<object>().ToArray(), PartValue, "part"),
                noteChanges = plan.TouchedNotes.Where(n => plan.After.Project.parts.OfType<UVoicePart>().Any(p => p.notes.Contains(n)) || plan.Before.Project.parts.OfType<UVoicePart>().Any(p => p.notes.Any(note => plan.Before.Target(note) == plan.After.Target(n)))).Select(n => {
                    var target = plan.After.Target(n);
                    var previousPart = plan.Before.Project.parts.OfType<UVoicePart>().FirstOrDefault(p => p.notes.Any(note => plan.Before.Target(note) == target));
                    var previous = previousPart?.notes.FirstOrDefault(note => plan.Before.Target(note) == target);
                    var changedPart = plan.After.Project.parts.OfType<UVoicePart>().FirstOrDefault(p => p.notes.Contains(n));
                    string id = plan.ClientRefs.FirstOrDefault(r => r.Value == n).Key ?? Id(target);
                    var displayPart = changedPart ?? previousPart!;
                    return new { noteId = id, partId = plan.ClientRefs.FirstOrDefault(r => r.Value == changedPart).Key ?? Id(changedPart == null ? plan.Before.Target(previousPart!) : plan.After.Target(changedPart)),
                        partName = displayPart.name, trackName = changedPart == null ? plan.Before.Project.tracks[displayPart.trackNo].TrackName : plan.After.Project.tracks[displayPart.trackNo].TrackName,
                        before = previous == null ? null : SnapshotNote(previous, id), after = changedPart == null ? null : SnapshotNote(n, id) };
                }).ToArray(),
                before = new { tracks = plan.Before.Project.tracks.Count, parts = plan.Before.Project.parts.Count, notes = plan.Before.Project.parts.OfType<UVoicePart>().Sum(p => p.notes.Count) },
                after = new { tracks = plan.After.Project.tracks.Count, parts = plan.After.Project.parts.Count, notes = plan.After.Project.parts.OfType<UVoicePart>().Sum(p => p.notes.Count) },
                lyricMapping = "Whitespace tokens and explicit '+' extenders; pronunciation and syllabification depend on the selected phonemizer." };
        }
        object[] PreviewEntityChanges(AutomationEditPlanner plan, object[] before, object[] after, Func<object, object> snapshot, string kind) {
            var targets = before.Select(plan.Before.Target).Concat(after.Select(plan.After.Target)).Distinct(ReferenceEqualityComparer.Instance);
            var changes = new List<object>();
            foreach (var target in targets) {
                var prior = before.FirstOrDefault(entity => ReferenceEquals(plan.Before.Target(entity), target));
                var next = after.FirstOrDefault(entity => ReferenceEquals(plan.After.Target(entity), target));
                object? beforeValue = prior == null ? null : snapshot(prior), afterValue = next == null ? null : snapshot(next);
                if (JsonSerializer.Serialize(beforeValue, JsonOptions) == JsonSerializer.Serialize(afterValue, JsonOptions)) continue;
                string id = plan.ClientRefs.FirstOrDefault(r => ReferenceEquals(r.Value, next)).Key ?? Identities.GetId(target, kind);
                changes.Add(new { entityId = id, before = beforeValue, after = afterValue });
            }
            return changes.ToArray();
        }
        static object TrackValue(object entity) {
            var t = (UTrack)entity;
            return new { name = t.TrackName, color = t.TrackColor, singerId = t.Singer?.Id, phonemizerId = t.phonemizer ?? t.Phonemizer.GetType().FullName,
                renderer = t.RendererSettings.renderer, volumeDb = t.Volume, pan = t.Pan, mute = t.Mute, solo = t.Solo };
        }
        static object PartValue(object entity) {
            var p = (UPart)entity; var voice = p as UVoicePart;
            return new { name = p.name, positionTicks = p.position, durationTicks = p.Duration, trackIndex = p.trackNo,
                curves = voice?.curves.Select(c => new { abbr = c.abbr, points = c.xs.Zip(c.ys, (x, y) => new { positionTicks = x, value = y }).ToArray() }).ToArray(),
                maskedCurves = voice?.maskedCurves.Select(c => new { abbr = c.abbr, runs = c.runs.Select(r => new { positionTicks = r.x, values = r.ys.ToArray() }).ToArray() }).ToArray() };
        }
        object ApplyBatch(JsonElement args, CancellationToken ct) {
            string id = args.String("operationId");
            var entry = Journal.Register(id, args, args.String("projectSessionId"));
            if (entry.Status != "queued") return entry.Result ?? Journal.Get(id);
            long previousRevision = EditRevision;
            try {
                entry.Status = "validating"; Guard(args);
                if (doc.HasOpenUndoGroup) throw new AutomationException("EDITOR_BUSY", "Finish the current editor gesture and try again.");
                var plan = new AutomationEditPlanner(doc.Project, Identities, revisions); plan.Plan(args);
                ct.ThrowIfCancellationRequested(); plan.PrepareCommit();
                return CommitPlan(plan, args, entry);
            } catch (OperationCanceledException) {
                entry.Status = "cancelled_before_commit"; entry.Result = new { ok = false, operationId = id, status = entry.Status, projectSessionId = entry.Session,
                    error = new { code = "CANCELLED", message = "The request was cancelled before commit." } }; throw;
            } catch (Exception ex) {
                if (doc.HasOpenUndoGroup && entry.UndoGroup != null) {
                    try { doc.AbortUndoGroup(); } finally { revisions.RestoreFailedRevision(previousRevision); }
                }
                entry.Status = "failed";
                entry.Result = new { ok = false, operationId = id, status = entry.Status, error = new { code = ex is AutomationException error ? error.Code : "INVALID_ARGUMENT", message = ex.Message } };
                throw;
            }
        }
        object CommitPlan(AutomationEditPlanner plan, JsonElement args, AutomationOperationJournal.Entry entry) {
            string label = args.String("label", "AI edit");
            doc.StartUndoGroup(label, deferValidate: true); entry.UndoGroup = new object();
            doc.ExecuteCmd(new AutomationBatchCommand(doc.Project, plan.Before, plan.After, label, DuringCommandForTest));
            doc.EndUndoGroup(); entry.UndoGroup = doc.LatestUndoGroup; entry.Revision = EditRevision; entry.Status = "committed";
            var references = plan.ClientRefs.ToDictionary(kv => kv.Key, kv => Id(plan.After.Target(kv.Value)));
            entry.Result = new { ok = true, operationId = entry.Id, status = "committed", projectSessionId = ProjectSessionId,
                editRevision = EditRevision, clientRefs = references, changed = new { operations = plan.Changes.Count, parts = plan.After.Project.parts.Count },
                changes = plan.Changes.ToArray(), undo = new { available = true, label },
                appliedNotes = plan.TouchedNotes.Select(n => SnapshotNote((UNote)plan.After.Target(n))).ToArray(),
                appliedPartCurves = plan.After.Project.parts.OfType<UVoicePart>().Where(p => p.curves.Count > 0 || p.maskedCurves.Count > 0).Select(p => new {
                    partId = Id(plan.After.Target(p)), curves = p.curves.Select(c => new { abbr = c.abbr, points = c.xs.Zip(c.ys, (x, y) => new { positionTicks = x, value = y }).ToArray() }).ToArray(),
                    maskedCurves = p.maskedCurves.Select(c => new { abbr = c.abbr, runs = c.runs.Select(r => new { positionTicks = r.x, values = r.ys.ToArray() }).ToArray() }).ToArray(),
                }).ToArray(),
                derived = new { phonemes = ArePhonemesReady() ? "ready" : "pending", partIds = doc.Project.parts.OfType<UVoicePart>().Select(Id).ToArray() } };
            return entry.Result;
        }
        object Revert(JsonElement args) {
            var revert = Journal.Register(args.String("operationId"), args, args.String("projectSessionId"));
            if (revert.Status != "queued") return revert.Result ?? Journal.Get(revert.Id);
            try {
                revert.Status = "validating"; Guard(args);
                if (doc.HasOpenUndoGroup) throw new AutomationException("EDITOR_BUSY", "Finish the current editor gesture before reverting.");
                var entry = Journal.Find(args.String("targetOperationId")) ?? throw new AutomationException("OPERATION_STATE_UNKNOWN", "Operation was not found.");
                if (entry.Status != "committed" || entry.Session != ProjectSessionId || entry.Revision != EditRevision || !ReferenceEquals(entry.UndoGroup, doc.LatestUndoGroup)) throw new AutomationException("REVISION_CONFLICT", "Only the unchanged latest automation transaction can be reverted.");
                doc.Undo(); entry.Status = "reverted"; revert.Status = "committed"; revert.Revision = EditRevision;
                revert.Result = new { status = "reverted", operationId = revert.Id, targetOperationId = entry.Id, projectSessionId = ProjectSessionId, editRevision = EditRevision };
                return revert.Result;
            } catch (Exception ex) {
                revert.Status = "failed"; revert.Result = new { ok = false, status = "failed", operationId = revert.Id, error = new { code = ex is AutomationException ae ? ae.Code : "INVALID_ARGUMENT", message = ex.Message } }; throw;
            }
        }
        async Task<object> WaitForPhonemesAsync(JsonElement args, CancellationToken ct) {
            int timeout = args.Int("timeoutMs", 5000); if (timeout < 0 || timeout > 30000) throw Arguments.Invalid("timeoutMs must be 0–30000.");
            DateTime deadline = DateTime.UtcNow.AddMilliseconds(timeout);
            while (true) {
                var result = await dispatcher.InvokeAsync(() => {
                    Guard(args);
                    var part = FindPart(args.String("partId")) as UVoicePart ?? throw Arguments.Invalid("A vocal part is required.");
                    return (ready: revisions.IsReady(part), value: (object)new { projectSessionId = ProjectSessionId, editRevision = EditRevision, partId = Id(part), phonemes = GetPhonemeState(part) });
                }, ct).ConfigureAwait(false);
                if (result.ready || DateTime.UtcNow >= deadline) return result.value;
                await Task.Delay(50, ct).ConfigureAwait(false);
            }
        }
        public void Dispose() {
            revisions.Changed -= OnChanged; revisions.ProjectChanged -= OnProjectChanged; revisions.Dispose(); snapshots.Clear();
        }
    }
}
