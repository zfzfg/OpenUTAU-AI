using System;
using System.Collections.Generic;

namespace OpenUtau.Core.Automation {
    /// <summary>Object identities remain stable through undo, independently of mutable positions and UPart.Id.</summary>
    public sealed class AutomationIdentityRegistry {
        readonly Dictionary<object, string> ids = new(ReferenceEqualityComparer.Instance);
        public string GetId(object value) => GetId(value, value switch { Ustx.UTrack => "track", Ustx.UPart => "part", Ustx.UNote => "note", _ => "entity" });
        public string GetId(object value, string kind) {
            if (!ids.TryGetValue(value, out var id)) ids.Add(value, id = kind + "_" + Guid.NewGuid().ToString("N"));
            return id;
        }
        public void Reset() => ids.Clear();
    }
}
