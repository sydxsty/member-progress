using System.Drawing.Imaging;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using StudentProgress.Assistant;
using StudentProgress.WeChat;

namespace StudentProgress;

// Opt-in, one real request; all queried records and images are synthetic.
internal static class AssistantReadLiveCheck
{
    internal static int Run(string directory, string connectionFrom)
    {
        directory = Path.GetFullPath(directory); connectionFrom = Path.GetFullPath(connectionFrom);
        if (directory.Equals(connectionFrom, StringComparison.OrdinalIgnoreCase) || directory.StartsWith(connectionFrom + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
            connectionFrom.StartsWith(directory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || Directory.Exists(directory) && Directory.EnumerateFileSystemEntries(directory).Any()) return 1;
        Directory.CreateDirectory(directory);
        var report = new JsonObject { ["ok"] = false, ["modelCalls"] = 0, ["syntheticDataOnly"] = true }; string key = "";
        try
        {
            var settings = JsonNode.Parse(File.ReadAllText(Path.Combine(connectionFrom, "state.json")))!["settings"]!;
            key = Encoding.UTF8.GetString(ProtectedData.Unprotect(File.ReadAllBytes(Path.Combine(connectionFrom, "secret.dat")), null, DataProtectionScope.CurrentUser));
            var connection = new JsonObject { ["key"] = key, ["baseUrl"] = settings["baseUrl"]?.DeepClone(), ["model"] = settings["model"]?.DeepClone(), ["apiMode"] = settings["apiMode"]?.DeepClone() };
            RunAsync(directory, connection, Path.GetDirectoryName(connectionFrom)!, report).GetAwaiter().GetResult(); report["ok"] = true;
        }
        catch (Exception error) { string text = error is InvalidOperationException ? error.Message : error.GetType().Name; report["error"] = key.Length > 0 ? text.Replace(key, "[REDACTED]") : text; }
        File.WriteAllText(Path.Combine(directory, "assistant-read-live-result.json"), report.ToJsonString(new JsonSerializerOptions(JsonSerializerOptions.Default) { WriteIndented = true }), new UTF8Encoding(false));
        return report["ok"]!.GetValue<bool>() ? 0 : 1;
    }
    static async Task RunAsync(string directory, JsonObject connection, string runtimeRoot, JsonObject report)
    {
        string data = Path.Combine(directory, "data"), archiveCode = Guid.NewGuid().ToString("N")[..8].ToUpperInvariant(), imageCode = Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
        Directory.CreateDirectory(Path.Combine(data, "wechat"));
        File.WriteAllText(Path.Combine(data, "initialized"), "synthetic-only");
        File.WriteAllText(Path.Combine(data, "wechat", "config.json"), "{\"enabled\":false,\"checkReminders\":false,\"selectedConversationIds\":[],\"archiveFrom\":\"all\"}");
        JsonObject Obj(object value) => JsonSerializer.SerializeToNode(value)!.AsObject();
        var messages = new JsonArray(Obj(new { id = "older-evidence", scopeType = "project", scopeId = "project", role = "user", text = "山海计划原始验收口令：" + archiveCode, createdAt = DateTimeOffset.Now.AddMonths(-4).ToString("O"), attachments = Array.Empty<object>() }));
        for (int i = 0; i < 6; i++) messages.Add(Obj(new { id = "recent-" + i, scopeType = "project", scopeId = "project", role = "user", text = "一般进度记录", createdAt = DateTimeOffset.Now.AddDays(-i).ToString("O"), attachments = Array.Empty<object>() }));
        var fixture = new JsonObject { ["schemaVersion"] = 1,
            ["students"] = new JsonArray(Obj(new { id = "self", name = "合成当前成员", grade = "", tags = Array.Empty<string>(), bio = "", currentSummary = "", summaryStatus = "idle" })),
            ["projects"] = new JsonArray(Obj(new { id = "project", name = "山海计划", studentIds = new[] { "self" }, currentSummary = "核对安排", summaryStatus = "idle" })),
            ["tasks"] = new JsonArray(Obj(new { id = "task", title = "整理验收清单", studentIds = new[] { "self" }, projectId = "project", start = DateTime.Today.ToString("yyyy-MM-dd"), due = DateTime.Today.AddDays(4).ToString("yyyy-MM-dd"), done = false })),
            ["alarms"] = new JsonArray(), ["messages"] = messages,
            ["settings"] = Obj(new { currentStudentId = "self", model = "", baseUrl = "http://127.0.0.1:1/v1", apiMode = "responses" }), ["status"] = new JsonObject() };
        File.WriteAllText(Path.Combine(data, "state.json"), fixture.ToJsonString());
        using var app = new AppService(data);
        using var wx = new WeChatService(data, runtimeRoot, app.Snapshot, _ => throw new InvalidOperationException("本次只读测试不创建记录。"));
        wx.Store.SaveConversation(Obj(new { id = "chat", accountId = "fixture", externalId = "friend", name = "陈老师（合成）", kind = "friend" }));
        wx.Store.UpsertMessage(Obj(new { id = "wx-evidence", accountId = "fixture", conversationId = "chat", senderId = "friend", senderName = "陈老师（合成）", kind = "image", text = "山海计划的最终确认码在这张图片里。", rawContent = "山海计划的最终确认码在图片里，请以图片为准。", when = DateTimeOffset.Now.AddDays(-10).ToString("O") }));
        string picture = Path.Combine(data, "wechat", "files", "confirmation.png"); Directory.CreateDirectory(Path.GetDirectoryName(picture)!);
        using (var bitmap = new Bitmap(1000, 350))
        {
            using var graphics = Graphics.FromImage(bitmap); graphics.Clear(Color.White); using var font = new Font("Microsoft YaHei", 32); using var mono = new Font("Consolas", 45, FontStyle.Bold);
            graphics.DrawString("山海计划 / 最终确认码", font, Brushes.DarkBlue, 30, 35); graphics.DrawString(imageCode, mono, Brushes.Black, 40, 145); bitmap.Save(picture, ImageFormat.Png);
        }
        string hash = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(picture))).ToLowerInvariant();
        wx.Store.UpsertFile(Obj(new { id = "wx-image", messageId = "wx-evidence", name = "确认图片.png", type = "image", mime = "image/png", status = "ready", available = true, size = new FileInfo(picture).Length }), "synthetic-original", "confirmation.png", hash, "");
        var before = app.Snapshot().ToJsonString(); var calls = new JsonArray(); int modelCalls = 0;
        using var assistant = new AssistantService(data, AppContext.BaseDirectory, app.BuildAssistantContext, () =>
        {
            report["modelCalls"] = ++modelCalls; if (modelCalls != 1) throw new InvalidOperationException("测试只允许一次模型运行。"); return connection.DeepClone().AsObject();
        });
        assistant.ReadApiDescription = AppService.DescribeAssistantReads(); foreach (var item in WeChatService.DescribeAssistantReads()) assistant.ReadApiDescription.Add(item!.DeepClone());
        assistant.ReadDataAsync = async (operation, args, workspace, ct) =>
        {
            calls.Add(operation);
            return operation.StartsWith("app.", StringComparison.Ordinal) ? await app.ReadAssistantDataAsync(operation, args, workspace, ct) : await wx.ReadAssistantDataAsync(operation, args, workspace, ct);
        };
        var watch = System.Diagnostics.Stopwatch.StartNew();
        await assistant.HandleAsync("assistant.send", new() { ["text"] = "帮我核对山海计划：项目最早归档中的原始验收口令是什么？陈老师在微信图片里发来的最终确认码是什么？目前未完成的任务是什么？请查原始资料，简短回答并注明来源，不创建或修改任何内容。", ["references"] = new JsonArray(), ["includeRecent"] = false, ["includeReminders"] = false });
        var deadline = DateTimeOffset.UtcNow.AddMinutes(7);
        while (assistant.Snapshot()["busy"]!.GetValue<bool>() && DateTimeOffset.UtcNow < deadline) await Task.Delay(400);
        if (assistant.Snapshot()["busy"]!.GetValue<bool>()) { await assistant.HandleAsync("assistant.cancel", new()); throw new InvalidOperationException("只读测试超时，已停止，不自动重试。"); }
        var snapshot = assistant.Snapshot(); string error = snapshot["error"]?.GetValue<string>() ?? "";
        string answer = snapshot["messages"]!.AsArray().LastOrDefault(m => m?["role"]?.GetValue<string>() == "assistant")?["text"]?.GetValue<string>() ?? "";
        report["milliseconds"] = watch.ElapsedMilliseconds; report["operations"] = calls; report["answer"] = answer;
        report["archiveCodeRead"] = answer.Contains(archiveCode, StringComparison.OrdinalIgnoreCase); report["imageCodeRead"] = answer.Contains(imageCode, StringComparison.OrdinalIgnoreCase);
        report["taskRead"] = answer.Contains("整理验收清单"); report["businessUnchanged"] = before == app.Snapshot().ToJsonString();
        if (error.Length > 0 || modelCalls != 1 || calls.Count == 0 || !report["archiveCodeRead"]!.GetValue<bool>() || !report["imageCodeRead"]!.GetValue<bool>() || !report["taskRead"]!.GetValue<bool>() || !report["businessUnchanged"]!.GetValue<bool>())
            throw new InvalidOperationException(error.Length > 0 ? error : "未通过无引用的全库读取、原图或业务数据保留验证。");
    }
}
