using System;
using System.Collections.Concurrent;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace OpenUtau.App.Assistant {
    public interface IAssistantEditor {
        string InstanceId { get; }
        string WorkingDirectory { get; }
        object GetContext();
        Task StartAsync(CancellationToken token);
        Task<string> CreateGrantAsync(string clientId, string mode, long generation, CancellationToken token);
        void RevokeGrant(string reference);
    }

    /// <summary>Owns the host independently of sidebar visibility. Restored history never replays writes.</summary>
    public sealed class AssistantSessionService : IAsyncDisposable {
        readonly IAssistantEditor editor;
        readonly Func<IAssistantProcessClient> clientFactory;
        readonly SemaphoreSlim lifecycleGate = new SemaphoreSlim(1);
        readonly ConcurrentDictionary<string, JsonElement> requests = new ConcurrentDictionary<string, JsonElement>();
        IAssistantProcessClient? client;
        string? grant;
        string? boundProject;
        string clientId = "native-" + Guid.NewGuid().ToString("N");
        long generation;
        bool disposed;
        TaskCompletionSource<bool>? turnFinished;
        public string? SessionId { get; private set; }
        public string? NativeSessionId { get; private set; }
        public string? NativeProviderId { get; private set; }
        public string? TurnId { get; private set; }
        public long ProcessGeneration => generation;
        public event Action<string, JsonElement>? Notification;
        public AssistantSessionService(IAssistantEditor editor, Func<IAssistantProcessClient>? clientFactory = null) {
            this.editor = editor;
            this.clientFactory = clientFactory ?? (() => new AssistantProcessClient());
        }

        public async Task<JsonElement> ConnectAsync(AssistantPreferences preferences, string? resumeId = null, CancellationToken token = default) {
            await lifecycleGate.WaitAsync(token);
            try {
                ObjectDisposedException.ThrowIf(disposed, this);
                await DisconnectCoreAsync();
                await editor.StartAsync(token);
                var paths = AssistantRuntimePaths.Resolve(preferences.NodePath, preferences.HostPath);
                client = clientFactory();
                client.Notification += OnNotification;
                generation++;
                try {
                    await client.StartAsync(paths.Node, paths.Host, generation, token);
                    var context = JsonSerializer.SerializeToElement(editor.GetContext(), AssistantRuntimePaths.Json);
                    boundProject = String(context, "projectSessionId");
                    grant = await editor.CreateGrantAsync(clientId, preferences.Mode, generation, token);
                    var result = await client.RequestAsync(resumeId == null ? "session.create" : "session.load", new {
                        providerId = preferences.Provider, cwd = editor.WorkingDirectory,
                        cliPath = NullIfEmpty(preferences.CliPath), adapterPath = NullIfEmpty(preferences.AdapterPath),
                        instanceId = editor.InstanceId, projectSessionId = boundProject, accessGrantRef = grant, clientId,
                        nativeSessionId = resumeId, mode = preferences.Mode,
                    }, token);
                    if (!result.TryGetProperty("processGeneration", out var number) || number.GetInt64() != generation) { throw new InvalidDataException("Session generation mismatch."); }
                    SessionId = String(result, "sessionId") ?? throw new InvalidDataException("Session ID is missing.");
                    NativeSessionId = String(result, "nativeSessionId");
                    NativeProviderId = preferences.Provider;
                    return result;
                } catch {
                    await DisconnectCoreAsync();
                    throw;
                }
            } finally { lifecycleGate.Release(); }
        }

        public async Task<JsonElement> ProvidersAsync(AssistantPreferences preferences, bool probe, CancellationToken token = default) {
            await lifecycleGate.WaitAsync(token);
            try {
                ObjectDisposedException.ThrowIf(disposed, this);
                if (client == null) {
                    var paths = AssistantRuntimePaths.Resolve(preferences.NodePath, preferences.HostPath);
                    client = clientFactory();
                    client.Notification += OnNotification;
                    generation++;
                    await client.StartAsync(paths.Node, paths.Host, generation, token);
                }
                return await client.RequestAsync(probe ? "providers.probe" : "providers.list", probe ? new { providerId = preferences.Provider, cliPath = NullIfEmpty(preferences.CliPath), adapterPath = NullIfEmpty(preferences.AdapterPath) } : new { }, token);
            } finally { lifecycleGate.Release(); }
        }

        public Task<JsonElement> PromptAsync(string text, object context, CancellationToken token = default) {
            if (SessionId == null || client == null) { throw new InvalidOperationException("Connect a provider first."); }
            var snapshot = JsonSerializer.SerializeToElement(context, AssistantRuntimePaths.Json);
            if (String(snapshot, "projectSessionId") != boundProject) { throw new InvalidOperationException("The project changed. Reconnect to bind a new session before sending."); }
            TurnId = null;
            turnFinished = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            return client.RequestAsync("session.prompt", new { sessionId = SessionId, text, context }, token);
        }

        public async Task CancelAsync() {
            WithdrawRequests("cancelled");
            if (client != null && SessionId != null) {
                await client.NotifyAsync("session.cancel", new { sessionId = SessionId });
                if (turnFinished != null) { await turnFinished.Task.WaitAsync(TimeSpan.FromSeconds(8)); }
            }
        }

        public async Task<JsonElement> ConfigureAsync(string method, string id, CancellationToken token = default) {
            if (client == null || SessionId == null) { throw new InvalidOperationException("No active session."); }
            object arguments = method == "session.setModel" ? new { sessionId = SessionId, modelId = id } : new { sessionId = SessionId, effortId = id };
            return await client.RequestAsync(method, arguments, token);
        }

        public async Task RespondAsync(string requestId, string method, string? optionId, string? answer, CancellationToken token = default) {
            if (client == null || SessionId == null || !requests.TryRemove(requestId, out var request)) { throw new InvalidOperationException("This request has expired or was already answered."); }
            if (String(request, "sessionId") != SessionId || request.GetProperty("processGeneration").GetInt64() != generation || String(request, "turnId") != TurnId) { throw new InvalidOperationException("This request belongs to an old turn."); }
            try {
                await client.RequestAsync(method, new { requestId, sessionId = SessionId, turnId = TurnId, processGeneration = generation, optionId, answer }, token);
            } catch {
                // Failed responses stay withdrawn: replay is unsafe when the host may have accepted them.
                throw;
            }
        }

        void OnNotification(string method, JsonElement arguments) {
            if (arguments.TryGetProperty("processGeneration", out var value) && value.GetInt64() != generation) { return; }
            if (arguments.TryGetProperty("sessionId", out var session) && SessionId != null && session.GetString() != SessionId) { return; }
            if (arguments.TryGetProperty("turnId", out var turn) && turn.ValueKind == JsonValueKind.String) {
                var id = turn.GetString();
                if (TurnId != null && id != TurnId && !(method == "sessionState" && String(arguments, "state") == "working")) { return; }
                if (method != "sessionState" || String(arguments, "state") != "ready") { TurnId = id; }
            }
            if (method is "permissionRequested" or "questionRequested") {
                var id = String(arguments, "requestId");
                if (id != null && !requests.TryAdd(id, arguments.Clone())) { return; }
            } else if (method == "requestWithdrawn") {
                if (String(arguments, "requestId") is { } id) { requests.TryRemove(id, out _); }
            } else if (method is "turnCompleted" or "processExited") {
                turnFinished?.TrySetResult(true);
                WithdrawRequests(method);
                if (method == "processExited") {
                    if (grant != null) { editor.RevokeGrant(grant); grant = null; }
                    SessionId = null;
                }
            }
            Notification?.Invoke(method, arguments);
            if (method == "turnCompleted") { TurnId = null; }
        }

        void WithdrawRequests(string reason) {
            foreach (var entry in requests) {
                if (requests.TryRemove(entry.Key, out _)) {
                    Notification?.Invoke("requestWithdrawn", JsonSerializer.SerializeToElement(new { requestId = entry.Key, reason, processGeneration = generation }, AssistantRuntimePaths.Json));
                }
            }
        }

        public async Task DisconnectAsync() {
            await lifecycleGate.WaitAsync();
            try { await DisconnectCoreAsync(); } finally { lifecycleGate.Release(); }
        }

        async Task DisconnectCoreAsync() {
            WithdrawRequests("disconnected");
            if (grant != null) { editor.RevokeGrant(grant); grant = null; }
            if (client != null) {
                try {
                    if (SessionId != null) {
                        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                        await client.RequestAsync("session.dispose", new { sessionId = SessionId }, timeout.Token);
                    }
                } catch (Exception ex) when (ex is IOException || ex is OperationCanceledException || ex is AssistantRpcException || ex is InvalidOperationException) { }
                client.Notification -= OnNotification;
                await client.DisposeAsync();
                client = null;
            }
            SessionId = null;
            TurnId = null;
            boundProject = null;
        }

        public async ValueTask DisposeAsync() {
            if (disposed) { return; }
            disposed = true;
            await DisconnectAsync();
            lifecycleGate.Dispose();
        }

        static string? NullIfEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;
        public static string? String(JsonElement value, string name) => value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String ? property.GetString() : null;
    }
}
