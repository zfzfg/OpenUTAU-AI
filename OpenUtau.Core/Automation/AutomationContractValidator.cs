using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Json.Schema;

namespace OpenUtau.Core.Automation {
    /// <summary>Uses the same JSON Schemas as MCP, without exposing schema-library types outside Core.</summary>
    public sealed class AutomationContractValidator {
        readonly Dictionary<string, JsonSchema> schemas;
        public AutomationContractValidator(string toolsJson) {
            using var document = JsonDocument.Parse(toolsJson);
            var root = document.RootElement;
            var tools = root.ValueKind == JsonValueKind.Array ? root : root.GetProperty("tools");
            schemas = tools.EnumerateArray().ToDictionary(tool => tool.GetProperty("name").GetString()!, tool => JsonSchema.FromText(tool.GetProperty("inputSchema").GetRawText()));
        }
        public void ValidateToolArguments(string operation, JsonElement arguments) {
            string name = operation.StartsWith("openutau_", StringComparison.Ordinal) ? operation : "openutau_" + operation;
            if (!schemas.TryGetValue(name, out var schema)) throw new AutomationException("UNSUPPORTED_CAPABILITY", "Unknown tool: " + name);
            if (!schema.Evaluate(arguments).IsValid) throw new AutomationException("INVALID_ARGUMENT", "Arguments do not match the schema of " + name + ".");
        }
        public static void Validate(JsonElement schema, JsonElement instance) {
            if (!JsonSchema.FromText(schema.GetRawText()).Evaluate(instance).IsValid) throw new AutomationException("INVALID_ARGUMENT", "Arguments do not match the operation schema.");
        }
    }
}
