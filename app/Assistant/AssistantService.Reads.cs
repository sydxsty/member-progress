using System.Text.Json.Nodes;

namespace StudentProgress.Assistant;

public sealed partial class AssistantService
{
    public Func<string, JsonObject, string, CancellationToken, Task<JsonObject>>? ReadDataAsync { get; set; }
    public JsonArray ReadApiDescription { get; set; } = new();

    internal JsonObject ReadConversation(JsonObject args)
    {
        lock (sync)
        {
            int offset = args["offset"] is JsonValue o && o.TryGetValue<int>(out var n) ? Math.Max(0, n) : 0;
            int limit = args["limit"] is JsonValue l && l.TryGetValue<int>(out var m) ? Math.Clamp(m, 1, 100) : 30;
            string query = AssistantJson.Text(args["query"]);
            var matches = state.Messages.Where(m => query.Length == 0 || m.Text.Contains(query, StringComparison.OrdinalIgnoreCase)).ToList();
            var items = new JsonArray(matches.Skip(offset).Take(limit).Select(m => (JsonNode)new JsonObject
            {
                ["id"] = m.Id, ["role"] = m.Role, ["text"] = m.Text,
                ["createdAt"] = System.Text.Json.JsonSerializer.SerializeToNode(m.CreatedAt, AssistantJson.Options),
                ["references"] = System.Text.Json.JsonSerializer.SerializeToNode(m.References, AssistantJson.Options),
                ["attachments"] = System.Text.Json.JsonSerializer.SerializeToNode(m.Attachments, AssistantJson.Options)
            }).ToArray());
            foreach(var row in items.OfType<JsonObject>())SanitizeAttachmentMessage(row);
            bool more = offset + items.Count < matches.Count;
            return new JsonObject { ["items"] = items, ["total"] = matches.Count, ["offset"] = offset, ["limit"] = limit,
                ["hasMore"] = more, ["nextOffset"] = more ? offset + items.Count : null };
        }
    }
}
