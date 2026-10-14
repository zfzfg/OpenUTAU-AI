using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace OpenUtau.Core.Automation {
    public interface IAutomationDispatcher {
        Task<T> InvokeAsync<T>(Func<T> action, CancellationToken cancellationToken = default);
        /// <summary>Neutral journal cleanup remains possible after a scoped request is revoked.</summary>
        Task<T> InvokeMaintenanceAsync<T>(Func<T> action) => InvokeAsync(action, CancellationToken.None);
    }

    public sealed class AutomationException : Exception {
        public string Code { get; }
        public object? Details { get; }
        public AutomationException(string code, string message, object? details = null) : base(message) {
            Code = code; Details = details;
        }
    }

    public sealed record AutomationNoteDto(string NoteId, int PositionTicks, int DurationTicks,
        int Tone, int TuningCents, string Lyric, string? PhoneticHint, object Pitch,
        object Vibrato, object[] Overrides, object[] Expressions);

    internal static class Arguments {
        internal static AutomationException Invalid(string message) => new("INVALID_ARGUMENT", message);
        internal static string String(this JsonElement value, string name, string? fallback = null) {
            if (!value.TryGetProperty(name, out var field)) return fallback ?? throw Invalid($"Missing {name}.");
            if (field.ValueKind != JsonValueKind.String) throw Invalid($"{name} must be a string.");
            return field.GetString()!;
        }
        internal static int Int(this JsonElement value, string name, int? fallback = null) {
            if (!value.TryGetProperty(name, out var field)) return fallback ?? throw Invalid($"Missing {name}.");
            if (!field.TryGetInt32(out int number)) throw Invalid($"{name} must be an integer.");
            return number;
        }
        internal static long Long(this JsonElement value, string name) {
            if (!value.TryGetProperty(name, out var field) || !field.TryGetInt64(out long number)) throw Invalid($"{name} must be an integer.");
            return number;
        }
        internal static double Number(this JsonElement value, string name, double? fallback = null) {
            if (!value.TryGetProperty(name, out var field)) return fallback ?? throw Invalid($"Missing {name}.");
            if (!field.TryGetDouble(out double number) || !double.IsFinite(number)) throw Invalid($"{name} must be finite.");
            return number;
        }
        internal static bool Bool(this JsonElement value, string name, bool fallback = false) {
            if (!value.TryGetProperty(name, out var field)) return fallback;
            if (field.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) throw Invalid($"{name} must be boolean.");
            return field.GetBoolean();
        }
        internal static JsonElement[] Array(this JsonElement value, string name) {
            if (!value.TryGetProperty(name, out var field) || field.ValueKind != JsonValueKind.Array) throw Invalid($"{name} must be an array.");
            return System.Linq.Enumerable.ToArray(field.EnumerateArray());
        }
        internal static int Nonnegative(int value, string name) => value >= 0 ? value : throw Invalid($"{name} must be nonnegative.");
        internal static double Range(double value, double min, double max, string name) => value >= min && value <= max ? value : throw Invalid($"{name} must be between {min} and {max}.");
    }
}
