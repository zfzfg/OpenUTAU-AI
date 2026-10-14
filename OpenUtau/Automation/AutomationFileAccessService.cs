using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using OpenUtau.Core.Automation;

namespace OpenUtau.Automation {
    public sealed record AutomationFileHandle(string Handle, string Session, string Path, string Purpose, bool Output,
        bool Overwrite, string Fingerprint, DateTimeOffset ExpiresAt);

    public sealed class AutomationFileAccessService {
        readonly HashSet<string> roots = new(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        readonly Dictionary<string, AutomationFileHandle> handles = new();
        string? defaultRoot;
        public const long MaxInputBytes = 512 * 1024 * 1024;
        public void AllowDirectory(string path) { lock (roots) roots.Add(Canonical(path, true)); }
        public void SetDefaultDirectory(string path) { lock (roots) defaultRoot = Canonical(path, true); }
        public string[] Roots { get { lock (roots) return roots.Concat(defaultRoot == null ? Array.Empty<string>() : new[] { defaultRoot }).Distinct().ToArray(); } }
        public object Register(string session, string path, string purpose, bool output, bool overwrite = false) {
            if (!Path.IsPathFullyQualified(path)) throw new AutomationException("INVALID_ARGUMENT", "Provide an absolute file path.");
            if (purpose != "project" && purpose != "midi" && purpose != "audio") throw new AutomationException("INVALID_ARGUMENT", "Unknown file purpose.");
            path = Canonical(path, false);
            DemandPath(path);
            var extension = Path.GetExtension(path).ToLowerInvariant();
            var allowed = purpose switch { "project" => new[] { ".ustx" }, "midi" => new[] { ".mid", ".midi" }, _ => output ? new[] { ".wav" } : new[] { ".wav", ".flac", ".mp3", ".ogg", ".aiff", ".aif" } };
            if (!allowed.Contains(extension)) throw new AutomationException("INVALID_ARGUMENT", "Unsupported file extension for this purpose.");
            if (!output && !File.Exists(path)) throw new AutomationException("INVALID_ARGUMENT", "The input file does not exist.");
            if (File.Exists(path) && new FileInfo(path).Length > MaxInputBytes) throw new AutomationException("LIMIT_EXCEEDED", "Input exceeds the 512 MiB file limit.");
            if (output && File.Exists(path) && !overwrite) throw new AutomationException("FILE_ACCESS_DENIED", "An existing output requires overwrite: true.");
            var handle = new AutomationFileHandle(Guid.NewGuid().ToString("N"), session, path, purpose, output, overwrite, Fingerprint(path), DateTimeOffset.UtcNow.AddMinutes(15));
            lock (handles) {
                foreach (var key in handles.Where(p => p.Value.ExpiresAt <= DateTimeOffset.UtcNow).Select(p => p.Key).ToArray()) handles.Remove(key);
                if (handles.Count >= 256) throw new AutomationException("LIMIT_EXCEEDED", "Too many outstanding file handles.");
                handles.Add(handle.Handle, handle);
            }
            return new { ok = true, fileHandle = output ? null : handle.Handle, outputHandle = output ? handle.Handle : null, path, purpose,
                expiresAt = handle.ExpiresAt, sizeBytes = File.Exists(path) ? new FileInfo(path).Length : 0, overwrite };
        }
        public AutomationFileHandle Resolve(string session, string id, string purpose, bool output) {
            AutomationFileHandle? handle;
            lock (handles) handles.TryGetValue(id, out handle);
            if (handle == null || handle.ExpiresAt <= DateTimeOffset.UtcNow || handle.Session != session)
                throw new AutomationException("FILE_ACCESS_DENIED", "The file handle expired or belongs to another project. Register it again.");
            if (handle.Output != output || handle.Purpose != purpose) throw new AutomationException("FILE_ACCESS_DENIED", "File handle purpose mismatch.");
            if (Canonical(handle.Path, false) != handle.Path) throw new AutomationException("FILE_ACCESS_DENIED", "File path changed after registration.");
            DemandPath(handle.Path);
            if (Fingerprint(handle.Path) != handle.Fingerprint) throw new AutomationException("REVISION_CONFLICT", "The file changed since the handle was created.");
            return handle;
        }
        public void CommitOutput(AutomationFileHandle handle, string temporaryPath) {
            Resolve(handle.Session, handle.Handle, handle.Purpose, true);
            File.Move(temporaryPath, handle.Path, handle.Overwrite);
            lock (handles) handles.Remove(handle.Handle);
        }
        public void Invalidate() { lock (handles) handles.Clear(); }
        public string ValidateInputPath(string path, string purpose) {
            path = Canonical(path, false);
            DemandPath(path);
            var allowed = purpose == "audio" ? new[] { ".wav", ".flac", ".mp3", ".ogg", ".aiff", ".aif" } : new[] { ".ustx" };
            if (!allowed.Contains(Path.GetExtension(path).ToLowerInvariant()) || !File.Exists(path) || new FileInfo(path).Length > MaxInputBytes)
                throw new AutomationException("FILE_ACCESS_DENIED", "The referenced input is unavailable or exceeds the allowed file limit.");
            return path;
        }
        void DemandPath(string path) {
            lock (roots) {
                if (!Roots.Any(root => path.StartsWith(Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar,
                    OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)))
                    throw new AutomationException("FILE_ACCESS_DENIED", "Release this folder in the assistant's file access settings.");
            }
        }
        public static string Canonical(string path, bool directory) {
            var full = Path.GetFullPath(path);
            var root = Path.GetPathRoot(full)!;
            var relative = Path.GetRelativePath(root, full);
            var current = root;
            var pieces = relative.Split(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar }, StringSplitOptions.RemoveEmptyEntries);
            for (var i = 0; i < pieces.Length; ++i) {
                current = Path.Combine(current, pieces[i]);
                FileSystemInfo info = Directory.Exists(current) ? new DirectoryInfo(current) : new FileInfo(current);
                if (info.Exists && (info.Attributes & FileAttributes.ReparsePoint) != 0) {
                    var target = info.ResolveLinkTarget(true) ?? throw new AutomationException("FILE_ACCESS_DENIED", "Cannot resolve linked file path.");
                    current = target.FullName;
                }
                if (i < pieces.Length - 1 && !Directory.Exists(current)) throw new AutomationException("FILE_ACCESS_DENIED", "The parent directory does not exist.");
            }
            if (directory && !Directory.Exists(current)) throw new AutomationException("FILE_ACCESS_DENIED", "Directory does not exist.");
            return Path.GetFullPath(current);
        }
        static string Fingerprint(string path) {
            if (!File.Exists(path)) return "absent";
            using var stream = File.OpenRead(path);
            return Convert.ToHexString(SHA256.HashData(stream));
        }
    }
}
