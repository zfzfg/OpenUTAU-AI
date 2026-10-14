using System;
using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Avalonia.Threading;
using OpenUtau.App.Assistant;
using ReactiveUI;
using ReactiveUI.Primitives;

namespace OpenUtau.App.ViewModels {
    public sealed class AssistantSidebarViewModel : ViewModelBase, IAsyncDisposable {
        readonly AssistantHistoryStore historyStore;
        readonly ConcurrentQueue<(string Method, JsonElement Arguments)> events = new ConcurrentQueue<(string, JsonElement)>();
        readonly DispatcherTimer streamTimer;
        AssistantSessionService? session;
        Func<object>? getContext;
        Func<string, Task<object>>? applyProposal;
        Action<string>? rejectProposal;
        bool disposed;
        string draft = "";
        string status = "";
        string contextSummary = "";
        string state = "disconnected";
        bool settingsOpen;
        bool configuring;
        bool includeContext = true;
        bool automationEnabled = true;
        string allowedDirectoriesText = "";
        AssistantChoiceViewModel? externalMode;
        Func<bool, Task>? setAutomationEnabled;
        Action<string>? setExternalMode;
        Func<string[]>? getDirectories;
        AssistantChoiceViewModel? provider;
        AssistantChoiceViewModel? model;
        AssistantChoiceViewModel? effort;
        AssistantChoiceViewModel? mode;
        AssistantChoiceViewModel? contextScope;
        AssistantMessageViewModel? streamingMessage;
        System.Threading.CancellationTokenSource? turnCancellation;
        AssistantHistory? restored;
        public AssistantPreferences Preferences { get; }
        public ObservableCollection<AssistantMessageViewModel> Messages { get; } = new ObservableCollection<AssistantMessageViewModel>();
        public ObservableCollection<AssistantChoiceViewModel> Providers { get; } = new ObservableCollection<AssistantChoiceViewModel>();
        public ObservableCollection<AssistantChoiceViewModel> Models { get; } = new ObservableCollection<AssistantChoiceViewModel>();
        public ObservableCollection<AssistantChoiceViewModel> Efforts { get; } = new ObservableCollection<AssistantChoiceViewModel>();
        public ObservableCollection<AssistantChoiceViewModel> Modes { get; } = new ObservableCollection<AssistantChoiceViewModel>();
        public ObservableCollection<AssistantChoiceViewModel> ContextScopes { get; } = new ObservableCollection<AssistantChoiceViewModel>();
        public string Draft { get => draft; set => this.RaiseAndSetIfChanged(ref draft, value); }
        public string Status { get => status; private set => this.RaiseAndSetIfChanged(ref status, value); }
        public string ContextSummary { get => contextSummary; private set => this.RaiseAndSetIfChanged(ref contextSummary, value); }
        public string State { get => state; private set { this.RaiseAndSetIfChanged(ref state, value); this.RaisePropertyChanged(nameof(IsBusy)); this.RaisePropertyChanged(nameof(CanConfigure)); this.RaisePropertyChanged(nameof(IsConnected)); } }
        public bool IsBusy => State is "working" or "waiting_user" or "cancelling" or "starting";
        public bool CanConfigure => !IsBusy;
        public bool IsConnected => session?.SessionId != null;
        public bool IsEmpty => Messages.Count == 0;
        public bool SettingsOpen { get => settingsOpen; set => this.RaiseAndSetIfChanged(ref settingsOpen, value); }
        public bool IncludeContext { get => includeContext; set => this.RaiseAndSetIfChanged(ref includeContext, value); }
        public bool AutomationEnabled { get => automationEnabled; set { var old = automationEnabled; this.RaiseAndSetIfChanged(ref automationEnabled, value); if (!configuring && old != value) { _ = UpdateAutomationEnabledAsync(value); } } }
        public string AllowedDirectoriesText { get => allowedDirectoriesText; private set => this.RaiseAndSetIfChanged(ref allowedDirectoriesText, value); }
        public int HistoryLimit { get => Preferences.HistoryLimit; set { Preferences.HistoryLimit = Math.Clamp(value, 100, 5000); this.RaisePropertyChanged(); } }
        public AssistantChoiceViewModel? ExternalMode { get => externalMode; set { var old = externalMode; this.RaiseAndSetIfChanged(ref externalMode, value); if (!configuring && old != value && value != null) { _ = UpdateExternalModeAsync(value.Id); } } }
        public string NodePath { get => Preferences.NodePath; set { Preferences.NodePath = value; this.RaisePropertyChanged(); } }
        public string HostPath { get => Preferences.HostPath; set { Preferences.HostPath = value; this.RaisePropertyChanged(); } }
        public string CliPath { get => Preferences.CliPath; set { Preferences.CliPath = value; this.RaisePropertyChanged(); } }
        public string AdapterPath { get => Preferences.AdapterPath; set { Preferences.AdapterPath = value; this.RaisePropertyChanged(); } }
        public AssistantChoiceViewModel? Provider {
            get => provider;
            set {
                if (this.RaiseAndSetIfChanged(ref provider, value) == null || configuring) { return; }
                Preferences.Provider = value!.Id;
                _ = DisconnectAsync();
            }
        }
        public AssistantChoiceViewModel? Model { get => model; set { if (configuring) { this.RaiseAndSetIfChanged(ref model, value); } else if (model != value && value != null) { _ = SetConfigurationAsync("session.setModel", value); this.RaisePropertyChanged(); } } }
        public AssistantChoiceViewModel? Effort { get => effort; set { if (configuring) { this.RaiseAndSetIfChanged(ref effort, value); } else if (effort != value && value != null) { _ = SetConfigurationAsync("session.setEffort", value); this.RaisePropertyChanged(); } } }
        public AssistantChoiceViewModel? Mode {
            get => mode;
            set {
                var old = mode;
                this.RaiseAndSetIfChanged(ref mode, value);
                if (!configuring && old != value && value != null) {
                    Preferences.Mode = value.Id;
                    _ = DisconnectAsync();
                    Status = Text("assistant.mode.changed");
                }
            }
        }
        public AssistantChoiceViewModel? ContextScope {
            get => contextScope;
            set {
                var old = contextScope; this.RaiseAndSetIfChanged(ref contextScope, value);
                if (!configuring && old != value && value != null) {
                    Preferences.ContextScope = value.Id;
                    _ = DisconnectAsync();
                    RefreshContext();
                }
            }
        }
        public ReactiveCommand<RxVoid, RxVoid> SendCommand { get; }
        public ReactiveCommand<RxVoid, RxVoid> StopCommand { get; }
        public ReactiveCommand<RxVoid, RxVoid> ConnectCommand { get; }
        public ReactiveCommand<RxVoid, RxVoid> DisconnectCommand { get; }
        public ReactiveCommand<RxVoid, RxVoid> ResumeCommand { get; }
        public ReactiveCommand<RxVoid, RxVoid> ClearCommand { get; }
        public ReactiveCommand<RxVoid, RxVoid> ProbeCommand { get; }
        public ReactiveCommand<RxVoid, RxVoid> SettingsCommand { get; }
        public ReactiveCommand<string, RxVoid> StartCommand { get; }
        public Action? Hide { get; set; }
        public Func<Task<string>>? McpConfiguration { get; set; }
        public Func<JsonElement, Task>? ShowInEditor { get; set; }
        public Func<JsonElement, Task>? PlayPreview { get; set; }
        public Action? StopPreview { get; set; }
        public Action<string>? AllowDirectory { get; set; }
        public Func<JsonElement, Task<JsonElement>>? Revert { get; set; }
        public Func<string, Task<JsonElement>>? GetJob { get; set; }
        readonly System.Threading.CancellationTokenSource lifetime = new System.Threading.CancellationTokenSource();
        readonly System.Collections.Generic.HashSet<string> pollingJobs = new System.Collections.Generic.HashSet<string>();
        public event Action? ConversationChanged;

        public AssistantSidebarViewModel(AssistantPreferences? preferences = null, AssistantHistoryStore? historyStore = null) {
            Preferences = preferences ?? AssistantPreferences.Load();
            this.historyStore = historyStore ?? new AssistantHistoryStore();
            configuring = true;
            Providers.Add(new AssistantChoiceViewModel("codex", "Codex"));
            Providers.Add(new AssistantChoiceViewModel("claude", "Claude"));
            Providers.Add(new AssistantChoiceViewModel("grok", "Grok"));
            Provider = Providers.FirstOrDefault(p => p.Id == Preferences.Provider) ?? Providers[0];
            RefreshLabels();
            configuring = false;
            restored = this.historyStore.Read();
            if (restored != null) {
                Draft = restored.Draft;
                foreach (var item in restored.Messages.TakeLast(Preferences.HistoryLimit)) {
                    Messages.Add(new AssistantMessageViewModel { Role = item.Role, Title = item.Title, Text = item.Text, Timestamp = item.Timestamp,
                        OperationId = item.OperationId, JobId = item.JobId, ArtifactId = item.ArtifactId,
                        Diagnostics = string.Join("\n", new[] { item.OperationId == null ? null : "operationId: " + item.OperationId,
                            item.JobId == null ? null : "jobId: " + item.JobId, item.ArtifactId == null ? null : "artifactId: " + item.ArtifactId }.Where(value => value != null)) });
                }
                Status = Text("assistant.history.restored");
            } else { Status = Text("assistant.status.disconnected"); }
            ContextSummary = Text("assistant.context.none");
            Messages.CollectionChanged += (_, _) => this.RaisePropertyChanged(nameof(IsEmpty));
            streamTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(40), DispatcherPriority.Background, (_, _) => FlushEvents());
            streamTimer.Start();
            SendCommand = ReactiveCommand.CreateFromTask(SendAsync);
            StopCommand = ReactiveCommand.CreateFromTask(StopAsync);
            ConnectCommand = ReactiveCommand.CreateFromTask(() => ConnectAsync());
            DisconnectCommand = ReactiveCommand.CreateFromTask(DisconnectAsync);
            ResumeCommand = ReactiveCommand.CreateFromTask(ResumeAsync);
            ClearCommand = ReactiveCommand.CreateFromTask(ClearAsync);
            ProbeCommand = ReactiveCommand.CreateFromTask(ProbeAsync);
            SettingsCommand = ReactiveCommand.Create(() => { SettingsOpen = !SettingsOpen; });
            StartCommand = ReactiveCommand.Create<string>(key => { if (key == "assistant.start.song") { ContextScope = ContextScopes.First(s => s.Id == "project"); } Draft = Text(key); });
        }

        public void Initialize(AssistantSessionService session, Func<object> getContext, Func<string, Task<object>> applyProposal, Action<string> rejectProposal) {
            this.session = session;
            this.getContext = getContext;
            this.applyProposal = applyProposal;
            this.rejectProposal = rejectProposal;
            session.Notification += EnqueueEvent;
            RefreshContext();
        }

        public void InitializeExternalSettings(bool enabled, string mode, Func<bool, Task> setEnabled, Action<string> setMode, Func<string[]> getDirectories) {
            setAutomationEnabled = setEnabled; setExternalMode = setMode; this.getDirectories = getDirectories;
            configuring = true;
            AutomationEnabled = enabled;
            ExternalMode = Modes.FirstOrDefault(m => m.Id == mode);
            configuring = false;
            RefreshDirectories();
        }

        public void RefreshDirectories() => AllowedDirectoriesText = getDirectories == null ? "" : string.Join("\n", getDirectories());

        async Task UpdateAutomationEnabledAsync(bool enabled) {
            try {
                if (setAutomationEnabled == null) { return; }
                if (!enabled) { await DisconnectAsync(); }
                await setAutomationEnabled(enabled);
            } catch (Exception ex) { Fail(ex); }
        }

        async Task UpdateExternalModeAsync(string mode) {
            try { setExternalMode?.Invoke(mode); await DisconnectAsync(); } catch (Exception ex) { Fail(ex); }
        }

        public void RefreshLabels() {
            var selected = mode?.Id ?? Preferences.Mode;
            var previous = configuring;
            configuring = true;
            var externalId = externalMode?.Id;
            var scopeId = contextScope?.Id ?? Preferences.ContextScope;
            Modes.Clear();
            Modes.Add(new AssistantChoiceViewModel("read_only", Text("assistant.mode.read")));
            Modes.Add(new AssistantChoiceViewModel("preview", Text("assistant.mode.preview")));
            Modes.Add(new AssistantChoiceViewModel("apply", Text("assistant.mode.apply")));
            Mode = Modes.FirstOrDefault(m => m.Id == selected) ?? Modes[1];
            if (externalId != null) { ExternalMode = Modes.FirstOrDefault(m => m.Id == externalId); }
            ContextScopes.Clear();
            ContextScopes.Add(new AssistantChoiceViewModel("part", Text("assistant.scope.part")));
            ContextScopes.Add(new AssistantChoiceViewModel("project", Text("assistant.scope.project")));
            ContextScope = ContextScopes.FirstOrDefault(s => s.Id == scopeId) ?? ContextScopes[0];
            configuring = previous;
            foreach (var card in Messages) { if (card.Kind != "message") { card.RefreshLabels(); } }
            var stateKey = State == "waiting_user" ? "waiting" : State;
            if (State != "failed") { Status = Text("assistant.status." + stateKey); }
        }

        public void RefreshContext() {
            if (getContext == null) { return; }
            var context = JsonSerializer.SerializeToElement(getContext(), AssistantRuntimePaths.Json);
            var projectName = AssistantSessionService.String(context, "projectName") ?? AssistantSessionService.String(context, "name") ?? Text("assistant.context.project");
            var selected = context.TryGetProperty("noteIds", out var noteIds) && noteIds.ValueKind == JsonValueKind.Array ? noteIds.GetArrayLength() : 0;
            ContextSummary = selected > 0 ? $"{projectName} · {selected} {Text("assistant.context.notes")}" : projectName;
            if (ContextScope?.Id == "project") { ContextSummary += " · " + Text("assistant.scope.project"); }
        }

        public async Task ProjectChangedAsync() {
            foreach (var card in Messages.Where(m => m.Pending && m.IsReview)) { card.Pending = false; card.Status = Text("assistant.card.expired"); }
            await DisconnectAsync();
            RefreshContext();
            Status = Text("assistant.project.changed");
        }

        public void ReportError(Exception exception) => Fail(exception);

        public async Task ConnectAsync(string? resumeId = null) {
            if (session == null || IsBusy) { return; }
            State = "starting";
            Status = Text("assistant.status.starting");
            Preferences.Save();
            try {
                var result = await session.ConnectAsync(Preferences, resumeId);
                ApplyCatalog(result);
                State = "ready";
                Status = Text("assistant.status.ready");
                automationEnabled = true;
                this.RaisePropertyChanged(nameof(AutomationEnabled));
                this.RaisePropertyChanged(nameof(IsConnected));
                await SaveHistoryAsync();
            } catch (Exception ex) { Fail(ex); }
        }

        async Task ResumeAsync() {
            if (restored?.ProviderId != Preferences.Provider || string.IsNullOrEmpty(restored.NativeSessionId)) { Status = Text("assistant.resume.unavailable"); return; }
            await ConnectAsync(restored.NativeSessionId);
        }

        public async Task SendAsync() {
            if (string.IsNullOrWhiteSpace(Draft) || IsBusy) { return; }
            if (getContext != null && IncludeContext && Draft.Trim() == Text("assistant.start.lyrics")) {
                var selection = JsonSerializer.SerializeToElement(getContext(), AssistantRuntimePaths.Json);
                if (selection.TryGetProperty("hasSelection", out var hasSelection) && !hasSelection.GetBoolean()) { Status = Text("assistant.context.selectfirst"); return; }
            }
            if (!IsConnected) { await ConnectAsync(); }
            if (!IsConnected || session == null || getContext == null) { return; }
            var context = getContext();
            var text = Draft.Trim();
            Add(new AssistantMessageViewModel { Role = "user", Title = Text("assistant.role.you"), Text = text });
            Draft = "";
            State = "working";
            Status = Text("assistant.status.working");
            streamingMessage = null;
            turnCancellation = new System.Threading.CancellationTokenSource();
            var cancellation = turnCancellation;
            await SaveHistoryAsync();
            try {
                cancellation.Token.ThrowIfCancellationRequested();
                // Session/revision are always attached; detailed selection only with the visible checkbox.
                object sentContext = context;
                if (!IncludeContext) {
                    var value = JsonSerializer.SerializeToElement(context, AssistantRuntimePaths.Json);
                    sentContext = new { projectSessionId = AssistantSessionService.String(value, "projectSessionId"), editRevision = value.TryGetProperty("editRevision", out var revision) ? revision.Clone() : default(JsonElement?) };
                }
                await session.PromptAsync(text, sentContext, cancellation.Token);
                FlushEvents();
                State = "ready";
                Status = Text("assistant.status.ready");
            } catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { Status = Text("assistant.status.cancelling"); }
            catch (Exception ex) { Fail(ex); }
            finally { turnCancellation = null; cancellation.Dispose(); WithdrawCards(); await SaveHistoryAsync(); }
        }

        public async Task StopAsync() {
            if (session == null) { return; }
            State = "cancelling";
            Status = Text("assistant.status.cancelling");
            turnCancellation?.Cancel();
            try { await session.CancelAsync(); FlushEvents(); WithdrawCards(); State = "ready"; Status = Text("assistant.status.ready"); } catch (Exception ex) { Fail(ex); }
        }

        public async Task DisconnectAsync() {
            if (session != null) {
                try { await session.DisconnectAsync(); } catch (Exception ex) { Fail(ex); return; }
            }
            WithdrawCards();
            StopPreview?.Invoke();
            State = "disconnected";
            Status = Text("assistant.status.disconnected");
            await SaveHistoryAsync();
        }

        async Task ProbeAsync() {
            if (session == null || IsBusy) { return; }
            try {
                var result = await session.ProvidersAsync(Preferences, true);
                Status = (AssistantSessionService.String(result, "status") ?? "unknown") + ": " + (AssistantSessionService.String(result, "message") ?? "");
            } catch (Exception ex) { Fail(ex); }
        }

        async Task SetConfigurationAsync(string method, AssistantChoiceViewModel choice) {
            try {
                if (session == null || IsBusy || !IsConnected) { throw new InvalidOperationException(Text("assistant.configuration.unavailable")); }
                ApplyCatalog(await session.ConfigureAsync(method, choice.Id));
            } catch (Exception ex) {
                Status = ex.Message;
            }
        }

        void ApplyCatalog(JsonElement result) {
            configuring = true;
            try {
                Models.Clear(); Efforts.Clear();
                if (result.TryGetProperty("models", out var models) && models.ValueKind == JsonValueKind.Array) {
                    foreach (var entry in models.EnumerateArray()) {
                        var id = AssistantSessionService.String(entry, "modelId");
                        if (id != null) { Models.Add(new AssistantChoiceViewModel(id, AssistantSessionService.String(entry, "name") ?? id)); }
                    }
                }
                if (result.TryGetProperty("efforts", out var efforts) && efforts.ValueKind == JsonValueKind.Array) {
                    foreach (var entry in efforts.EnumerateArray()) {
                        var id = AssistantSessionService.String(entry, "id");
                        if (id != null) { Efforts.Add(new AssistantChoiceViewModel(id, AssistantSessionService.String(entry, "name") ?? id)); }
                    }
                }
                Model = Models.FirstOrDefault(m => m.Id == AssistantSessionService.String(result, "currentModelId"));
                Effort = Efforts.FirstOrDefault(e => e.Id == AssistantSessionService.String(result, "currentEffortId"));
            } finally { configuring = false; }
        }

        public async Task RespondAsync(AssistantMessageViewModel card, string? optionId = null) {
            if (!card.Pending || session == null) { return; }
            card.Pending = false;
            try {
                await session.RespondAsync(card.Id, card.IsQuestion ? "question.respond" : "permission.respond", optionId, card.IsQuestion ? card.Answer : null);
                card.SetStatus("assistant.card.answered");
                State = "working";
            } catch (Exception ex) { card.Status = ex.Message; }
        }

        public void AddProposal(string id, string label, JsonElement preview) {
            var card = new AssistantMessageViewModel { Id = id, Kind = "review", Title = label, Text = SummarizePreview(preview), Result = preview.Clone(), Pending = true, Status = Text("assistant.card.preview") };
            card.LoadReview(preview);
            card.SetStatus("assistant.card.preview");
            Add(card);
        }

        public async Task RevertAsync(AssistantMessageViewModel card) {
            if (!card.CanRevert || card.Result is not { } result || Revert == null) { return; }
            try { await Revert(result); card.SetStatus("assistant.card.reverted"); card.Result = null; await SaveHistoryAsync(); }
            catch (Exception ex) { card.Status = ex.Message; }
        }

        public async Task ApplyAsync(AssistantMessageViewModel card) {
            if (!card.Pending || applyProposal == null) { return; }
            card.Pending = false;
            try {
                var result = await applyProposal(card.Id);
                card.Result = JsonSerializer.SerializeToElement(result, AssistantRuntimePaths.Json);
                card.SetStatus("assistant.card.applied");
                await SaveHistoryAsync();
            } catch (Exception ex) { card.Status = ex.Message; }
        }

        public void Reject(AssistantMessageViewModel card) {
            if (!card.Pending) { return; }
            rejectProposal?.Invoke(card.Id);
            card.Pending = false;
            card.SetStatus("assistant.card.rejected");
        }

        public void EnqueueEvent(string method, JsonElement arguments) { if (!disposed) { events.Enqueue((method, arguments.Clone())); } }

        public void FlushEvents() {
            if (disposed) { return; }
            var changed = false;
            var delta = new StringBuilder();
            while (events.TryDequeue(out var entry)) {
                var (method, value) = entry;
                if (method == "messageDelta") {
                    delta.Append(AssistantSessionService.String(value, "text"));
                    continue;
                }
                FlushDelta(delta);
                switch (method) {
                    case "messageCompleted":
                        if (streamingMessage == null) { Add(streamingMessage = new AssistantMessageViewModel { Title = Preferences.Provider, Role = "assistant" }); }
                        streamingMessage.Text = AssistantSessionService.String(value, "text") ?? streamingMessage.Text;
                        streamingMessage = null;
                        changed = true;
                        break;
                    case "sessionState":
                        State = AssistantSessionService.String(value, "state") ?? State;
                        break;
                    case "models":
                        ApplyCatalog(value);
                        break;
                    case "toolStarted": case "toolUpdated": case "toolCompleted":
                        var id = AssistantSessionService.String(value, "toolCallId") ?? Guid.NewGuid().ToString("N");
                        var card = Messages.FirstOrDefault(m => m.Id == id);
                        if (card == null) { Add(card = new AssistantMessageViewModel { Id = id, Kind = "tool", Title = AssistantSessionService.String(value, "title") ?? Text("assistant.card.tool") }); }
                        card.Status = AssistantSessionService.String(value, "status") ?? (method == "toolCompleted" ? "completed" : "running");
                        card.Text = ToolSummary(value);
                        card.Diagnostics = value.GetRawText().Length > 16384 ? value.GetRawText()[..16384] : value.GetRawText();
                        if (value.TryGetProperty("result", out var result)) { card.Result = result.Clone(); }
                        if (method == "toolCompleted" && card.Result is { } toolResult) { ObserveResult(card, toolResult); }
                        changed = true;
                        break;
                    case "permissionRequested": case "questionRequested":
                        var request = new AssistantMessageViewModel {
                            Id = AssistantSessionService.String(value, "requestId") ?? "", Kind = method == "permissionRequested" ? "permission" : "question",
                            Title = AssistantSessionService.String(value, "title") ?? Text("assistant.card.question"),
                            Text = AssistantSessionService.String(value, "question") ?? AssistantSessionService.String(value, "title") ?? "", Pending = true,
                        };
                        if (value.TryGetProperty("options", out var options) && options.ValueKind == JsonValueKind.Array) {
                            foreach (var option in options.EnumerateArray()) {
                                var optionId = AssistantSessionService.String(option, "optionId");
                                if (optionId != null) { request.Options.Add(new AssistantChoiceViewModel(optionId, AssistantSessionService.String(option, "name") ?? optionId)); }
                            }
                        }
                        Add(request);
                        State = "waiting_user";
                        Status = Text("assistant.status.waiting");
                        changed = true;
                        break;
                    case "requestWithdrawn":
                        var withdrawn = Messages.FirstOrDefault(m => m.Id == AssistantSessionService.String(value, "requestId"));
                        if (withdrawn != null) { withdrawn.Pending = false; withdrawn.SetStatus("assistant.card.expired"); }
                        break;
                    case "error": case "processExited":
                        State = "failed";
                        Status = AssistantSessionService.String(value, "message") ?? Text("assistant.status.failed");
                        WithdrawCards();
                        break;
                    case "turnCompleted":
                        State = "ready";
                        Status = Text("assistant.status.ready");
                        WithdrawCards();
                        streamingMessage = null;
                        changed = true;
                        break;
                }
            }
            FlushDelta(delta);
            if (changed) { ConversationChanged?.Invoke(); }
        }

        void FlushDelta(StringBuilder delta) {
            if (delta.Length == 0) { return; }
            if (streamingMessage == null) { Add(streamingMessage = new AssistantMessageViewModel { Title = Preferences.Provider, Role = "assistant" }); }
            streamingMessage.Text += delta.ToString();
            delta.Clear();
            ConversationChanged?.Invoke();
        }

        void Add(AssistantMessageViewModel message) {
            Messages.Add(message);
            while (Messages.Count > Preferences.HistoryLimit) { Messages.RemoveAt(0); }
            ConversationChanged?.Invoke();
        }

        void WithdrawCards() {
            foreach (var card in Messages.Where(m => m.Pending && m.Kind is "permission" or "question")) { card.Pending = false; card.SetStatus("assistant.card.expired"); }
        }

        void ObserveResult(AssistantMessageViewModel card, JsonElement result) {
            if (result.ValueKind != JsonValueKind.Object) { return; }
            if (AssistantSessionService.String(result, "jobId") is { } jobId && GetJob != null && pollingJobs.Add(jobId)) { _ = PollJobAsync(card, jobId); }
            if (result.TryGetProperty("artifacts", out var artifacts) && artifacts.ValueKind == JsonValueKind.Array) {
                foreach (var artifact in artifacts.EnumerateArray()) {
                    var id = AssistantSessionService.String(artifact, "artifactId");
                    if (id == null || Messages.Any(m => m.Id == id)) { continue; }
                    var duration = artifact.TryGetProperty("durationSeconds", out var seconds) && seconds.TryGetDouble(out var number) ? number : 0;
                    var range = artifact.TryGetProperty("startTicks", out var start) && artifact.TryGetProperty("endTicks", out var end) ? $" · {start}–{end} ticks" : "";
                    Add(new AssistantMessageViewModel { Id = id, Kind = "audio", Title = Text("assistant.card.audio"), Text = $"{duration:0.0} s{range}", Result = artifact.Clone(), Status = Text("assistant.card.ready") });
                }
            }
        }

        async Task PollJobAsync(AssistantMessageViewModel card, string jobId) {
            try {
                while (!lifetime.IsCancellationRequested && GetJob != null) {
                    var result = await GetJob(jobId);
                    var jobState = AssistantSessionService.String(result, "status") ?? "unknown";
                    card.Status = jobState;
                    if (result.TryGetProperty("stale", out var stale) && stale.ValueKind == JsonValueKind.True) { card.Status += " · " + Text("assistant.card.stale"); }
                    if (jobState == "succeeded") { card.Result = result.Clone(); ObserveResult(card, result); await SaveHistoryAsync(); return; }
                    if (jobState is "failed" or "cancelled") {
                        if (result.TryGetProperty("error", out var error)) { card.Text = AssistantSessionService.String(error, "message") ?? Text("assistant.card.jobfailed"); }
                        return;
                    }
                    await Task.Delay(500, lifetime.Token);
                }
            } catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
            catch (Exception ex) { card.Status = ex.Message; }
            finally { pollingJobs.Remove(jobId); }
        }

        async Task ClearAsync() {
            await DisconnectAsync();
            Messages.Clear();
            restored = null;
            await historyStore.ClearAsync();
        }

        async Task SaveHistoryAsync() {
            try {
                var context = getContext != null ? JsonSerializer.SerializeToElement(getContext(), AssistantRuntimePaths.Json) : default;
                var currentProvider = session?.NativeProviderId == Preferences.Provider;
                var history = new AssistantHistory(1, Preferences.Provider, currentProvider ? session!.NativeSessionId : restored?.ProviderId == Preferences.Provider ? restored.NativeSessionId : null, AssistantSessionService.String(context, "projectSessionId"), Draft,
                    Messages.Select(m => new AssistantHistoryMessage(m.Role, m.Title, m.Text, m.Timestamp, m.OperationId, m.JobId, m.ArtifactId)).ToArray(),
                    currentProvider ? Model?.Id : restored?.ProviderId == Preferences.Provider ? restored.ModelId : null,
                    currentProvider ? Effort?.Id : restored?.ProviderId == Preferences.Provider ? restored.EffortId : null);
                await historyStore.SaveAsync(history, Preferences.HistoryLimit);
                restored = history;
            } catch (Exception ex) when (ex is System.IO.IOException || ex is UnauthorizedAccessException) { Status = Text("assistant.history.error") + " " + ex.Message; }
        }

        public async ValueTask DisposeAsync() {
            if (disposed) { return; }
            disposed = true;
            streamTimer.Stop();
            lifetime.Cancel();
            StopPreview?.Invoke();
            try {
                await SaveHistoryAsync();
                Preferences.Save();
            } finally {
                if (session != null) { session.Notification -= EnqueueEvent; await session.DisposeAsync(); }
            }
        }

        void Fail(Exception exception) {
            State = "failed";
            Status = exception.Message;
            WithdrawCards();
        }

        static string ToolSummary(JsonElement value) {
            if (value.TryGetProperty("result", out var result) && result.ValueKind == JsonValueKind.Object) {
                if (AssistantSessionService.String(result, "summary") is { } summary) { return summary; }
                if (AssistantSessionService.String(result, "message") is { } message) { return message; }
                if (result.TryGetProperty("appliedOperations", out var operations) && operations.ValueKind == JsonValueKind.Array) { return $"{operations.GetArrayLength()} {Text("assistant.card.operations")}"; }
                if (AssistantSessionService.String(result, "jobId") is { }) { return Text("assistant.card.job"); }
                return Text("assistant.card.result");
            }
            return AssistantSessionService.String(value, "content") ?? "";
        }

        static string SummarizePreview(JsonElement preview) {
            var summary = AssistantSessionService.String(preview, "summary");
            if (summary != null) { return summary; }
            if (preview.TryGetProperty("noteChanges", out var notes) && notes.ValueKind == JsonValueKind.Array) {
                return notes.GetArrayLength() + " " + Text("assistant.review.notes");
            }
            if (preview.TryGetProperty("operations", out var operations) && operations.ValueKind == JsonValueKind.Array) {
                return string.Join("\n", operations.EnumerateArray().Take(30).Select(op => (AssistantSessionService.String(op, "type") ?? AssistantSessionService.String(op, "kind") ?? Text("assistant.card.edit")) + " · " + (AssistantSessionService.String(op, "lyric") ?? AssistantSessionService.String(op, "label") ?? "")));
            }
            return Text("assistant.card.preview");
        }

        static string Text(string key) => ThemeManager.GetString(key);
    }
}
