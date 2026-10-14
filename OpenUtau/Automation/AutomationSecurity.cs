using System;
using System.Collections.Concurrent;
using System.IO;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using OpenUtau.Core.Automation;

namespace OpenUtau.Automation {
    public static class AutomationJson {
        public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);
        public static JsonElement Element(object value) => JsonSerializer.SerializeToElement(value, Options);
        public static string Required(JsonElement value, string name) {
            if (!value.TryGetProperty(name, out var field) || field.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(field.GetString()))
                throw new AutomationException("INVALID_ARGUMENT", $"{name} is required.");
            return field.GetString()!;
        }
        public static string? Text(JsonElement value, string name) => value.TryGetProperty(name, out var field) && field.ValueKind == JsonValueKind.String ? field.GetString() : null;
        public static bool Bool(JsonElement value, string name) => value.TryGetProperty(name, out var field) && field.ValueKind == JsonValueKind.True;
        public static string Secret() => Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        public static bool EqualSecret(string? a, string b) => a != null && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(a), Encoding.UTF8.GetBytes(b));
    }

    public static class AutomationPrivateFiles {
        public static string RuntimeDirectory {
            get {
                if (Environment.GetEnvironmentVariable("OPENUTAU_AUTOMATION_DIR") is { Length: > 0 } configured) {
                    if (!Path.IsPathFullyQualified(configured)) throw new IOException("Automation runtime directory must be absolute.");
                    return Path.GetFullPath(configured);
                }
                var root = OperatingSystem.IsWindows() ? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)
                    : Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR") ?? Environment.GetEnvironmentVariable("XDG_DATA_HOME")
                        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share");
                return Path.Combine(root, "OpenUtau", "automation");
            }
        }
        public static void ProtectDirectory(string path) {
            Directory.CreateDirectory(path);
            if ((new DirectoryInfo(path).Attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("Automation storage cannot be a symbolic link.");
            if (OperatingSystem.IsWindows()) {
                var sid = WindowsIdentity.GetCurrent().User ?? throw new IOException("Cannot resolve current user.");
                var security = new DirectorySecurity();
                security.SetOwner(sid);
                security.SetAccessRuleProtection(true, false);
                security.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl,
                    InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
                new DirectoryInfo(path).SetAccessControl(security);
            } else File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        public static void Write(string path, object value) {
            ProtectDirectory(Path.GetDirectoryName(path)!);
            var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try {
                File.WriteAllText(temp, JsonSerializer.Serialize(value, AutomationJson.Options), new UTF8Encoding(false));
                if (OperatingSystem.IsWindows()) {
                    var sid = WindowsIdentity.GetCurrent().User ?? throw new IOException("Cannot resolve current user.");
                    var security = new FileSecurity();
                    security.SetOwner(sid);
                    security.SetAccessRuleProtection(true, false);
                    security.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl, AccessControlType.Allow));
                    new FileInfo(temp).SetAccessControl(security);
                } else File.SetUnixFileMode(temp, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                File.Move(temp, path, true);
            } finally { if (File.Exists(temp)) File.Delete(temp); }
        }
    }

    public sealed record AutomationGrant(string GrantId, string Credential, string ClientId, string ProjectSessionId,
        string Mode, long ProcessGeneration, DateTimeOffset ExpiresAt, bool Native, string[]? PartIds = null);

    public sealed class AutomationAccessService {
        readonly ConcurrentDictionary<string, AutomationGrant> grants = new();
        public string ExternalMode { get; set; } = "apply";
        public AutomationGrant Create(string clientId, string projectSessionId, string mode, long generation, bool native, string[]? partIds = null) {
            if (mode != "read_only" && mode != "preview" && mode != "apply") throw new AutomationException("INVALID_ARGUMENT", "Unknown access mode.");
            if (string.IsNullOrWhiteSpace(clientId) || clientId.Length > 128) throw new AutomationException("INVALID_ARGUMENT", "Invalid clientId.");
            Sweep();
            if (grants.Count >= 128) throw new AutomationException("LIMIT_EXCEEDED", "Too many active connections.");
            var grant = new AutomationGrant(Guid.NewGuid().ToString("N"), AutomationJson.Secret(), clientId, projectSessionId,
                mode, generation, DateTimeOffset.UtcNow.AddHours(8), native, partIds);
            grants[grant.Credential] = grant;
            return grant;
        }
        public AutomationGrant Authenticate(string? credential) {
            if (credential == null || !grants.TryGetValue(credential, out var grant) || grant.ExpiresAt <= DateTimeOffset.UtcNow)
                throw new AutomationException("UNAUTHORIZED", "The connection has expired or was revoked. Reconnect in OpenUtau.");
            return grant;
        }
        public void Demand(AutomationGrant grant, string operation, string projectSessionId, JsonElement args) {
            Authenticate(grant.Credential);
            if (grant.ProjectSessionId != projectSessionId && operation is not ("get_operation" or "get_job" or "cancel_job")) throw new AutomationException("PROJECT_CHANGED", "Read and explicitly reconnect to the new project.");
            if (!IsWrite(operation) && operation != "preview_edit") return;
            if (IsWrite(operation) && grant.Mode == "read_only") throw new AutomationException("UNAUTHORIZED", "This connection only allows reading music.");
            if (grant.PartIds != null) {
                if (operation != "apply_edit_batch" && operation != "preview_edit") throw new AutomationException("UNAUTHORIZED", "This connection is limited to selected parts.");
                if (!args.TryGetProperty("operations", out var operations) || operations.ValueKind != JsonValueKind.Array)
                    throw new AutomationException("INVALID_ARGUMENT", "operations must be an array.");
                foreach (var item in operations.EnumerateArray()) {
                    var part = AutomationJson.Text(item, "partId");
                    var type = AutomationJson.Text(item, "type");
                    if (part == null || !Array.Exists(grant.PartIds, id => id == part) || type is "update_part" or "remove_part" or "add_part")
                        throw new AutomationException("UNAUTHORIZED", "The operation exceeds the granted musical area.");
                }
            }
        }
        public static bool IsWrite(string operation) => operation is "apply_edit_batch" or "new_project" or "open_project" or "import_midi" or "import_audio" or "save_project" or "revert_operation";
        public void Revoke(string credential) => grants.TryRemove(credential, out _);
        public void RevokeAll() => grants.Clear();
        public void RevokeNative() { foreach (var pair in grants) if (pair.Value.Native) grants.TryRemove(pair.Key, out _); }
        public void RevokeExternal() { foreach (var pair in grants) if (!pair.Value.Native) grants.TryRemove(pair.Key, out _); }
        void Sweep() { foreach (var pair in grants) if (pair.Value.ExpiresAt <= DateTimeOffset.UtcNow) grants.TryRemove(pair.Key, out _); }
    }
}
