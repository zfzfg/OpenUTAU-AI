using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace OpenUtau.App.Assistant {
    public sealed record AssistantHistoryMessage(string Role, string Title, string Text, DateTimeOffset Timestamp,
        string? OperationId = null, string? JobId = null, string? ArtifactId = null);
    public sealed record AssistantHistory(int SchemaVersion, string ProviderId, string? NativeSessionId, string? ProjectSessionId, string Draft, IReadOnlyList<AssistantHistoryMessage> Messages,
        string? ModelId = null, string? EffortId = null);

    public sealed class AssistantHistoryStore {
        const int MaximumHistoryBytes = 8 * 1024 * 1024;
        readonly string directory;
        readonly SemaphoreSlim gate = new SemaphoreSlim(1);
        public AssistantHistoryStore(string? directory = null) { this.directory = directory ?? AssistantRuntimePaths.UserDirectory; }

        public AssistantHistory? Read() {
            try {
                var path = Path.Combine(directory, "history.json");
                if (!File.Exists(path) || new FileInfo(path).Length > 8 * 1024 * 1024) { return null; }
                var history = JsonSerializer.Deserialize<AssistantHistory>(File.ReadAllText(path), AssistantRuntimePaths.Json);
                return history?.SchemaVersion == 1 && history.ProviderId != null && history.Draft != null && history.Messages != null && history.Messages.All(m => m != null && m.Role != null && m.Title != null && m.Text != null) ? history : null;
            } catch (IOException) { return null; } catch (JsonException) { return null; } catch (UnauthorizedAccessException) { return null; }
        }

        public async Task SaveAsync(AssistantHistory history, int limit, CancellationToken cancellationToken = default) {
            await gate.WaitAsync(cancellationToken);
            string? temp = null;
            try {
                OpenUtau.Automation.AutomationPrivateFiles.ProtectDirectory(directory);
                var path = Path.Combine(directory, "history.json");
                temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
                var bounded = history with { Draft = Bound(history.Draft, 65536), ProviderId = Bound(history.ProviderId, 128),
                    NativeSessionId = history.NativeSessionId?.Length > 4096 ? null : history.NativeSessionId,
                    ProjectSessionId = history.ProjectSessionId?.Length > 128 ? null : history.ProjectSessionId,
                    ModelId = Identifier(history.ModelId), EffortId = Identifier(history.EffortId),
                    Messages = Array.Empty<AssistantHistoryMessage>() };
                var used = JsonSerializer.SerializeToUtf8Bytes(bounded, AssistantRuntimePaths.Json).Length;
                var retained = new List<AssistantHistoryMessage>();
                foreach (var message in history.Messages.TakeLast(Math.Clamp(limit, 100, 5000)).Reverse()) {
                    var item = message with { Role = Bound(message.Role, 32), Title = Bound(message.Title, 1024), Text = Bound(message.Text, 65536),
                        OperationId = Identifier(message.OperationId), JobId = Identifier(message.JobId), ArtifactId = Identifier(message.ArtifactId) };
                    var size = JsonSerializer.SerializeToUtf8Bytes(item, AssistantRuntimePaths.Json).Length + (retained.Count > 0 ? 1 : 0);
                    if (used + size > MaximumHistoryBytes) { break; }
                    retained.Add(item); used += size;
                }
                retained.Reverse();
                bounded = bounded with { Messages = retained.ToArray() };
                await File.WriteAllBytesAsync(temp, JsonSerializer.SerializeToUtf8Bytes(bounded, AssistantRuntimePaths.Json), cancellationToken);
                if (File.Exists(path)) { File.Copy(path, path + ".previous", true); }
                File.Move(temp, path, true);
            } finally {
                if (temp != null && File.Exists(temp)) { try { File.Delete(temp); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
                gate.Release();
            }
        }

        static string Bound(string value, int length) => value.Length <= length ? value : value[..length];
        static string? Identifier(string? value) => value?.Length > 4096 ? null : value;

        public async Task ClearAsync() {
            await gate.WaitAsync();
            try {
                foreach (var file in new[] { "history.json", "history.json.previous" }) {
                    var path = Path.Combine(directory, file);
                    if (File.Exists(path)) { File.Delete(path); }
                }
            } finally { gate.Release(); }
        }
    }
}
