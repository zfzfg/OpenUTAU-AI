using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;

namespace OpenUtau.Core.Automation {
    public sealed class AutomationOperationJournal {
        internal sealed class Entry {
            internal string Id = "", Hash = "", Session = "", Status = "queued";
            internal object? Result, UndoGroup;
            internal long Revision;
            internal DateTime Created = DateTime.UtcNow;
        }
        readonly Dictionary<string, Entry> entries = new(StringComparer.Ordinal);
        internal Entry? Find(string id) => entries.GetValueOrDefault(id);
        internal Entry Register(string id, JsonElement payload, string session) {
            if (string.IsNullOrWhiteSpace(id) || id.Length > 128) throw Arguments.Invalid("operationId must contain 1–128 characters.");
            string hash = Hash(payload);
            if (entries.TryGetValue(id, out var previous)) {
                if (previous.Hash != hash || previous.Session != session) throw new AutomationException("IDEMPOTENCY_CONFLICT", "This operationId was already used for another payload or project.");
                return previous;
            }
            if (entries.Count >= 4096) throw new AutomationException("LIMIT_EXCEEDED", "The operation journal is full for this editor session. Restart the editor after resolving pending operations.");
            var entry = new Entry { Id = id, Hash = hash, Session = session };
            entries.Add(id, entry); return entry;
        }
        public object Get(string id) {
            var entry = Find(id) ?? throw new AutomationException("OPERATION_STATE_UNKNOWN", "No operation with this ID is recorded in this editor process.");
            return new { operationId = entry.Id, status = entry.Status, projectSessionId = entry.Session,
                editRevision = entry.Revision, result = entry.Result, createdAt = entry.Created };
        }
        internal static string Hash(JsonElement value) {
            using var stream = new MemoryStream();
            using (var writer = new Utf8JsonWriter(stream)) Canonical(writer, value);
            return Convert.ToHexString(SHA256.HashData(stream.ToArray()));
        }
        static void Canonical(Utf8JsonWriter writer, JsonElement value) {
            if (value.ValueKind == JsonValueKind.Object) {
                writer.WriteStartObject();
                foreach (var p in value.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal)) { writer.WritePropertyName(p.Name); Canonical(writer, p.Value); }
                writer.WriteEndObject();
            } else if (value.ValueKind == JsonValueKind.Array) {
                writer.WriteStartArray(); foreach (var item in value.EnumerateArray()) Canonical(writer, item); writer.WriteEndArray();
            } else value.WriteTo(writer);
        }
    }
}
