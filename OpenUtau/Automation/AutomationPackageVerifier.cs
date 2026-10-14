using System;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;

namespace OpenUtau.Automation {
    public static class AutomationPackageVerifier {
        public static void Verify(string directory) {
            var manifestPath = Path.Combine(directory, "manifest.json");
            if (!File.Exists(manifestPath)) throw new InvalidDataException("The AI package manifest is missing. Rebuild or reinstall the AI runtime.");
            using var document = JsonDocument.Parse(File.ReadAllText(manifestPath));
            var manifest = document.RootElement;
            if (manifest.GetProperty("schemaVersion").GetInt32() != 1 || manifest.GetProperty("apiVersion").GetString() != "1")
                throw new InvalidDataException("The AI package uses an unsupported contract version.");
            foreach (var artifact in manifest.GetProperty("artifacts").EnumerateObject())
                VerifyFile(directory, artifact.Name, artifact.Value.GetProperty("sha256").GetString()!);
            if (manifest.TryGetProperty("runtime", out var runtime))
                VerifyFile(directory, runtime.GetProperty("executable").GetString()!, runtime.GetProperty("sha256").GetString()!);
        }
        static void VerifyFile(string directory, string name, string expected) {
            var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory)) + Path.DirectorySeparatorChar;
            var path = Path.GetFullPath(Path.Combine(root, name));
            if (!path.StartsWith(root, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal) || !File.Exists(path))
                throw new InvalidDataException("The AI package contains an invalid or missing artifact.");
            using var stream = File.OpenRead(path);
            if (!string.Equals(Convert.ToHexString(SHA256.HashData(stream)), expected, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The AI package checksum is invalid. Rebuild or reinstall the AI runtime.");
        }
    }
}
