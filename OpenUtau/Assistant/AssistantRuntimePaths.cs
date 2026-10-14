using System;
using System.IO;
using System.Text.Json;

namespace OpenUtau.App.Assistant {
    public static class AssistantRuntimePaths {
        // Never put transcripts in a portable install or a song directory.
        public static string UserDirectory => Environment.GetEnvironmentVariable("OPENUTAU_ASSISTANT_DATA_DIR") is { Length: > 0 } directory
            ? Path.GetFullPath(directory) : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OpenUtau", "Assistant");

        public static (string Node, string Host, string Mcp) Resolve(string? configuredNode = null, string? configuredHost = null) {
            var configuredDirectory = Environment.GetEnvironmentVariable("OPENUTAU_AI_DIR");
            var directory = string.IsNullOrWhiteSpace(configuredDirectory) ? Path.Combine(AppContext.BaseDirectory, "AI") : Path.GetFullPath(configuredDirectory);
            var environmentNode = Environment.GetEnvironmentVariable("OPENUTAU_AI_NODE_PATH");
            var node = string.IsNullOrWhiteSpace(configuredNode) ? (string.IsNullOrWhiteSpace(environmentNode) ? Path.Combine(directory, OperatingSystem.IsWindows() ? "node.exe" : "node") : Path.GetFullPath(environmentNode)) : Path.GetFullPath(configuredNode);
            var host = string.IsNullOrWhiteSpace(configuredHost) ? Path.Combine(directory, "agent-host.js") : Path.GetFullPath(configuredHost);
            if (!File.Exists(node) || !File.Exists(host)) {
                throw new FileNotFoundException("AI runtime is missing. Build/install the OpenUtau AI package, or select its Node and agent-host files in Assistant settings.");
            }
            if (string.IsNullOrWhiteSpace(configuredHost) || File.Exists(Path.Combine(Path.GetDirectoryName(host)!, "manifest.json"))) {
                OpenUtau.Automation.AutomationPackageVerifier.Verify(Path.GetDirectoryName(host)!);
            }
            return (node, host, Path.Combine(Path.GetDirectoryName(host)!, "mcp-server.js"));
        }

        public static JsonSerializerOptions Json { get; } = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = false, DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull };

        public static string McpConfiguration(AssistantPreferences preferences) {
            var paths = Resolve(preferences.NodePath, preferences.HostPath);
            if (!File.Exists(paths.Mcp)) { throw new FileNotFoundException("The MCP server is missing from the selected AI package."); }
            return JsonSerializer.Serialize(new { mcpServers = new { openutau = new { command = paths.Node, args = new[] { paths.Mcp, "--stdio" } } } }, new JsonSerializerOptions(Json) { WriteIndented = true });
        }
    }

    public sealed class AssistantPreferences {
        public int SchemaVersion { get; set; } = 1;
        public bool Visible { get; set; }
        public double Width { get; set; } = 360;
        public string Provider { get; set; } = "codex";
        public string Mode { get; set; } = "preview";
        public string ContextScope { get; set; } = "part";
        public string NodePath { get; set; } = "";
        public string HostPath { get; set; } = "";
        public string CliPath { get; set; } = "";
        public string AdapterPath { get; set; } = "";
        public int HistoryLimit { get; set; } = 1000;

        public static AssistantPreferences Load() {
            try {
                var path = Path.Combine(AssistantRuntimePaths.UserDirectory, "preferences.json");
                if (File.Exists(path)) {
                    var value = JsonSerializer.Deserialize<AssistantPreferences>(File.ReadAllText(path), AssistantRuntimePaths.Json);
                    if (value?.SchemaVersion == 1) {
                        value.Width = Math.Clamp(value.Width, 300, 520);
                        value.HistoryLimit = Math.Clamp(value.HistoryLimit, 100, 5000);
                        value.Provider = value.Provider is "codex" or "claude" or "grok" ? value.Provider : "codex";
                        value.Mode = value.Mode is "read_only" or "preview" or "apply" ? value.Mode : "preview";
                        value.ContextScope = value.ContextScope == "project" ? "project" : "part";
                        value.NodePath ??= ""; value.HostPath ??= ""; value.CliPath ??= ""; value.AdapterPath ??= "";
                        return value;
                    }
                }
            } catch (IOException) { } catch (JsonException) { } catch (UnauthorizedAccessException) { }
            return new AssistantPreferences();
        }

        public void Save() {
            OpenUtau.Automation.AutomationPrivateFiles.ProtectDirectory(AssistantRuntimePaths.UserDirectory);
            var path = Path.Combine(AssistantRuntimePaths.UserDirectory, "preferences.json");
            var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(this, AssistantRuntimePaths.Json));
            File.Move(temp, path, true);
        }
    }
}
