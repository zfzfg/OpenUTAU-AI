using System;
using System.Collections.Generic;
using System.Linq;
using OpenUtau.App.ViewModels;
using OpenUtau.Core;
using OpenUtau.Core.Automation;
using OpenUtau.Core.Ustx;
using ReactiveUI;
using ReactiveUI.Primitives;

namespace OpenUtau.Automation {
    public sealed class AutomationContextService : IDisposable {
        readonly DocManager doc;
        readonly EditorAutomationService editor;
        readonly List<IDisposable> subscriptions = new();
        UTrack[] tracks = Array.Empty<UTrack>();
        UPart[] parts = Array.Empty<UPart>();
        UNote[] notes = Array.Empty<UNote>();
        UPart? openPart;
        public AutomationContextService(DocManager doc, EditorAutomationService editor) {
            this.doc = doc; this.editor = editor;
            subscriptions.Add(SubscribeExtensions.Subscribe(MessageBus.Current.Listen<TrackSelectionEvent>(), e => tracks = e.selectedTracks));
            subscriptions.Add(SubscribeExtensions.Subscribe(MessageBus.Current.Listen<PartsSelectionEvent>(), e => parts = e.selectedParts));
            subscriptions.Add(SubscribeExtensions.Subscribe(MessageBus.Current.Listen<NotesSelectionEvent>(), e => notes = e.selectedNotes));
            subscriptions.Add(SubscribeExtensions.Subscribe(MessageBus.Current.Listen<PianoRollOpenPartChangedEvent>(), e => { openPart = e.Part; notes = Array.Empty<UNote>(); }));
        }
        public object GetSnapshot() {
            var project = doc.Project;
            var currentPart = openPart != null && project.parts.Contains(openPart) ? openPart : null;
            return new {
                projectSessionId = editor.ProjectSessionId, editRevision = editor.EditRevision, projectName = project.name,
                trackIds = tracks.Where(project.tracks.Contains).Select(editor.Identities.GetId).ToArray(),
                partIds = parts.Where(project.parts.Contains).Select(editor.Identities.GetId).ToArray(),
                activePartId = currentPart == null ? null : editor.Identities.GetId(currentPart),
                activeTrackId = currentPart == null ? null : editor.Identities.GetId(project.tracks[currentPart.trackNo]),
                noteIds = currentPart is UVoicePart voice ? notes.Where(voice.notes.Contains).Select(editor.Identities.GetId).ToArray() : Array.Empty<string>(),
                hasSelection = tracks.Any(project.tracks.Contains) || parts.Any(project.parts.Contains) || (currentPart is UVoicePart p && notes.Any(p.notes.Contains))
            };
        }
        public void Dispose() { foreach (var subscription in subscriptions) subscription.Dispose(); subscriptions.Clear(); }
    }
}
