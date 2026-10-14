using System;
using System.Collections.Generic;
using OpenUtau.Core.Ustx;

namespace OpenUtau.Core.Automation {
    public sealed class AutomationRevisionTracker : ICmdSubscriber, IDisposable {
        readonly DocManager doc;
        readonly Dictionary<UVoicePart, State> states = new();
        public string ProjectSessionId { get; private set; } = "project_" + Guid.NewGuid().ToString("N");
        public long EditRevision { get; private set; }
        public event Action? Changed;
        public event Action? ProjectChanged;
        sealed class State {
            internal long Requested = 1, Completed, Source;
            internal int NativeRevision = -1;
            internal long NativeRequested;
        }
        public AutomationRevisionTracker(DocManager doc) { this.doc = doc; doc.AddSubscriber(this); }
        public void OnNext(UCommand command, bool isUndo) {
            if (command is LoadProjectNotification) {
                ProjectSessionId = "project_" + Guid.NewGuid().ToString("N");
                EditRevision = 0; states.Clear(); ProjectChanged?.Invoke(); Changed?.Invoke(); return;
            }
            if (command is PhonemizedNotification phonemized) { Observe(phonemized.part); Changed?.Invoke(); return; }
            if (command is UNotification && command is not (VolumeChangeNotification or PanChangeNotification or SoloTrackNotification or OtoChangedNotification or VoiceColorRemappingNotification)) return;
            EditRevision++;
            bool invalidatesPhonemes = command is not (VolumeChangeNotification or PanChangeNotification or SoloTrackNotification) && !command.ValidateOptions.SkipPhonemizer;
            // Skip-only pitch and playback/mixer edits do not await a response that will never arrive.
            foreach (var part in doc.Project.parts) if (invalidatesPhonemes && part is UVoicePart voice) {
                if (command.ValidateOptions.Part != null && command.ValidateOptions.Part != voice) continue;
                var state = Get(voice); state.Requested++; state.Source = EditRevision; state.NativeRevision = voice.phonemesRevision;
            }
            Changed?.Invoke();
        }
        State Get(UVoicePart part) {
            if (!states.TryGetValue(part, out var state)) states.Add(part, state = new State { Source = EditRevision, NativeRequested = part.PhonemizationRequestedGeneration });
            return state;
        }
        void Observe(UVoicePart part) {
            var state = Get(part);
            if (part.PhonemizationRequestedGeneration != state.NativeRequested) {
                state.Requested += Math.Max(1, part.PhonemizationRequestedGeneration - state.NativeRequested);
                state.NativeRequested = part.PhonemizationRequestedGeneration;
                state.Source = EditRevision;
            }
            if (part.PhonemesUpToDate && (part.notes.Count == 0 || part.phonemesRevision > 0 && part.phonemesRevision != state.NativeRevision)) {
                state.Completed = state.Requested; state.NativeRevision = part.phonemesRevision;
            }
        }
        public object GetPhonemeState(UVoicePart part) {
            Observe(part); var s = Get(part);
            return new { requestedGeneration = s.Requested, completedGeneration = s.Completed,
                phonemeGeneration = s.Completed, sourceEditRevision = s.Source,
                status = s.Completed == s.Requested && part.PhonemesUpToDate ? "ready" : s.Completed > 0 ? "stale" : "pending" };
        }
        public bool IsReady(UVoicePart part) { Observe(part); var s = Get(part); return s.Completed == s.Requested && part.PhonemesUpToDate; }
        public void RequireGeneration(UVoicePart part, long generation) {
            Observe(part); var s = Get(part);
            if (!IsReady(part) || generation != s.Completed) throw new AutomationException("PHONEME_GENERATION_CONFLICT", "Read and wait for current phonemes before changing an index.", GetPhonemeState(part));
        }
        internal void RestoreFailedRevision(long revision) {
            EditRevision = revision;
            // Never reuse an earlier generation after failure. Rollback validation
            // may have legitimately requested fresh derived output.
            foreach (var state in states.Values) state.Source = Math.Min(state.Source, revision);
            Changed?.Invoke();
        }
        public void Dispose() => doc.RemoveSubscriber(this);
    }
}
