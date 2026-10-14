using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using OpenUtau.Api;
using OpenUtau.Core.Ustx;

namespace OpenUtau.Core.Automation {
    internal sealed class AutomationEditPlanner {
        readonly AutomationIdentityRegistry identities;
        readonly AutomationRevisionTracker revisions;
        readonly UProject live;
        readonly Dictionary<string, object> knownEntities = new(StringComparer.Ordinal);
        internal readonly AutomationProjectState Before, After;
        internal readonly Dictionary<string, object> ClientRefs = new(StringComparer.Ordinal);
        internal readonly List<object> Changes = new();
        internal readonly HashSet<UNote> TouchedNotes = new();
        int itemCount;
        internal AutomationEditPlanner(UProject project, AutomationIdentityRegistry identities, AutomationRevisionTracker revisions) {
            live = project; this.identities = identities; this.revisions = revisions;
            Before = new(project); After = new(project);
            foreach (var t in After.Project.tracks) knownEntities.Add(Id(t, "track"), t);
            foreach (var p in After.Project.parts) {
                knownEntities.Add(Id(p, "part"), p);
                if (p is UVoicePart voice) foreach (var n in voice.notes) knownEntities.Add(Id(n, "note"), n);
            }
        }
        string Id(object copy, string kind) => identities.GetId(After.Target(copy), kind);
        T Find<T>(IEnumerable<T> objects, string id, string kind) where T : class {
            if (ClientRefs.TryGetValue(id.TrimStart('@'), out var target) && target is T found && objects.Contains(found)) return found;
            if (knownEntities.TryGetValue(id, out var entity) && entity is T existing && objects.Contains(existing)) return existing;
            throw new AutomationException("ENTITY_NOT_FOUND", $"Unknown {kind}: {id}.");
        }
        UTrack Track(string id) => Find(After.Project.tracks, id, "track");
        UPart Part(string id) => Find(After.Project.parts, id, "part");
        UVoicePart Voice(JsonElement op) => Part(op.String("partId")) as UVoicePart ?? throw Arguments.Invalid("A vocal part is required.");
        UNote Note(UVoicePart part, JsonElement item) {
            var note = Find(part.notes, item.String("noteId"), "note"); TouchedNotes.Add(note); return note;
        }
        void Ref(JsonElement input, object plan, object target) {
            After.Targets.Add(plan, target);
            if (input.TryGetProperty("clientRef", out _)) {
                var reference = input.String("clientRef");
                if (string.IsNullOrWhiteSpace(reference) || reference.Length > 128 || reference.StartsWith('@') || !ClientRefs.TryAdd(reference, plan)) throw Arguments.Invalid("clientRef must be unique, nonempty, and at most 128 characters.");
            }
        }
        internal void Plan(JsonElement arguments) {
            var operations = arguments.Array("operations");
            if (operations.Length == 0 || operations.Length > 128) throw new AutomationException("LIMIT_EXCEEDED", "A batch needs 1–128 operations.");
            foreach (var op in operations) {
                string type = op.String("type");
                Apply(op, type);
                Changes.Add(new { type, partId = op.TryGetProperty("partId", out var p) ? p.GetString() : null,
                    trackId = op.TryGetProperty("trackId", out var t) ? t.GetString() : null,
                    affectedItems = op.TryGetProperty("items", out var items) ? items.GetArrayLength() : op.TryGetProperty("notes", out var notes) ? notes.GetArrayLength() : 1 });
            }
            foreach (var p in After.Project.parts.OfType<UVoicePart>()) {
                if (p.notes.Count > 0) p.duration = Math.Max(p.duration, p.notes.Max(n => n.End));
                checked { _ = p.position + p.duration; }
            }
            foreach (var wave in After.Project.parts.OfType<UWavePart>()) wave.Validate(default, After.Project, After.Project.tracks[wave.trackNo]);
        }
        void Items(JsonElement op, Action<JsonElement> action, string name = "items") {
            foreach (var item in op.Array(name)) {
                if (++itemCount > 5000) throw new AutomationException("LIMIT_EXCEEDED", "A batch contains at most 5000 note/expression items.");
                action(item);
            }
        }
        void Apply(JsonElement op, string type) {
            var project = After.Project;
            switch (type) {
                case "set_project_info":
                    if (op.TryGetProperty("name", out _)) project.name = Text(op.String("name"));
                    if (op.TryGetProperty("comment", out _)) project.comment = Text(op.String("comment"));
                    if (op.TryGetProperty("key", out _)) project.key = (int)Arguments.Range(op.Int("key"), 0, 11, "key");
                    break;
                case "set_tempo_map":
                    project.tempos = op.Array("tempos").Select(t => new UTempo(Arguments.Nonnegative(t.Int("positionTicks"), "positionTicks"), Arguments.Range(t.Number("bpm"), 10, 1000, "bpm"))).OrderBy(t => t.position).ToList();
                    if (project.tempos.Count == 0 || project.tempos[0].position != 0 || project.tempos.Select(t => t.position).Distinct().Count() != project.tempos.Count) throw Arguments.Invalid("Tempo map must start at zero and have unique positions.");
                    project.timeAxis.BuildSegments(project); break;
                case "set_time_signature_map":
                    project.timeSignatures = op.Array("timeSignatures").Select(t => new UTimeSignature(Arguments.Nonnegative(t.Int("barPosition"), "barPosition"), (int)Arguments.Range(t.Int("numerator"), 1, 32, "numerator"), t.Int("denominator"))).OrderBy(t => t.barPosition).ToList();
                    if (project.timeSignatures.Count == 0 || project.timeSignatures[0].barPosition != 0 || project.timeSignatures.Select(t => t.barPosition).Distinct().Count() != project.timeSignatures.Count || project.timeSignatures.Any(t => !new[] { 1, 2, 4, 8, 16, 32 }.Contains(t.beatUnit))) throw Arguments.Invalid("Invalid time signature map.");
                    project.timeAxis.BuildSegments(project); break;
                case "add_track": {
                    if (project.tracks.Count >= 256) throw new AutomationException("LIMIT_EXCEEDED", "At most 256 tracks are supported.");
                    var t = new UTrack { TrackNo = project.tracks.Count }; UpdateTrack(t, op);
                    project.tracks.Add(t); Ref(op, t, AutomationProjectState.CloneTrack(t)); break;
                }
                case "update_track": UpdateTrack(Track(op.String("trackId")), op); break;
                case "remove_track": {
                    var t = Track(op.String("trackId")); int index = project.tracks.IndexOf(t);
                    foreach (var n in project.parts.OfType<UVoicePart>().Where(p => p.trackNo == index).SelectMany(p => p.notes)) TouchedNotes.Add(n);
                    project.parts.RemoveAll(p => p.trackNo == index); project.tracks.Remove(t);
                    foreach (var p in project.parts) if (p.trackNo > index) p.trackNo--;
                    for (int i = 0; i < project.tracks.Count; i++) project.tracks[i].TrackNo = i;
                    break;
                }
                case "add_part": {
                    if (op.String("kind", "voice") != "voice") throw new AutomationException("UNSUPPORTED_CAPABILITY", "Use import_audio to add a wave part.");
                    var t = Track(op.String("trackId"));
                    var p = new UVoicePart { trackNo = project.tracks.IndexOf(t), name = Text(op.String("name", "New Part")),
                        position = Arguments.Nonnegative(op.Int("positionTicks", 0), "positionTicks"),
                        duration = (int)Arguments.Range(op.Int("durationTicks", project.resolution * 4), 1, int.MaxValue, "durationTicks") };
                    project.parts.Add(p); Ref(op, p, new UVoicePart()); break;
                }
                case "update_part": {
                    var p = Part(op.String("partId"));
                    if (op.TryGetProperty("name", out _)) p.name = Text(op.String("name"));
                    if (op.TryGetProperty("positionTicks", out _)) p.position = Arguments.Nonnegative(op.Int("positionTicks"), "positionTicks");
                    if (op.TryGetProperty("trackId", out _)) p.trackNo = project.tracks.IndexOf(Track(op.String("trackId")));
                    if (op.TryGetProperty("durationTicks", out _)) {
                        if (p is not UVoicePart) throw new AutomationException("UNSUPPORTED_CAPABILITY", "Wave length is controlled by audio and trim.");
                        p.Duration = (int)Arguments.Range(op.Int("durationTicks"), 1, int.MaxValue, "durationTicks");
                    }
                    break;
                }
                case "remove_part": {
                    var part = Part(op.String("partId"));
                    if (part is UVoicePart voice) foreach (var n in voice.notes) TouchedNotes.Add(n);
                    project.parts.Remove(part); break;
                }
                case "add_notes": {
                    var p = Voice(op);
                    Items(op, item => {
                        var note = project.CreateNote(); UpdateNote(note, item, true);
                        p.notes.Add(note); Ref(item, note, project.CreateNote()); TouchedNotes.Add(note);
                    }, "notes"); break;
                }
                case "update_notes": {
                    var p = Voice(op);
                    Items(op, item => { var note = Note(p, item); p.notes.Remove(note); UpdateNote(note, item, false); p.notes.Add(note); }); break;
                }
                case "remove_notes": {
                    var p = Voice(op);
                    foreach (var id in op.Array("noteIds")) { var n = Find(p.notes, id.GetString() ?? "", "note"); TouchedNotes.Add(n); p.notes.Remove(n); }
                    break;
                }
                case "set_lyrics": {
                    var p = Voice(op);
                    if (op.TryGetProperty("text", out _)) {
                        var ids = op.Array("noteIds"); var lyrics = LyricMapping.Split(op.String("text"));
                        if (ids.Length != lyrics.Length) throw Arguments.Invalid("Lyric token count must match noteIds. Use explicit '+' tokens for melismas.");
                        for (int i = 0; i < ids.Length; i++) { var n = Find(p.notes, ids[i].GetString() ?? "", "note"); TouchedNotes.Add(n); n.lyric = Text(lyrics[i]); }
                    } else Items(op, item => Note(p, item).lyric = Text(item.String("lyric")));
                    break;
                }
                case "set_phonetic_hints": {
                    var p = Voice(op);
                    Items(op, item => {
                        var n = Note(p, item); var alphabet = item.String("alphabet", "phonemizer");
                        if (alphabet != "phonemizer") throw new AutomationException("UNSUPPORTED_CAPABILITY", "Hints must use the selected phonemizer's own alphabet; universal IPA/Arpabet conversion is unavailable.");
                        string hint = Text(item.String("phoneticHint"));
                        if (hint.Contains('[') || hint.Contains(']')) throw Arguments.Invalid("Hint cannot contain brackets.");
                        string lyric = item.String("lyric", LyricMapping.VisibleLyric(n.lyric));
                        n.lyric = Text(lyric) + (hint.Length == 0 ? "" : " [" + hint + "]");
                    }); break;
                }
                case "set_phoneme_overrides": {
                    var p = Voice(op); RequirePhonemes(p, op);
                    Items(op, item => {
                        var n = Note(p, item); int index = Arguments.Nonnegative(item.Int("phonemeIndex"), "phonemeIndex");
                        var original = (UNote)After.Target(n);
                        if (!original.phonemeIndexes.Contains(index)) throw Arguments.Invalid("Phoneme index does not belong to this note.");
                        var originalPart = (UVoicePart)After.Target(p);
                        var phoneme = originalPart.phonemes.FirstOrDefault(ph => ph.Parent == original && ph.index == index);
                        var o = n.GetPhonemeOverride(index);
                        if (item.TryGetProperty("alias", out _)) {
                            string alias = Text(item.String("alias")); var singer = project.tracks[p.trackNo].Singer;
                            if (singer == null || !singer.Found) throw new AutomationException("MISSING_SINGER", "Select an installed singer first.");
                            if (singer.SingerType == USingerType.Classic && !singer.TryGetOto(alias, out _) && !singer.TryGetMappedOto(alias, n.tone, "", out _)) throw new AutomationException("MISSING_ALIAS", "Alias is absent from the singer's mapping: " + alias);
                            o.phoneme = alias;
                        }
                        if (item.TryGetProperty("offsetTicks", out _)) o.offset = (int)Arguments.Range(item.Int("offsetTicks"), -1000000, 1000000, "offsetTicks");
                        if (new[] { "preutterDeltaMs", "overlapDeltaMs", "attackTimeDeltaMs", "releaseTimeDeltaMs" }.Any(key => item.TryGetProperty(key, out _)) && phoneme == null) throw new AutomationException("PHONEME_GENERATION_CONFLICT", "Timing needs an existing generated phoneme.");
                        if (phoneme != null) {
                            double preutterMax = phoneme.maxFilePreutter - phoneme.autoPreutter;
                            if (phoneme.Prev != null) preutterMax = Math.Min(preutterMax, phoneme.adjacent
                                ? phoneme.Prev.preutter < 5 ? phoneme.Prev.DurationMs + phoneme.Prev.preutter - 5 - phoneme.autoPreutter : phoneme.Prev.DurationMs - phoneme.autoPreutter
                                : phoneme.PositionMs - phoneme.Prev.EndMs - phoneme.autoPreutter);
                            SetClampedDelta(item, "preutterDeltaMs", -phoneme.autoPreutter, Math.Max(0, preutterMax), value => o.preutterDelta = value);
                            double overlap = phoneme.preutter - phoneme.autoOverlap;
                            SetClampedDelta(item, "overlapDeltaMs", -phoneme.Prev?.DurationMs + 5 + overlap ?? 0, phoneme.envelope.data[3].X + overlap, value => o.overlapDelta = value);
                            SetClampedDelta(item, "attackTimeDeltaMs", -phoneme.GetFadeIn() + 5, phoneme.autoPreutter - phoneme.GetFadeIn() + phoneme.envelope.data[3].X, value => o.attackTimeDelta = value);
                            SetClampedDelta(item, "releaseTimeDeltaMs", -phoneme.GetFadeOut() + 5, phoneme.envelope.data[4].X - phoneme.GetFadeOut() - phoneme.envelope.data[2].X, value => o.releaseTimeDelta = value);
                        }
                    }); break;
                }
                case "clear_phoneme_overrides": {
                    var p = Voice(op); RequirePhonemes(p, op);
                    foreach (var id in op.Array("noteIds")) { var n = Find(p.notes, id.GetString() ?? "", "note"); TouchedNotes.Add(n); n.phonemeOverrides.Clear(); }
                    break;
                }
                case "set_pitch": {
                    var p = Voice(op);
                    Items(op, item => {
                        var n = Note(p, item); var points = item.Array("points");
                        if (points.Length < 2 || points.Length > 256) throw Arguments.Invalid("Pitch needs 2–256 points.");
                        var pitch = new UPitch { snapFirst = item.Bool("snapFirst", false) };
                        foreach (var point in points) {
                            if (!Enum.TryParse<PitchPointShape>(point.String("shape", "io"), true, out var shape)) throw Arguments.Invalid("Unknown pitch point shape.");
                            pitch.AddPoint(new PitchPoint((float)Arguments.Range(point.Number("xMs"), -10000, 600000, "xMs"), (float)Arguments.Range(point.Number("yCents"), -12000, 12000, "yCents") / 10f, shape));
                        }
                        if (pitch.data.Select(x => x.X).Distinct().Count() != points.Length) throw Arguments.Invalid("Pitch point times must be unique.");
                        n.pitch = pitch;
                    }); break;
                }
                case "set_vibrato": {
                    var p = Voice(op);
                    Items(op, item => {
                        var v = Note(p, item).vibrato;
                        SetFloat(item, "lengthPercent", 0, 100, x => v.length = x); SetFloat(item, "periodMs", 5, 500, x => v.period = x);
                        SetFloat(item, "depthCents", 5, 200, x => v.depth = x); SetFloat(item, "fadeInPercent", 0, 100, x => v.@in = x);
                        SetFloat(item, "fadeOutPercent", 0, 100, x => v.@out = x); SetFloat(item, "shiftPercent", 0, 100, x => v.shift = x);
                        SetFloat(item, "driftPercent", -100, 100, x => v.drift = x); SetFloat(item, "volumeLinkPercent", -100, 100, x => v.volLink = x);
                    }); break;
                }
                case "set_expressions": {
                    var p = Voice(op); var t = project.tracks[p.trackNo];
                    Items(op, item => {
                        string abbr = item.String("abbr");
                        if (!t.TryGetExpDescriptor(project, abbr, out var descriptor) || t.RendererSettings.Renderer == null || !t.RendererSettings.Renderer.SupportsExpression(descriptor)) throw new AutomationException("UNSUPPORTED_CAPABILITY", "Expression is unsupported by this track's renderer: " + abbr);
                        if (descriptor.type == UExpressionType.Curve) {
                            var curve = new UCurve(descriptor);
                            foreach (var point in item.Array("points")) {
                                curve.xs.Add(Arguments.Nonnegative(point.Int("positionTicks"), "positionTicks"));
                                curve.ys.Add((int)Arguments.Range(point.Number("value"), descriptor.min, descriptor.max, "value"));
                            }
                            if (!curve.xs.SequenceEqual(curve.xs.OrderBy(x => x)) || curve.xs.Distinct().Count() != curve.xs.Count) throw Arguments.Invalid("Curve positions must increase.");
                            p.curves.RemoveAll(c => c.abbr == abbr); p.curves.Add(curve);
                        } else if (descriptor.type == UExpressionType.MaskedCurve) {
                            var values = item.Array("points").Select(point => (
                                x: (int)Arguments.Range(point.Int("positionTicks"), 0, int.MaxValue - UMaskedCurve.interval, "positionTicks"),
                                y: (float)Arguments.Range(point.Number("value"), descriptor.min, descriptor.max, "value"))).ToArray();
                            if (!values.Select(p => p.x).SequenceEqual(values.Select(p => p.x).OrderBy(x => x))) throw Arguments.Invalid("Masked curve positions must increase.");
                            var curve = new UMaskedCurve(abbr); curve.SetValues(values);
                            p.maskedCurves.RemoveAll(c => c.abbr == abbr); p.maskedCurves.Add(curve);
                        } else {
                            var n = Note(p, item); int index = Arguments.Nonnegative(item.Int("phonemeIndex", 0), "phonemeIndex");
                            float value = (float)Arguments.Range(item.Number("value"), descriptor.min, descriptor.max, "value");
                            n.phonemeExpressions.RemoveAll(e => e.abbr == abbr && e.index == index);
                            n.phonemeExpressions.Add(new UExpression(descriptor) { index = index, value = value });
                        }
                    }); break;
                }
                default: throw new AutomationException("UNSUPPORTED_CAPABILITY", "Unknown edit operation: " + type);
            }
        }
        void RequirePhonemes(UVoicePart copy, JsonElement op) {
            if (After.Target(copy) is not UVoicePart original || !live.parts.Contains(original)) throw new AutomationException("PHONEME_GENERATION_CONFLICT", "New parts must be committed and phonemized before applying overrides.");
            revisions.RequireGeneration(original, op.Long("phonemeGeneration"));
            // A preceding lyric/timing/track operation in this same batch changes the basis of phoneme indices.
            var previous = Before.Project.parts.OfType<UVoicePart>().FirstOrDefault(p => Before.Target(p) == original);
            if (previous == null || previous.position != copy.position || previous.trackNo != copy.trackNo || !previous.notes.Select(n => (n.position, n.duration, n.tone, n.lyric)).SequenceEqual(copy.notes.Select(n => (n.position, n.duration, n.tone, n.lyric)))) throw new AutomationException("PHONEME_GENERATION_CONFLICT", "Apply lyric and timing edits first, then wait for new phonemes.");
            var oldTrack = Before.Project.tracks[previous.trackNo]; var newTrack = After.Project.tracks[copy.trackNo];
            if (oldTrack.Singer != newTrack.Singer || oldTrack.phonemizer != newTrack.phonemizer || !Before.Project.tempos.Select(t => (t.position, t.bpm)).SequenceEqual(After.Project.tempos.Select(t => (t.position, t.bpm))) || !Before.Project.timeSignatures.Select(t => (t.barPosition, t.beatPerBar, t.beatUnit)).SequenceEqual(After.Project.timeSignatures.Select(t => (t.barPosition, t.beatPerBar, t.beatUnit)))) throw new AutomationException("PHONEME_GENERATION_CONFLICT", "Commit singer, phonemizer, tempo and meter edits before applying phoneme overrides.");
        }
        void UpdateTrack(UTrack track, JsonElement input) {
            if (input.TryGetProperty("name", out _)) track.TrackName = Text(input.String("name"));
            if (input.TryGetProperty("color", out _)) track.TrackColor = Text(input.String("color"));
            if (input.TryGetProperty("volumeDb", out _)) track.Volume = Arguments.Range(input.Number("volumeDb"), -60, 24, "volumeDb");
            if (input.TryGetProperty("pan", out _)) track.Pan = Arguments.Range(input.Number("pan"), -1, 1, "pan");
            if (input.TryGetProperty("mute", out _)) track.Mute = input.Bool("mute");
            if (input.TryGetProperty("solo", out _)) track.Solo = input.Bool("solo");
            if (input.TryGetProperty("singerId", out _)) {
                var id = input.String("singerId");
                if (!SingerManager.Inst.Singers.TryGetValue(id, out var singer) || !singer.Found) throw new AutomationException("MISSING_SINGER", "Singer is not installed: " + id);
                track.Singer = singer; track.singer = id;
            }
            if (input.TryGetProperty("phonemizerId", out _)) {
                string id = input.String("phonemizerId");
                var factory = PhonemizerFactory.Get(id) ?? PhonemizerFactory.GetAll().FirstOrDefault(f => f.tag == id);
                if (factory == null) throw new AutomationException("UNSUPPORTED_CAPABILITY", "Unknown phonemizer: " + id);
                track.phonemizer = factory.type.FullName!;
                // Instance construction is deferred to commit, preserving inert previews.
            }
        }
        static void UpdateNote(UNote note, JsonElement input, bool required) {
            if (required || input.TryGetProperty("positionTicks", out _)) note.position = Arguments.Nonnegative(input.Int("positionTicks"), "positionTicks");
            if (required || input.TryGetProperty("durationTicks", out _)) note.duration = (int)Arguments.Range(input.Int("durationTicks"), 10, int.MaxValue, "durationTicks");
            if (required || input.TryGetProperty("tone", out _)) note.tone = (int)Arguments.Range(input.Int("tone"), 0, 127, "tone");
            if (input.TryGetProperty("tuningCents", out _)) note.tuning = (int)Arguments.Range(input.Int("tuningCents"), -1200, 1200, "tuningCents");
            if (input.TryGetProperty("lyric", out _)) note.lyric = Text(input.String("lyric"));
            checked { _ = note.position + note.duration; }
        }
        static string Text(string text) => text.Length <= 8192 ? text : throw new AutomationException("LIMIT_EXCEEDED", "Text field is limited to 8192 characters.");
        static void SetFloat(JsonElement item, string key, double min, double max, Action<float> setter) { if (item.TryGetProperty(key, out _)) setter((float)Arguments.Range(item.Number(key), min, max, key)); }
        static void SetClampedDelta(JsonElement item, string key, double min, double max, Action<float> setter) {
            if (item.TryGetProperty(key, out _)) setter((float)Math.Max(Math.Min(Arguments.Range(item.Number(key), -10000, 10000, key), max), min));
        }
        internal void PrepareCommit() {
            foreach (var track in After.Project.tracks) if (!string.IsNullOrEmpty(track.phonemizer) && track.phonemizer != track.Phonemizer.GetType().FullName) track.Phonemizer = PhonemizerFactory.Get(track.phonemizer)!.Create();
        }
    }

    public static class LyricMapping {
        public static string[] Split(string text) => text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        public static string VisibleLyric(string lyric) => System.Text.RegularExpressions.Regex.Replace(lyric, @"\[[^\]]*\]", "").Trim();
        public static string? Hint(string lyric) {
            var match = System.Text.RegularExpressions.Regex.Match(lyric, @"\[([^\]]*)\]");
            return match.Success ? match.Groups[1].Value : null;
        }
    }
}
