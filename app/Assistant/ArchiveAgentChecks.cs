using System.Text;
using System.Text.Json.Nodes;

namespace StudentProgress.Assistant;

public static class ArchiveAgentChecks
{
    public static int Run(string dataRoot)
    {
        var passed = new List<string>();
        try
        {
            if (Directory.Exists(dataRoot) && Directory.EnumerateFileSystemEntries(dataRoot).Any()) throw new InvalidOperationException("Archive checks require an empty isolated directory.");
            Directory.CreateDirectory(dataRoot);
            void Check(bool ok, string name) { if (!ok) throw new InvalidOperationException(name); passed.Add(name); }
            var connection = new JsonObject { ["model"] = "model", ["key"] = "test-only-secret", ["baseUrl"] = "https://example.invalid/v1", ["apiMode"] = "responses" };
            var normal = CodexRunner.CreateStartInfo("not-launched.exe", dataRoot, connection);
            var archive = CodexRunner.CreateStartInfo("not-launched.exe", dataRoot, connection, archiveMode: true);
            string[] args = archive.ArgumentList.ToArray();
            bool Disabled(string feature) => args.Select((value, index) => value == feature && index > 0 && args[index - 1] == "--disable").Any(x => x);
            Check(new[] { "shell_tool", "unified_exec", "view_image", "workspace_dependencies", "code_mode", "code_mode_host" }.All(feature => !Disabled(feature)) && args.Contains("workspace-write") && !string.Join("\n", args).Contains("mcp_servers") && !string.Join("\n", args).Contains("test-only-secret") && normal.ArgumentList.Contains("read-only"), "archive_uses_native_codex_tools_and_isolated_workspace_without_custom_mcp");
            const string command = "{\"type\":\"item.completed\",\"item\":{\"type\":\"command_execution\"}}";
            var regularEvents = new CodexEvents(); regularEvents.Accept(command);
            var archiveEvents = new CodexEvents(true); archiveEvents.Accept(command);
            Check(regularEvents.UnexpectedTool && !archiveEvents.UnexpectedTool && archiveEvents.ArchiveToolCalls == 1, "native_archive_tool_count_does_not_change_generic_chat_boundary");
            string originalRoot = Path.Combine(dataRoot, "source-attachments"), workspace = Path.Combine(dataRoot, "workspace"); Directory.CreateDirectory(originalRoot);
            string body = new string('x', 18000) + "ORIGINAL_TAIL"; byte[] bytes = Encoding.UTF8.GetBytes(body);
            File.WriteAllBytes(Path.Combine(originalRoot, "test.txt"), bytes);
            var messages = new JsonArray();
            for (int i = 0; i < 12; i++) messages.Add(new JsonObject { ["id"] = "message-" + i, ["role"] = "user", ["text"] = "history " + i, ["attachments"] = new JsonArray() });
            ((JsonArray)messages[0]!["attachments"]!).Add(new JsonObject { ["id"] = "file-1", ["name"] = "资料.txt", ["storedName"] = "test.txt", ["extractedText"] = "old preview" });
            messages.Add(new JsonObject { ["id"] = "assistant", ["role"] = "assistant", ["text"] = "earlier answer", ["attachments"] = new JsonArray() });
            var tasks = new JsonArray(new JsonObject { ["id"] = "task", ["title"] = "existing work", ["done"] = true });
            string before = messages.ToJsonString();
            AppService.WriteArchiveWorkspaceAsync(originalRoot, new JsonObject { ["name"] = "Example member" }, tasks, messages, workspace, CancellationToken.None).GetAwaiter().GetResult();
            var prepared = JsonNode.Parse(File.ReadAllText(Path.Combine(workspace, "archive.json")))!.AsObject();
            var attachment = prepared["messages"]![0]!["attachments"]![0]!;
            Check(((JsonArray)prepared["messages"]!).Count == 13 && prepared["messages"]![12]!["role"]!.GetValue<string>() == "assistant" && prepared["tasks"]![0]!["done"]!.GetValue<bool>() && before == messages.ToJsonString(), "all_archive_history_and_tasks_copied_without_mutating_source_snapshot");
            Check(File.ReadAllBytes(Path.Combine(workspace, attachment["file"]!.GetValue<string>())).SequenceEqual(bytes) && File.ReadAllText(Path.Combine(workspace, attachment["extractedTextFile"]!.GetValue<string>())).Contains("ORIGINAL_TAIL"), "original_attachment_and_full_text_sidecar_keep_content_beyond_old_preview");
            Check(!prepared.ToJsonString().Contains(originalRoot) && !prepared.ToJsonString().Contains("storedName") && !prepared.ContainsKey("settings") && !prepared.ToJsonString().Contains("test-only-secret"), "agent_materials_do_not_include_live_data_paths_or_api_settings");
            AssistantJson.WriteAtomic(Path.Combine(dataRoot, "archive-agent-result.json"), new { ok = true, checks = passed, realModelRequests = 0 }); return 0;
        }
        catch (Exception error)
        {
            Directory.CreateDirectory(dataRoot); AssistantJson.WriteAtomic(Path.Combine(dataRoot, "archive-agent-result.json"), new { ok = false, checks = passed, error = error.ToString(), realModelRequests = 0 }); return 1;
        }
    }
}
