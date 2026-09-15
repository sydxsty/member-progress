using System.Drawing.Imaging;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using StudentProgress.Assistant;
using StudentProgress.WeChat;

namespace StudentProgress;

// Opt-in: one real chat request with a synthetic image, never the user's WeChat images.
internal static class WeChatImageLiveCheck
{
    internal static int Run(string directory, string connectionFrom)
    {
        directory = Path.GetFullPath(directory); connectionFrom = Path.GetFullPath(connectionFrom);
        if (directory.Equals(connectionFrom, StringComparison.OrdinalIgnoreCase) ||
            directory.StartsWith(connectionFrom + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
            connectionFrom.StartsWith(directory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
            Directory.Exists(directory) && Directory.EnumerateFileSystemEntries(directory).Any()) return 1;
        Directory.CreateDirectory(directory);
        var report = new JsonObject { ["ok"] = false, ["modelCalls"] = 0, ["syntheticImageOnly"] = true };
        string key = "";
        try
        {
            var settings = JsonNode.Parse(File.ReadAllText(Path.Combine(connectionFrom, "state.json")))!["settings"]!;
            key = Encoding.UTF8.GetString(ProtectedData.Unprotect(File.ReadAllBytes(Path.Combine(connectionFrom, "secret.dat")), null, DataProtectionScope.CurrentUser));
            var connection = new JsonObject { ["key"] = key, ["baseUrl"] = settings["baseUrl"]?.DeepClone(),
                ["model"] = settings["model"]?.DeepClone(), ["apiMode"] = settings["apiMode"]?.DeepClone() };
            RunAsync(directory, connection, report).GetAwaiter().GetResult(); report["ok"] = true;
        }
        catch (Exception error)
        {
            var text = error is InvalidOperationException ? error.Message : error.GetType().Name;
            report["error"] = key.Length > 0 ? text.Replace(key, "[REDACTED]") : text;
        }
        File.WriteAllText(Path.Combine(directory, "wechat-image-live-result.json"), report.ToJsonString(new JsonSerializerOptions(JsonSerializerOptions.Default) { WriteIndented = true }), new UTF8Encoding(false));
        return report["ok"]!.GetValue<bool>() ? 0 : 1;
    }

    static async Task RunAsync(string directory, JsonObject connection, JsonObject report)
    {
        string data = Path.Combine(directory, "isolated-data"), code = Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
        Directory.CreateDirectory(Path.Combine(data, "wechat"));
        File.WriteAllText(Path.Combine(data, "wechat", "config.json"), "{\"enabled\":false,\"checkReminders\":false}");
        var context = new JsonObject { ["generatedAt"] = DateTimeOffset.Now.ToString("O"), ["students"] = new JsonArray(),
            ["projects"] = new JsonArray(), ["tasks"] = new JsonArray(), ["reminders"] = new JsonArray(), ["referencedArchives"] = new JsonArray() };
        using var wx = new WeChatService(data, AppContext.BaseDirectory, () => context.DeepClone().AsObject(), _ => throw new InvalidOperationException("联调不创建业务数据。"));
        wx.Store.SaveConversation(new() { ["id"] = "image-fixture", ["accountId"] = "fixture", ["externalId"] = "fixture", ["name"] = "合成图片联调", ["kind"] = "friend" });
        var message = new JsonObject { ["id"] = "image-message", ["accountId"] = "fixture", ["conversationId"] = "image-fixture", ["senderId"] = "fixture",
            ["senderName"] = "合成联系人", ["kind"] = "image", ["when"] = DateTimeOffset.Now.ToString("O"), ["text"] = "安排以图片为准。" };
        wx.Store.UpsertMessage(message);
        string picture = Path.Combine(directory, "fixture.png");
        using (var bitmap = new Bitmap(1100, 620))
        {
            using var graphics = Graphics.FromImage(bitmap); graphics.Clear(Color.White);
            using var font = new Font("Microsoft YaHei", 34); using var codeFont = new Font("Consolas", 46, FontStyle.Bold);
            graphics.DrawString("图片内容验证 / 会议安排", font, Brushes.DarkBlue, 45, 45);
            graphics.DrawString("CODE: " + code, codeFont, Brushes.Black, 45, 165);
            graphics.DrawString("开始时间：15:40", font, Brushes.Black, 45, 290);
            graphics.DrawString("地点：科技楼 608", font, Brushes.Black, 45, 400);
            bitmap.Save(picture, ImageFormat.Png);
        }
        await wx.ImportFilesAsync(new() { ["attachments"] = new JsonArray(new JsonObject { ["id"] = "fixture-image", ["name"] = "会议图.png", ["path"] = picture,
            ["size"] = new FileInfo(picture).Length, ["type"] = "image", ["status"] = "ready", ["decodeStatus"] = "decoded" }) }, message, CancellationToken.None);
        var reference = wx.CreateReference("image-fixture");
        int calls = 0;
        using var assistant = new AssistantService(data, AppContext.BaseDirectory, _ => context.DeepClone().AsObject(), () =>
        {
            report["modelCalls"] = ++calls;
            if (calls != 1) throw new InvalidOperationException("本次联调只允许一次模型调用。");
            return connection.DeepClone().AsObject();
        });
        assistant.PrepareMaterialsAsync = async (selection, workspace, ct) =>
        {
            var material = await wx.WriteReferenceWorkspaceAsync(selection["references"]!.AsArray(), workspace, ct);
            report["readableImageCount"] = material["imageFileCount"]?.DeepClone();
            var manifest = new JsonObject { ["references"] = new JsonArray(), ["wechat"] = material, ["conversationFile"] = "chat-history.json" };
            AssistantJson.WriteAtomic(Path.Combine(workspace, "materials.json"), manifest); return manifest;
        };
        var watch = System.Diagnostics.Stopwatch.StartNew();
        await assistant.HandleAsync("assistant.send", new() { ["text"] = "请读取引用中的图片，用一句话告诉我图片中的 CODE 校验码、会议开始时间和地点。不要创建任务或提醒。", ["includeRecent"] = false, ["includeReminders"] = false, ["references"] = new JsonArray(reference) });
        var deadline = DateTimeOffset.UtcNow.AddMinutes(5);
        while (assistant.Snapshot()["busy"]!.GetValue<bool>() && DateTimeOffset.UtcNow < deadline) await Task.Delay(400);
        if (assistant.Snapshot()["busy"]!.GetValue<bool>()) { await assistant.HandleAsync("assistant.cancel", new()); throw new InvalidOperationException("图片联调超时，已取消；不自动重试模型。"); }
        var state = assistant.Snapshot(); string error = state["error"]?.GetValue<string>() ?? "";
        string answer = state["messages"]!.AsArray().LastOrDefault(m => m?["role"]?.GetValue<string>() == "assistant")?["text"]?.GetValue<string>() ?? "";
        report["milliseconds"] = watch.ElapsedMilliseconds; report["answer"] = answer;
        report["imageOnlyCodeRead"] = answer.Contains(code, StringComparison.OrdinalIgnoreCase);
        report["timeAndLocationRead"] = (answer.Contains("15:40") || answer.Contains("15：40")) && answer.Contains("608");
        if (calls != 1 || error.Length > 0 || !report["imageOnlyCodeRead"]!.GetValue<bool>() || !report["timeAndLocationRead"]!.GetValue<bool>())
            throw new InvalidOperationException(error.Length > 0 ? error : "GPT 未完整读出只存在于图片中的内容。");
    }
}
