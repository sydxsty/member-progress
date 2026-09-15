using System.Text.Json.Nodes;

namespace StudentProgress.Assistant;

internal sealed class CodexEvents
{
    readonly bool allowArchiveTools;
    internal CodexEvents(bool allowArchiveTools = false) { this.allowArchiveTools = allowArchiveTools; }
    internal string Answer { get; private set; } = "";
    internal string Error { get; private set; } = "";
    internal bool Completed { get; private set; }
    internal bool Failed { get; private set; }
    internal bool UnexpectedTool { get; private set; }
    internal int ArchiveToolCalls { get; private set; }
    internal void Accept(string line)
    {
        if (string.IsNullOrWhiteSpace(line)) return;
        JsonObject? item;
        try { item = JsonNode.Parse(line) as JsonObject; }
        catch (System.Text.Json.JsonException) { return; }
        if (item == null) return;
        string type = AssistantJson.Text(item["type"]);
        if (type is "item.completed" or "item.started")
        {
            var content = item["item"] as JsonObject;
            string itemType = AssistantJson.Text(content?["type"]);
            if (itemType is "mcp_tool_call" or "web_search" or "collab_tool_call") UnexpectedTool = true;
            if (itemType is "command_execution" or "file_change")
            {
                if (!allowArchiveTools) UnexpectedTool = true;
                else if (type == "item.completed") ArchiveToolCalls++;
            }
            if (type == "item.completed" && itemType == "agent_message")
            {
                var text = AssistantJson.Text(content?["text"]);
                if (!string.IsNullOrWhiteSpace(text)) Answer = text;
            }
        }
        else if (type == "turn.completed") Completed = true;
        else if (type is "turn.failed" or "error")
        {
            Failed = true;
            Error = AssistantJson.Text(item["error"]?["message"]);
            if (string.IsNullOrWhiteSpace(Error)) Error = AssistantJson.Text(item["message"]);
        }
    }
}
