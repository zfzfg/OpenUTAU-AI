using System;
using System.IO;
using System.Text.Json;

namespace OpenUtau.Automation {
    public sealed class AutomationSettings {
        public int SchemaVersion { get; set; } = 1;
        public bool Enabled { get; set; } = true;
        public string ExternalMode { get; set; } = "apply";
        public string[] AllowedDirectories { get; set; } = Array.Empty<string>();
        static string FilePath => Path.Combine(Path.GetDirectoryName(AutomationPrivateFiles.RuntimeDirectory)!, "automation-settings.json");
        public static AutomationSettings Load() {
            try {
                if (!File.Exists(FilePath)) return new();
                var settings = JsonSerializer.Deserialize<AutomationSettings>(File.ReadAllText(FilePath), AutomationJson.Options);
                if (settings?.SchemaVersion == 1 && settings.ExternalMode is "apply" or "preview" or "read_only") return settings;
            } catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { }
            return new();
        }
        public void Save() => AutomationPrivateFiles.Write(FilePath, this);
    }
}
