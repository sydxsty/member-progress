using System.Text.Json.Nodes;

namespace StudentProgress.Assistant;

internal sealed class CodexEvents
{
    readonly bool allowArchiveTools;
    readonly Action<CodexProgressEvent>? progress;
    readonly Dictionary<string, string> phases = new(StringComparer.Ordinal);
    readonly Dictionary<string, string> messages = new(StringComparer.Ordinal);
    readonly Dictionary<string, string> plans = new(StringComparer.Ordinal);
    readonly HashSet<string> completedTools = new(StringComparer.Ordinal);
    internal CodexEvents(bool allowArchiveTools = false, Action<CodexProgressEvent>? progress = null)
    { this.allowArchiveTools = allowArchiveTools; this.progress = progress; }
    internal string Answer { get; private set; } = "";
    internal string Error { get; private set; } = "";
    internal bool Completed { get; private set; }
    internal bool Failed { get; private set; }
    internal bool UnexpectedTool { get; private set; }
    internal int ArchiveToolCalls { get; private set; }
    internal string ThreadId { get; private set; } = "";
    internal string TurnId { get; private set; } = "";
    internal string Status { get; private set; } = "";
    internal JsonObject? TokenUsage { get; private set; }
    internal void SetThread(string id)
    {
        if (id.Length == 0 || ThreadId == id) return;
        ThreadId = id; Emit("thread.started", new JsonObject { ["threadId"] = id });
    }
    internal void SetTurn(string id)
    {
        if (id.Length == 0 || TurnId == id) return;
        TurnId = id; Emit("turn.started", new JsonObject { ["turnId"] = id });
    }
    void Emit(string kind, JsonObject data)
    {
        data["threadId"] ??= ThreadId; data["turnId"] ??= TurnId;
        progress?.Invoke(new(kind, data));
    }
    internal void AcceptNotification(string method, JsonObject data)
    {
        if (method == "thread/started") { SetThread(AssistantJson.Text(data["thread"]?["id"])); return; }
        string thread = AssistantJson.Text(data["threadId"]);
        if (thread.Length > 0 && ThreadId.Length > 0 && thread != ThreadId) return;
        if (method == "turn/started") { SetTurn(AssistantJson.Text(data["turn"]?["id"])); return; }
        if (method == "item/reasoning/textDelta") return; // Never expose private/raw reasoning.
        string itemId = AssistantJson.Text(data["itemId"]);
        if (method == "item/agentMessage/delta")
        {
            string delta = AssistantJson.Text(data["delta"]);
            messages[itemId] = messages.GetValueOrDefault(itemId, "") + delta;
            Emit("message.delta", new JsonObject { ["itemId"] = itemId, ["delta"] = delta, ["phase"] = phases.GetValueOrDefault(itemId, "") });
        }
        else if (method is "item/reasoning/summaryTextDelta" or "item/reasoning/summaryPartAdded")
        {
            var visible = Fields(data, "itemId", "summaryIndex", "delta");
            Emit(method.EndsWith("summaryTextDelta", StringComparison.Ordinal) ? "summary.delta" : "summary.part", visible);
        }
        else if (method == "thread/tokenUsage/updated")
        {
            TokenUsage = data["tokenUsage"] is JsonObject usage ? NormalizeUsage(usage) : null;
            if (TokenUsage != null) Emit("usage.updated", TokenUsage.DeepClone().AsObject());
        }
        else if (method is "turn/plan/updated" or "item/plan/delta")
        {
            var plan = Fields(data, "itemId", "delta", "explanation", "plan");
            if (method == "item/plan/delta")
            { plans[itemId] = plans.GetValueOrDefault(itemId, "") + AssistantJson.Text(data["delta"]); plan["text"] = plans[itemId]; }
            else plan["text"] = string.Join("\n", (data["plan"] as JsonArray ?? new()).OfType<JsonObject>().Select(p => AssistantJson.Text(p["step"]) + " · " + AssistantJson.Text(p["status"])));
            Emit("plan.updated", plan);
        }
        else if (method == "item/commandExecution/outputDelta") Emit("tool.output", Fields(data, "itemId", "delta"));
        else if (method is "item/started" or "item/completed")
        {
            if (data["item"] is JsonObject item) AcceptItem(item, method == "item/completed");
        }
        else if (method == "turn/completed")
        {
            var turn = data["turn"] as JsonObject ?? new(); SetTurn(AssistantJson.Text(turn["id"]));
            // Some providers only supply items with the completed turn. Do not stream them twice.
            foreach (var item in (turn["items"] as JsonArray ?? new()).OfType<JsonObject>())
                if (AssistantJson.Text(item["type"]) == "agentMessage") FinishMessage(item, emit: false);
            Status = AssistantJson.Text(turn["status"]); Completed = true; Failed = Status == "failed";
            Error = AssistantJson.Text(turn["error"]?["message"]);
            Emit("turn.completed", new JsonObject { ["status"] = Status, ["error"] = Error });
        }
        else if (method == "error")
        {
            Error = AssistantJson.Text(data["error"]?["message"]);
            Emit("error", Fields(data, "error", "willRetry"));
        }
        else if (method == "thread/compacted") Emit("context.compacted", new());
    }
    void AcceptItem(JsonObject item, bool completed)
    {
        string type = AssistantJson.Text(item["type"]), id = AssistantJson.Text(item["id"]);
        if (type == "agentMessage")
        {
            phases[id] = AssistantJson.Text(item["phase"]);
            if (completed) FinishMessage(item, emit: true);
            else Emit("message.started", new JsonObject { ["itemId"] = id, ["phase"] = phases[id] });
            return;
        }
        if (type == "reasoning")
        {
            if (completed) Emit("summary.completed", new JsonObject { ["itemId"] = id, ["summary"] = item["summary"]?.DeepClone() ?? new JsonArray() });
            return;
        }
        if (type == "plan") { if (completed) Emit("plan.updated", new JsonObject { ["itemId"] = id, ["text"] = item["text"]?.DeepClone() }); return; }
        if (type == "contextCompaction") { Emit(completed ? "context.compacted" : "context.compacting", new()); return; }
        if (type is "mcpToolCall" or "webSearch" or "collabAgentToolCall") UnexpectedTool = true;
        if (type is "commandExecution" or "fileChange" or "imageView" && !allowArchiveTools) UnexpectedTool = true;
        if (type is "commandExecution" or "fileChange" or "imageView" or "dynamicToolCall")
        {
            if (completed && completedTools.Add(id)) ArchiveToolCalls++;
            var visible = Fields(item, "id", "type", "tool", "namespace", "status", "command", "cwd", "durationMs", "exitCode", "success", "path");
            visible["itemId"] = id;
            string name = AssistantJson.Text(item["tool"]);
            visible["name"] = name.Length > 0 ? name : type;
            visible["title"] = name.Length > 0 ? name : type switch { "commandExecution" => "运行命令", "imageView" => "查看图片", "fileChange" => "整理资料", _ => type };
            visible["text"] = type == "commandExecution" ? AssistantJson.Text(item["command"]) : AssistantJson.Text(item["path"]);
            visible["status"] ??= completed ? "completed" : "inProgress";
            Emit(completed ? "tool.completed" : "tool.started", visible);
        }
    }
    void FinishMessage(JsonObject item, bool emit)
    {
        string id = AssistantJson.Text(item["id"]), phase = AssistantJson.Text(item["phase"]), text = AssistantJson.Text(item["text"]);
        if (text.Length == 0) text = messages.GetValueOrDefault(id, "");
        messages[id] = text; phases[id] = phase;
        if (phase != "commentary" && text.Length > 0) Answer = text;
        if (emit) Emit("message.completed", new JsonObject { ["itemId"] = id, ["phase"] = phase, ["text"] = text });
    }
    static JsonObject Fields(JsonObject source, params string[] names)
    {
        var output = new JsonObject();
        foreach (string name in names) if (source.TryGetPropertyValue(name, out var value)) output[name] = value?.DeepClone();
        return output;
    }
    internal static JsonObject NormalizeUsage(JsonObject native)
    {
        var last = native["last"] as JsonObject ?? new();
        var output = Fields(last, "inputTokens", "cachedInputTokens", "cacheWriteInputTokens", "outputTokens", "reasoningOutputTokens", "totalTokens");
        output["contextTokens"] = last["totalTokens"]?.DeepClone();
        output["contextWindow"] = native["modelContextWindow"]?.DeepClone();
        output["cumulative"] = native["total"]?.DeepClone();
        output["percent"] = null;
        if (last["totalTokens"] is JsonValue count && count.TryGetValue<long>(out long tokens) &&
            native["modelContextWindow"] is JsonValue window && window.TryGetValue<long>(out long limit) && limit > 0)
            output["percent"] = Math.Clamp((double)tokens / limit * 100, 0, 100);
        return output;
    }
}
