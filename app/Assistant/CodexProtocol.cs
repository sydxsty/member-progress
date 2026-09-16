using System.Text.Json.Nodes;

namespace StudentProgress.Assistant;

// Shapes checked against the bundled 0.154.0 app-server --experimental schema.
internal sealed record CodexToolCall(string ThreadId, string TurnId, string CallId, string Name, JsonObject Arguments, string Namespace = "");
internal sealed record CodexToolResult(bool Success, JsonArray ContentItems)
{
    internal static CodexToolResult Text(string text, bool success = true) => new(success,
        new JsonArray(new JsonObject { ["type"] = "inputText", ["text"] = text }));
    internal static CodexToolResult Json(JsonNode value, bool success = true) => Text(value.ToJsonString(), success);
}
internal sealed record CodexProgressEvent(string Kind, JsonObject Data);
internal sealed record CodexTurnResult(string ThreadId, string TurnId, string Answer, JsonObject? TokenUsage);
