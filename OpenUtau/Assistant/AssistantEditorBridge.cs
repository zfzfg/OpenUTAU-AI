using System.Text.Json;
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using OpenUtau.Automation;

namespace OpenUtau.App.Assistant {
    public sealed class AssistantEditorBridge : IAssistantEditor {
        readonly AutomationLifecycleService lifecycle;
        readonly Func<string> scope;
        public AssistantEditorBridge(AutomationLifecycleService lifecycle, Func<string>? scope = null) { this.lifecycle = lifecycle; this.scope = scope ?? (() => "part"); }
        public string InstanceId => lifecycle.InstanceId;
        public string WorkingDirectory => string.IsNullOrEmpty(OpenUtau.Core.DocManager.Inst.Project.FilePath)
            ? Path.Combine(lifecycle.RuntimeDirectory, "workspace") : Path.GetDirectoryName(OpenUtau.Core.DocManager.Inst.Project.FilePath)!;
        public object GetContext() => lifecycle.Context.GetSnapshot();
        public Task StartAsync(CancellationToken token) => lifecycle.StartAsync(token);
        public Task<string> CreateGrantAsync(string clientId, string mode, long generation, CancellationToken token) {
            if (scope() == "project" || mode == "read_only") { return lifecycle.CreateNativeGrantAsync(clientId, mode, generation, token); }
            // The selected parts (including a detached piano roll's active part) bound the musical grant.
            var context = JsonSerializer.SerializeToElement(GetContext(), AssistantRuntimePaths.Json);
            string[]? partIds = null;
            if (context.TryGetProperty("partIds", out var parts) && parts.GetArrayLength() > 0) {
                partIds = JsonSerializer.Deserialize<string[]>(parts.GetRawText());
            } else if (AssistantSessionService.String(context, "activePartId") is { } active) { partIds = new[] { active }; }
            if (partIds == null) { throw new InvalidOperationException(ThemeManager.GetString("assistant.context.scopefirst")); }
            return lifecycle.CreateNativeGrantAsync(clientId, mode, generation, token, partIds);
        }
        public void RevokeGrant(string reference) => lifecycle.RevokeNativeGrant(reference);
    }
}
