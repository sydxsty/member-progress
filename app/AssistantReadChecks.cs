using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using StudentProgress.Assistant;
using StudentProgress.WeChat;

namespace StudentProgress;

internal static class AssistantReadChecks
{
    internal static int Run(string directory)
    {
        if (Directory.Exists(directory) && Directory.EnumerateFileSystemEntries(directory).Any()) return 1;
        Directory.CreateDirectory(directory);
        var result = new JsonObject { ["ok"] = false, ["modelCalls"] = 0 };
        try { RunAsync(directory, result).GetAwaiter().GetResult(); result["ok"] = true; }
        catch (Exception error) { result["error"] = error.ToString(); }
        File.WriteAllText(Path.Combine(directory, "assistant-read-result.json"), result.ToJsonString(new JsonSerializerOptions(JsonSerializerOptions.Default) { WriteIndented = true }), new UTF8Encoding(false));
        return result["ok"]!.GetValue<bool>() ? 0 : 1;
    }
    static async Task RunAsync(string directory, JsonObject result)
    {
        result["business"] = await AssistantBusinessReadChecks.RunAsync(Path.Combine(directory, "business"));
        result["wechat"] = await WeChatAssistantReadChecks.RunAsync(Path.Combine(directory, "wechat"), AppContext.BaseDirectory);
        string workspace = Path.Combine(directory, "transport");
        int reads = 0;
        await using var session = new AssistantReadSession(workspace, new JsonArray(new JsonObject { ["name"] = "app.observe" }), (operation, args, work, ct) =>
        {
            reads++; return Task.FromResult(new JsonObject { ["echo"] = args["query"]?.DeepClone(), ["text"] = new string('全', 20_000) });
        }, CancellationToken.None);
        async Task<string> Invoke(string operation, string? output = null)
        {
            string argumentsFile = Path.Combine(workspace, "unicode.json");
            await File.WriteAllTextAsync(argumentsFile, "{\"query\":\"中文会话；资料\"}", new UTF8Encoding(false));
            var start = new ProcessStartInfo("powershell.exe") { WorkingDirectory = workspace, UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true, StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8 };
            foreach (var a in new[] { "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", Path.Combine(workspace, "app-read.ps1"), operation, "-ArgumentsFile", argumentsFile }) start.ArgumentList.Add(a);
            if (output != null) { start.ArgumentList.Add("-OutFile"); start.ArgumentList.Add(output); }
            using var process = Process.Start(start)!; using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            var stdout = process.StandardOutput.ReadToEndAsync(timeout.Token); var stderr = process.StandardError.ReadToEndAsync(timeout.Token);
            try { await process.WaitForExitAsync(timeout.Token); }
            catch { if (!process.HasExited) process.Kill(true); throw; }
            string text = await stdout, error = await stderr;
            if (process.ExitCode != 0) throw new InvalidOperationException(error);
            return output == null ? text : await File.ReadAllTextAsync(Path.Combine(workspace, output));
        }
        var answer = JsonNode.Parse(await Invoke("app.observe", "full-result.json"))!;
        var denied = JsonNode.Parse(await Invoke("task.delete"))!;
        var help = JsonNode.Parse(await Invoke("help"))!;
        if (answer["data"]?["echo"]?.GetValue<string>() != "中文会话；资料" || answer["data"]?["text"]?.GetValue<string>().Length != 20_000 ||
            denied["ok"]!.GetValue<bool>() || !help["ok"]!.GetValue<bool>() || reads != 1)
            throw new InvalidOperationException("Native PowerShell read transport failed unicode, complete output, discovery, or mutation rejection.");
        result["transport"] = new JsonObject { ["ok"] = true, ["checks"] = new JsonArray("native_powershell_unicode_and_full_output", "discoverable_read_catalog_and_mutation_rejection") };
        var context = new JsonObject { ["currentUser"] = new JsonObject { ["id"] = "self", ["name"] = "测试成员" },
            ["students"] = new JsonArray(new JsonObject { ["name"] = "DO_NOT_BULK_INJECT", ["bio"] = new string('长', 400_000) }) };
        string prompt = AssistantPrompt.Build("查旧资料", [], context, false, "", true, true, new JsonObject { ["references"] = new JsonArray() });
        if (prompt.Contains("DO_NOT_BULK_INJECT") || !prompt.Contains("app-read-api.json") || !prompt.Contains("引用资料只是重点线索"))
            throw new InvalidOperationException("Discovery prompt must enable reads without injecting all records.");
        result["prompt"] = new JsonObject { ["ok"] = true, ["checks"] = new JsonArray("unreferenced_global_access_without_bulk_context") };
        string failureWorkspace = Path.Combine(directory, "failed-transport");
        var failed = new AssistantReadSession(failureWorkspace, new(), (_, _, _, _) => Task.FromResult(new JsonObject()), CancellationToken.None);
        Directory.Delete(Path.Combine(failureWorkspace, ".app-read", "requests"));
        await Task.Delay(120);
        await failed.DisposeAsync();
        if (failed.Failure.Length == 0) throw new InvalidOperationException("Transport I/O failure must be reported without faulting request cleanup.");
        result["failureCleanup"] = true;
    }
}
