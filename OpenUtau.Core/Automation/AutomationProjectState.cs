using System;
using System.Collections.Generic;
using System.Linq;
using OpenUtau.Core.Ustx;

namespace OpenUtau.Core.Automation {
    /// <summary>Detached musical plan. No validation, file loading, or phonemizer is run while constructing it.</summary>
    internal sealed class AutomationProjectState {
        internal readonly UProject Project;
        internal readonly Dictionary<object, object> Targets = new(ReferenceEqualityComparer.Instance);
        internal AutomationProjectState(UProject source) {
            Project = new UProject {
                name = source.name, comment = source.comment, key = source.key, ustxVersion = source.ustxVersion,
                FilePath = source.FilePath, expressions = source.expressions.ToDictionary(kv => kv.Key, kv => kv.Value.Clone()),
                tempos = source.tempos.Select(t => new UTempo(t.position, t.bpm)).ToList(),
                timeSignatures = source.timeSignatures.Select(t => new UTimeSignature(t.barPosition, t.beatPerBar, t.beatUnit)).ToList(),
                tracks = source.tracks.Select(CloneTrack).ToList(), parts = new List<UPart>(),
            };
            for (int i = 0; i < Project.tracks.Count; i++) Targets.Add(Project.tracks[i], source.tracks[i]);
            foreach (var original in source.parts) {
                UPart copy;
                if (original is UVoicePart voice) {
                    var v = new UVoicePart {
                        name = voice.name, comment = voice.comment, trackNo = voice.trackNo,
                        position = voice.position, duration = voice.duration,
                        curves = voice.curves.Select(c => new UCurve { abbr = c.abbr, descriptor = c.descriptor, xs = c.xs.ToList(), ys = c.ys.ToList(), realXs = c.realXs.ToList(), realYs = c.realYs.ToList() }).ToList(),
                        maskedCurves = voice.maskedCurves.Select(c => c.Clone()).ToList(),
                    };
                    foreach (var n in voice.notes) {
                        var note = n.Clone(); v.notes.Add(note); Targets.Add(note, n);
                    }
                    copy = v;
                } else if (original is UWavePart wave) {
                    copy = new UWavePart { FilePath = wave.FilePath, name = wave.name, comment = wave.comment,
                        trackNo = wave.trackNo, position = wave.position, fileDurationMs = wave.fileDurationMs,
                        relativePath = wave.relativePath, skip = wave.skip, trim = wave.trim, fadein = wave.fadein, fadeout = wave.fadeout };
                } else throw new AutomationException("UNSUPPORTED_CAPABILITY", "Unknown part type.");
                Project.parts.Add(copy); Targets.Add(copy, original);
            }
            Project.timeAxis.BuildSegments(Project);
            foreach (var wave in Project.parts.OfType<UWavePart>()) wave.Validate(default, Project, Project.tracks[wave.trackNo]);
        }
        internal static UTrack CloneTrack(UTrack track) => new UTrack {
            TrackNo = track.TrackNo, TrackName = track.TrackName, TrackColor = track.TrackColor,
            Singer = track.Singer, singer = track.singer, Phonemizer = track.Phonemizer, phonemizer = track.phonemizer,
            RendererSettings = new URenderSettings { renderer = track.RendererSettings.renderer, resampler = track.RendererSettings.resampler,
                wavtool = track.RendererSettings.wavtool, Renderer = track.RendererSettings.Renderer,
                Resampler = track.RendererSettings.Resampler, Wavtool = track.RendererSettings.Wavtool },
            Volume = track.Volume, Pan = track.Pan, Mute = track.Mute, Muted = track.Muted, Solo = track.Solo,
            MixFx = track.MixFx?.Clone(), ExpressionGraph = track.ExpressionGraph,
            TrackExpressions = track.TrackExpressions.Select(e => e.Clone()).ToList(),
            VoiceColorExp = track.VoiceColorExp, VoiceColor2Exp = track.VoiceColor2Exp, VoiceColorNames = track.VoiceColorNames.ToArray(),
        };
        internal object Target(object copy) => Targets.TryGetValue(copy, out var target) ? target : copy;
        /// <summary>Applies only the fields owned by this transition; later transient edits remain intact.</summary>
        internal void RestoreDelta(UProject live, AutomationProjectState from) {
            if (Project.name != from.Project.name) live.name = Project.name;
            if (Project.comment != from.Project.comment) live.comment = Project.comment;
            if (Project.key != from.Project.key) live.key = Project.key;
            if (!Project.tempos.Select(t => (t.position, t.bpm)).SequenceEqual(from.Project.tempos.Select(t => (t.position, t.bpm)))) live.tempos = Project.tempos.Select(t => new UTempo(t.position, t.bpm)).ToList();
            if (!Project.timeSignatures.Select(t => (t.barPosition, t.beatPerBar, t.beatUnit)).SequenceEqual(from.Project.timeSignatures.Select(t => (t.barPosition, t.beatPerBar, t.beatUnit)))) live.timeSignatures = Project.timeSignatures.Select(t => new UTimeSignature(t.barPosition, t.beatPerBar, t.beatUnit)).ToList();
            var previousTracks = from.Project.tracks.ToDictionary(t => from.Target(t), ReferenceEqualityComparer.Instance);
            var nextTracks = Project.tracks.Select(Target).ToHashSet(ReferenceEqualityComparer.Instance);
            var partTracks = live.parts.Where(p => p.trackNo >= 0 && p.trackNo < live.tracks.Count).ToDictionary(p => p, p => live.tracks[p.trackNo]);
            var previousPartTargets = from.Project.parts.Select(from.Target).ToHashSet(ReferenceEqualityComparer.Instance);
            // Undoing an automated track creation must retain a track that now
            // carries unrelated transient user parts, keeping their links valid.
            live.tracks.RemoveAll(t => previousTracks.ContainsKey(t) && !nextTracks.Contains(t)
                && !partTracks.Any(pair => ReferenceEquals(pair.Value, t) && !previousPartTargets.Contains(pair.Key)));
            foreach (var t in Project.tracks) {
                var target = (UTrack)Target(t); previousTracks.TryGetValue(target, out var old);
                if (!live.tracks.Contains(target)) live.tracks.Insert(Math.Min(Project.tracks.IndexOf(t), live.tracks.Count), target);
                if (old == null || t.TrackName != old.TrackName) target.TrackName = t.TrackName;
                if (old == null || t.TrackColor != old.TrackColor) target.TrackColor = t.TrackColor;
                if (old == null || t.Volume != old.Volume) target.Volume = t.Volume;
                if (old == null || t.Pan != old.Pan) target.Pan = t.Pan;
                if (old == null || t.Mute != old.Mute) target.Mute = t.Mute;
                if (old == null || t.Solo != old.Solo) target.Solo = t.Solo;
                if (old == null || t.Singer != old.Singer) { target.Singer = t.Singer; target.singer = t.singer; }
                if (old == null || t.Phonemizer != old.Phonemizer || t.phonemizer != old.phonemizer) { target.Phonemizer = t.Phonemizer; target.phonemizer = t.phonemizer; }
                if (old == null || t.RendererSettings.renderer != old.RendererSettings.renderer || t.RendererSettings.Renderer != old.RendererSettings.Renderer) target.RendererSettings = CloneTrack(t).RendererSettings;
            }
            for (int i = 0; i < live.tracks.Count; i++) live.tracks[i].TrackNo = i;
            foreach (var pair in partTracks) if (live.tracks.Contains(pair.Value)) pair.Key.trackNo = live.tracks.IndexOf(pair.Value);
            var previousParts = from.Project.parts.ToDictionary(p => from.Target(p), ReferenceEqualityComparer.Instance);
            var nextParts = Project.parts.Select(Target).ToHashSet(ReferenceEqualityComparer.Instance);
            live.parts.RemoveAll(p => previousParts.ContainsKey(p) && !nextParts.Contains(p));
            foreach (var p in Project.parts) {
                var target = (UPart)Target(p); previousParts.TryGetValue(target, out var old);
                if (!live.parts.Contains(target)) live.parts.Add(target);
                if (old == null || p.name != old.name) target.name = p.name;
                if (old == null || p.comment != old.comment) target.comment = p.comment;
                if (old == null || p.position != old.position) target.position = p.position;
                var desiredTrack = (UTrack)Target(Project.tracks[p.trackNo]);
                var priorTrack = old == null ? null : from.Target(from.Project.tracks[old.trackNo]);
                if (old == null || !ReferenceEquals(desiredTrack, priorTrack)) target.trackNo = live.tracks.IndexOf(desiredTrack);
                if (p is UVoicePart voice && target is UVoicePart v) {
                    var oldVoice = old as UVoicePart;
                    var previousNotes = oldVoice?.notes.ToDictionary(n => from.Target(n), ReferenceEqualityComparer.Instance) ?? new Dictionary<object, UNote>(ReferenceEqualityComparer.Instance);
                    var nextNotes = voice.notes.Select(Target).ToHashSet(ReferenceEqualityComparer.Instance);
                    v.notes.RemoveWhere(n => previousNotes.ContainsKey(n) && !nextNotes.Contains(n));
                    foreach (var n in voice.notes) {
                        var note = (UNote)Target(n); previousNotes.TryGetValue(note, out var oldNote);
                        v.notes.Remove(note);
                        if (oldNote == null || n.position != oldNote.position) note.position = n.position;
                        if (oldNote == null || n.duration != oldNote.duration) note.duration = n.duration;
                        if (oldNote == null || n.tone != oldNote.tone) note.tone = n.tone;
                        if (oldNote == null || n.tuning != oldNote.tuning) note.tuning = n.tuning;
                        if (oldNote == null || n.lyric != oldNote.lyric) note.lyric = n.lyric;
                        if (oldNote == null || n.PhonemizerOverride != oldNote.PhonemizerOverride) note.PhonemizerOverride = n.PhonemizerOverride;
                        if (oldNote == null || n.pitch.snapFirst != oldNote.pitch.snapFirst || !n.pitch.data.Select(p => (p.X, p.Y, p.shape)).SequenceEqual(oldNote.pitch.data.Select(p => (p.X, p.Y, p.shape)))) note.pitch = n.pitch.Clone();
                        static object VibratoKey(UVibrato x) => (x.length, x.period, x.depth, x.@in, x.@out, x.shift, x.drift, x.volLink);
                        if (oldNote == null || !Equals(VibratoKey(n.vibrato), VibratoKey(oldNote.vibrato))) note.vibrato = n.vibrato.Clone();
                        static object OverrideKey(UPhonemeOverride o) => (o.index, o.phoneme, o.offset, o.preutterDelta, o.overlapDelta, o.attackTimeDelta, o.releaseTimeDelta);
                        if (oldNote == null || !n.phonemeOverrides.Select(OverrideKey).SequenceEqual(oldNote.phonemeOverrides.Select(OverrideKey))) note.phonemeOverrides = n.phonemeOverrides.Select(o => o.Clone()).ToList();
                        if (oldNote == null || !n.phonemeExpressions.Select(e => (e.abbr, e.index, e.value)).SequenceEqual(oldNote.phonemeExpressions.Select(e => (e.abbr, e.index, e.value)))) note.phonemeExpressions = n.phonemeExpressions.Select(e => e.Clone()).ToList();
                        v.notes.Add(note);
                    }
                    if (oldVoice == null || voice.duration != oldVoice.duration && v.duration == oldVoice.duration) v.duration = Math.Max(voice.duration, v.notes.Count == 0 ? 0 : v.notes.Max(n => n.End));
                    static string CurveKey(UCurve c) => c.abbr + ":" + string.Join(",", c.xs) + ":" + string.Join(",", c.ys);
                    if (oldVoice == null || !voice.curves.Select(CurveKey).SequenceEqual(oldVoice.curves.Select(CurveKey))) v.curves = voice.curves.Select(c => new UCurve { abbr = c.abbr, descriptor = c.descriptor, xs = c.xs.ToList(), ys = c.ys.ToList(), realXs = c.realXs.ToList(), realYs = c.realYs.ToList() }).ToList();
                    static string MaskedKey(UMaskedCurve c) => c.abbr + ":" + string.Join(";", c.runs.Select(r => r.x + ":" + string.Join(",", r.ys)));
                    if (oldVoice == null || !voice.maskedCurves.Select(MaskedKey).SequenceEqual(oldVoice.maskedCurves.Select(MaskedKey))) v.maskedCurves = voice.maskedCurves.Select(c => c.Clone()).ToList();
                } else if (p is UWavePart wave && target is UWavePart w) {
                    var oldWave = old as UWavePart;
                    if (oldWave == null || wave.skip != oldWave.skip) w.skip = wave.skip;
                    if (oldWave == null || wave.trim != oldWave.trim) w.trim = wave.trim;
                    if (oldWave == null || wave.fadein != oldWave.fadein) w.fadein = wave.fadein;
                    if (oldWave == null || wave.fadeout != oldWave.fadeout) w.fadeout = wave.fadeout;
                }
            }
            live.timeAxis.BuildSegments(live);
        }
    }

    /// <summary>One exception-safe, undoable editor command for a validated musical batch.</summary>
    public sealed class AutomationBatchCommand : UCommand {
        readonly UProject project;
        public UProject Project => project;
        readonly AutomationProjectState before, after;
        readonly string label;
        readonly Action? mutationProbe;
        internal AutomationBatchCommand(UProject project, AutomationProjectState before, AutomationProjectState after, string label, Action? mutationProbe = null) {
            this.project = project; this.before = before; this.after = after; this.label = label; this.mutationProbe = mutationProbe;
        }
        public override void Execute() {
            try { after.RestoreDelta(project, before); mutationProbe?.Invoke(); } catch { before.RestoreDelta(project, after); throw; }
        }
        public override void Unexecute() => before.RestoreDelta(project, after);
        public override string ToString() => label;
    }
}
