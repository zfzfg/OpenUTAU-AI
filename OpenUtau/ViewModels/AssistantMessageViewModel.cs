using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text.Json;
using ReactiveUI;

namespace OpenUtau.App.ViewModels {
    public sealed class AssistantChoiceViewModel {
        public string Id { get; }
        public string Name { get; }
        public AssistantChoiceViewModel(string id, string name) { Id = id; Name = name; }
        public override string ToString() => Name;
    }

    public sealed class AssistantMessageViewModel : ViewModelBase {
        string text = "";
        string title = "";
        string status = "";
        string answer = "";
        bool pending;
        string diagnostics = "";
        int reviewPage;
        JsonElement[] noteChanges = Array.Empty<JsonElement>();
        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        public string Kind { get; set; } = "message";
        public string Role { get; set; } = "assistant";
        public DateTimeOffset Timestamp { get; set; } = DateTimeOffset.UtcNow;
        public string Text { get => text; set => this.RaiseAndSetIfChanged(ref text, value); }
        public string Title { get => title; set => this.RaiseAndSetIfChanged(ref title, value); }
        public string Status { get => status; set { this.RaiseAndSetIfChanged(ref status, value); this.RaisePropertyChanged(nameof(DisplayStatus)); } }
        public string DisplayStatus => ThemeManager.TryGetString("assistant.tool." + Status, out var label) ? label : Status;
        public string Answer { get => answer; set => this.RaiseAndSetIfChanged(ref answer, value); }
        public bool Pending { get => pending; set => this.RaiseAndSetIfChanged(ref pending, value); }
        public bool IsQuestion => Kind == "question";
        public bool IsReview => Kind == "review";
        public bool IsAudio => Kind == "audio";
        public bool CanRevert => Kind == "tool" && Result is { } value && value.ValueKind == JsonValueKind.Object && value.TryGetProperty("operationId", out _) && value.TryGetProperty("undo", out _);
        public int ReviewPage {
            get => reviewPage;
            set { this.RaiseAndSetIfChanged(ref reviewPage, Math.Clamp(value, 0, Math.Max(0, (noteChanges.Length - 1) / 12))); RefreshReviewLines(); }
        }
        public bool HasReviewLines => noteChanges.Length > 0;
        public string ReviewPageLabel => $"{ReviewPage + 1}/{Math.Max(1, (noteChanges.Length + 11) / 12)}";
        public ObservableCollection<string> ReviewLines { get; } = new ObservableCollection<string>();
        public string Diagnostics { get => diagnostics; set { this.RaiseAndSetIfChanged(ref diagnostics, value); this.RaisePropertyChanged(nameof(HasDiagnostics)); } }
        public bool HasDiagnostics => !string.IsNullOrEmpty(Diagnostics);
        public string? StatusResourceKey { get; private set; }
        public string? OperationId { get; set; }
        public string? JobId { get; set; }
        public string? ArtifactId { get; set; }
        public void SetStatus(string resourceKey) { StatusResourceKey = resourceKey; Status = ThemeManager.GetString(resourceKey); }
        public void RefreshLabels() { if (StatusResourceKey != null) { Status = ThemeManager.GetString(StatusResourceKey); } this.RaisePropertyChanged(nameof(DisplayStatus)); RefreshReviewLines(); }
        JsonElement? result;
        public JsonElement? Result {
            get => result;
            set {
                this.RaiseAndSetIfChanged(ref result, value);
                if (value is { ValueKind: JsonValueKind.Object } document) {
                    OperationId = Reference(document, "operationId") ?? OperationId;
                    JobId = Reference(document, "jobId") ?? JobId;
                    ArtifactId = Reference(document, "artifactId") ?? ArtifactId;
                }
                this.RaisePropertyChanged(nameof(CanRevert));
            }
        }
        public JsonElement? Preview { get; set; }
        public ObservableCollection<AssistantChoiceViewModel> Options { get; } = new ObservableCollection<AssistantChoiceViewModel>();

        public void LoadReview(JsonElement preview) {
            Preview = preview.Clone();
            noteChanges = preview.TryGetProperty("noteChanges", out var changes) && changes.ValueKind == JsonValueKind.Array ? changes.EnumerateArray().Select(n => n.Clone()).ToArray() : Array.Empty<JsonElement>();
            this.RaisePropertyChanged(nameof(HasReviewLines));
            RefreshReviewLines();
        }

        void RefreshReviewLines() {
            ReviewLines.Clear();
            foreach (var change in noteChanges.Skip(ReviewPage * 12).Take(12)) {
                var location = Value(change, "trackName") + " · " + Value(change, "partName");
                var before = change.GetProperty("before");
                var after = change.GetProperty("after");
                if (before.ValueKind == JsonValueKind.Null) { ReviewLines.Add(location + "\n" + ThemeManager.GetString("assistant.review.add") + " " + Note(after)); continue; }
                if (after.ValueKind == JsonValueKind.Null) { ReviewLines.Add(location + "\n" + ThemeManager.GetString("assistant.review.remove") + " " + Note(before)); continue; }
                var edits = new System.Collections.Generic.List<string>();
                foreach (var (property, label) in new[] { ("lyric", "lyrics"), ("tone", "tone"), ("positionTicks", "position"), ("durationTicks", "duration"), ("phoneticHint", "hint"), ("tuningCents", "tuning") }) {
                    var oldValue = Value(before, property); var newValue = Value(after, property);
                    if (oldValue != newValue) { edits.Add(ThemeManager.GetString("assistant.review." + label) + ": " + oldValue + " → " + newValue); }
                }
                foreach (var property in new[] { "pitch", "vibrato", "overrides", "expressions" }) {
                    if (before.TryGetProperty(property, out var oldValue) && after.TryGetProperty(property, out var newValue) && oldValue.GetRawText() != newValue.GetRawText()) {
                        edits.Add(ThemeManager.GetString("assistant.review." + property));
                    }
                }
                ReviewLines.Add(location + "\n" + string.Join(" · ", edits));
            }
            this.RaisePropertyChanged(nameof(ReviewPageLabel));
        }
        static string Note(JsonElement value) => "“" + Value(value, "lyric") + "” · " + Value(value, "tone") + " · " + Value(value, "durationTicks") + " ticks";
        static string Value(JsonElement value, string name) {
            if (!value.TryGetProperty(name, out var property) || property.ValueKind == JsonValueKind.Null) { return "—"; }
            return property.ValueKind == JsonValueKind.String ? property.GetString() ?? "" : property.ToString();
        }
        static string? Reference(JsonElement document, string name) {
            if (document.TryGetProperty(name, out var reference) && reference.ValueKind == JsonValueKind.String) { return reference.GetString(); }
            foreach (var nestedName in new[] { "job", "artifact" }) {
                if (document.TryGetProperty(nestedName, out var nested) && nested.ValueKind == JsonValueKind.Object && nested.TryGetProperty(name, out reference) && reference.ValueKind == JsonValueKind.String) { return reference.GetString(); }
            }
            if (document.TryGetProperty("artifacts", out var artifacts) && artifacts.ValueKind == JsonValueKind.Array) {
                foreach (var artifact in artifacts.EnumerateArray()) {
                    if (artifact.ValueKind == JsonValueKind.Object && artifact.TryGetProperty(name, out reference) && reference.ValueKind == JsonValueKind.String) { return reference.GetString(); }
                }
            }
            return null;
        }
    }
}
