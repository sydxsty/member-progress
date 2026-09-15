using System.Text.Json.Nodes;

namespace StudentProgress;

public sealed partial class AppService
{
    // Deleted records remain local so Undo and backup restore retain the original files.
    // Only messages is used by visible archives, mobile projections and GPT context.
    static IEnumerable<JsonObject> AllArchiveMessages(JsonObject source) =>
        ((JsonArray)source["messages"]!).OfType<JsonObject>()
        .Concat((source["deletedMessages"] as JsonArray ?? new()).OfType<JsonObject>()
            .Select(x => x["message"]).OfType<JsonObject>());

    JsonObject ArchiveScope(string type, string id) => type switch
    {
        "student" => Rows("students").Concat(Rows("archivedStudents")).FirstOrDefault(x => S(x, "id") == id)
            ?? throw new InvalidOperationException("归档成员已不存在。"),
        "project" => Find("projects", id),
        _ => throw new InvalidOperationException("归档对象无效。")
    };

    (string Type, string Id) DeleteArchiveMessage(string id)
    {
        var message = Find("messages", id);
        var type = S(message, "scopeType"); var scopeId = S(message, "scopeId");
        var scope = ArchiveScope(type, scopeId);
        var messages = Arr("messages"); var index = messages.IndexOf(message);
        var deleted = new JsonObject
        {
            ["id"] = id, ["deletedAt"] = Now(), ["messageIndex"] = index,
            ["beforeMessageId"] = index > 0 ? S((JsonObject)messages[index - 1]!, "id") : "",
            ["afterMessageId"] = index + 1 < messages.Count ? S((JsonObject)messages[index + 1]!, "id") : ""
        };
        messages.Remove(message);
        deleted["message"] = message;
        Arr("deletedMessages").Add(deleted);
        ReconcileArchiveSummaryAfterDelete(scope, deleted);
        scope["summaryStatus"] = "idle"; scope["summaryError"] = "";
        return (type, scopeId);
    }

    (string Type, string Id)? RestoreArchiveMessage(string id)
    {
        var deleted = Rows("deletedMessages").FirstOrDefault(x => S(x, "id") == id);
        if (deleted == null)
        {
            if (Rows("messages").Any(x => S(x, "id") == id)) return null;
            throw new InvalidOperationException("这条归档已无法撤销，请刷新后重试。");
        }
        var message = (JsonObject)deleted["message"]!;
        var type = S(message, "scopeType"); var scopeId = S(message, "scopeId");
        var scope = ArchiveScope(type, scopeId);
        var messages = Arr("messages");
        var after = Rows("messages").FirstOrDefault(x => S(x, "id") == S(deleted, "afterMessageId"));
        var before = Rows("messages").FirstOrDefault(x => S(x, "id") == S(deleted, "beforeMessageId"));
        var index = after != null ? messages.IndexOf(after) : before != null ? messages.IndexOf(before) + 1
            : Math.Clamp(I(deleted, "messageIndex"), 0, messages.Count);
        messages.Insert(index, message.DeepClone());
        RestoreArchiveSummary(scope, deleted);
        Arr("deletedMessages").Remove(deleted);
        scope["summaryStatus"] = "idle"; scope["summaryError"] = "";
        return (type, scopeId);
    }

    void ReconcileArchiveSummaryAfterDelete(JsonObject scope, JsonObject deleted)
    {
        var message = (JsonObject)deleted["message"]!;
        var currentId = S(scope, "currentSummaryMessageId");
        if (currentId.Length == 0) return; // Old records cannot prove whether a person has edited the text.
        var current = currentId == S(message, "id") ? message : Rows("messages").FirstOrDefault(x => S(x, "id") == currentId);
        if (current == null || !current.ContainsKey("generatedCurrentTask") || S(scope, "currentSummary") != S(current, "generatedCurrentTask")) return;
        var sources = (current["sourceMessageIds"] as JsonArray ?? new()).OfType<JsonValue>().Select(x => x.GetValue<string>());
        if (currentId != S(message, "id") && !sources.Contains(S(message, "id"), StringComparer.Ordinal)) return;
        var before = SummaryState(scope);
        var previous = Rows("messages").LastOrDefault(x => S(x, "role") == "assistant"
            && S(x, "scopeType") == S(message, "scopeType") && S(x, "scopeId") == S(message, "scopeId")
            && x.ContainsKey("generatedCurrentTask") && SummarySourcesExist(x));
        scope["currentSummary"] = previous == null ? "" : S(previous, "generatedCurrentTask");
        if (previous == null) scope.Remove("currentSummaryMessageId");
        else scope["currentSummaryMessageId"] = S(previous, "id");
        scope["currentSummaryRevision"] = I(scope, "currentSummaryRevision") + 1;
        deleted["summaryBefore"] = before;
        deleted["summaryAfter"] = SummaryState(scope);
    }

    void RestoreArchiveSummary(JsonObject scope, JsonObject deleted)
    {
        if (deleted["summaryBefore"] is not JsonObject before || deleted["summaryAfter"] is not JsonObject after
            || !JsonNode.DeepEquals(SummaryState(scope), after)) return;
        var source = Rows("messages").FirstOrDefault(x => S(x, "id") == S(before, "currentSummaryMessageId"));
        if (source == null || !SummarySourcesExist(source)) return;
        scope["currentSummary"] = S(before, "currentSummary");
        scope["currentSummaryMessageId"] = S(before, "currentSummaryMessageId");
        scope["currentSummaryRevision"] = I(scope, "currentSummaryRevision") + 1;
    }

    bool SummarySourcesExist(JsonObject message)
    {
        if (message["sourceMessageIds"] is not JsonArray sources) return false;
        return sources.All(x => x is JsonValue value && value.TryGetValue<string>(out var id)
            && Rows("messages").Any(m => S(m, "id") == id && S(m, "role") == "user"
                && S(m, "scopeType") == S(message, "scopeType") && S(m, "scopeId") == S(message, "scopeId")));
    }

    static JsonObject SummaryState(JsonObject scope) => new()
    {
        ["currentSummary"] = S(scope, "currentSummary"),
        ["currentSummaryMessageId"] = S(scope, "currentSummaryMessageId"),
        ["currentSummaryRevision"] = I(scope, "currentSummaryRevision")
    };
}
