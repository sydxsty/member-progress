using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using StudentProgress.Assistant;

namespace StudentProgress;

internal static class V15Checks
{
    internal static int Run(string directory, string? connectionFrom = null, bool actionsOnly = false)
    {
        if (Directory.Exists(directory) && Directory.EnumerateFileSystemEntries(directory).Any()) return 1;
        Directory.CreateDirectory(directory);
        var result = new JsonObject { ["ok"] = false, ["modelCalls"] = 0 };
        try { (connectionFrom == null ? Offline(directory, result, actionsOnly) : Live(directory, connectionFrom, result)).GetAwaiter().GetResult(); result["ok"] = true; }
        catch (Exception ex) { result["error"] = ex.ToString(); }
        File.WriteAllText(Path.Combine(directory, "v15-result.json"), result.ToJsonString(new JsonSerializerOptions(JsonSerializerOptions.Default) { WriteIndented = true }), new UTF8Encoding(false));
        return result["ok"]!.GetValue<bool>() ? 0 : 1;
    }

    static void Seed(string data)
    {
        Directory.CreateDirectory(data); File.WriteAllText(Path.Combine(data, "initialized"), "synthetic-v15");
        AssistantJson.WriteAtomic(Path.Combine(data, "state.json"), new JsonObject {
            ["schemaVersion"] = 1, ["students"] = new JsonArray(new JsonObject { ["id"] = "self", ["name"] = "合成当前用户", ["grade"] = "", ["tags"] = new JsonArray() }),
            ["projects"] = new JsonArray(), ["tasks"] = new JsonArray(), ["alarms"] = new JsonArray(), ["messages"] = new JsonArray(),
            ["settings"] = new JsonObject { ["currentStudentId"] = "self", ["baseUrl"] = "http://127.0.0.1:1/v1", ["model"] = "", ["apiMode"] = "chat" }, ["status"] = new JsonObject() });
    }
    static async Task Offline(string directory, JsonObject result, bool actionsOnly)
    {
        if (!actionsOnly)
        {
            result["wechat"] = await WeChatProgressChecks.RunAsync(Path.Combine(directory, "wechat"), AppContext.BaseDirectory);
            result["attachments"] = await AssistantAttachmentChecks.RunAsync(Path.Combine(directory, "attachments"));
        }
        string data = Path.Combine(directory, "actions"); Seed(data);
        var user = new AssistantMessage { Id = "request", Text = "定个线上日程" };
        AssistantJson.WriteAtomic(Path.Combine(data, "assistant", "history.json"), new AssistantState { Messages = [user], NativeThreadId = "native-synthetic", SessionId = "0123456789abcdef0123456789abcdef" });
        using var app = new AppService(data);
        using var assistant = new AssistantService(data, AppContext.BaseDirectory, app.BuildAssistantContext, app.GetAssistantConnection, app.CreateAssistantReminders) { ArchiveWriter = app.ImportAssistantArchive, ReadApiDescription = AppService.DescribeAssistantReads(), ReadDataAsync = app.ReadAssistantDataAsync };
        var checks = new JsonArray(); void Check(bool good, string label) { if (!good) throw new InvalidOperationException(label); checks.Add(label); }
        Check(app.GetAssistantConnection()["apiMode"]!.GetValue<string>() == "responses", "legacy_chat_setting_migrates_to_responses");
        string tomorrow = DateTime.Today.AddDays(1).ToString("yyyy-MM-dd");
        var context = app.BuildAssistantContext(new());
        var task = new JsonObject { ["title"] = "找老师签字", ["start"] = tomorrow, ["due"] = tomorrow };
        var first = await assistant.InvokeNativeToolAsync("app_task_create", task, user, context, directory, CancellationToken.None);
        var reordered = new JsonObject { ["due"] = tomorrow, ["start"] = tomorrow, ["title"] = "找老师签字", ["projectId"] = "", ["studentIds"] = new JsonArray("self", "self") };
        var repeat = await assistant.InvokeNativeToolAsync("app_task_create", reordered, user, context, directory, CancellationToken.None);
        Check(app.Snapshot()["tasks"]!.AsArray().Count == 1 && app.Snapshot()["alarms"]!.AsArray().Count == 0 && first["tasks"]![0]!["studentIds"]![0]!.GetValue<string>() == "self" && repeat["replayed"]!.GetValue<bool>(), "date_task_current_member_and_reordered_retry_is_idempotent");
        await assistant.InvokeNativeToolAsync("app_reminder_create", new() { ["title"] = "13:30线上会议 腾讯会议378-806-993", ["when"] = new DateTimeOffset(DateTime.Today.AddDays(2).AddHours(13.5)).ToString("O") }, user, context, directory, CancellationToken.None);
        Check(app.Snapshot()["tasks"]!.AsArray().Count == 1 && app.Snapshot()["alarms"]!.AsArray().Count == 1 && app.Snapshot()["alarms"]![0]!["studentId"]!.GetValue<string>() == "self", "timed_reminder_creates_only_alarm_and_retains_meeting_number");
        var archive = new JsonObject { ["scopeType"] = "student", ["scopeId"] = "self", ["text"] = "明确要求归档的分析结果" };
        await assistant.InvokeNativeToolAsync("app_archive_append", archive, user, context, directory, CancellationToken.None);
        await assistant.InvokeNativeToolAsync("app_archive_append", archive, user, context, directory, CancellationToken.None);
        Check(app.Snapshot()["messages"]!.AsArray().Count == 1 && app.Snapshot()["messages"]![0]!["sourceType"]!.GetValue<string>() == "assistant", "explicit_archive_append_persists_and_deduplicates");
        string before = app.Snapshot().ToJsonString();
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel(); bool stopped = false;
        try { await assistant.InvokeNativeToolAsync("app_task_create", task, user, context, directory, cancelled.Token); } catch (OperationCanceledException) { stopped = true; }
        bool denied = false; try { await assistant.InvokeNativeToolAsync("task.delete", new(), user, context, directory, CancellationToken.None); } catch (InvalidOperationException) { denied = true; }
        Check(stopped && denied && before == app.Snapshot().ToJsonString(), "cancelled_or_unregistered_tools_cannot_modify_data");
        await assistant.HandleAsync("assistant.clear", new()); await assistant.HandleAsync("assistant.undo-clear", new());
        var restored = new AssistantStore(Path.Combine(data, "assistant")).Load();
        Check(restored.NativeThreadId == "native-synthetic" && restored.SessionId == "0123456789abcdef0123456789abcdef" && restored.Messages[0].ToolReceipts.Count == 3, "clear_undo_restores_native_thread_and_durable_receipts");
        var events = new List<CodexProgressEvent>(); var parser = new CodexEvents(true, events.Add); parser.SetThread("thread");
        parser.AcceptNotification("item/reasoning/textDelta", new() { ["threadId"] = "thread", ["delta"] = "PRIVATE_RAW_REASONING" });
        parser.AcceptNotification("item/reasoning/summaryTextDelta", new() { ["threadId"] = "thread", ["itemId"] = "reason", ["delta"] = "正在核对通知的时间" });
        parser.AcceptNotification("thread/tokenUsage/updated", JsonNode.Parse("""{"tokenUsage":{"last":{"inputTokens":1800,"outputTokens":200,"totalTokens":2000},"total":{"totalTokens":99000},"modelContextWindow":10000}}""")!.AsObject());
        Check(events.Any(e => e.Kind == "summary.delta") && !events.Any(e => e.Data.ToJsonString().Contains("PRIVATE_RAW_REASONING")) && parser.TokenUsage!["percent"]!.GetValue<double>() == 20, "public_summary_only_and_context_uses_last_request_not_cumulative_spend");
        var unknown = CodexEvents.NormalizeUsage(new() { ["last"] = new JsonObject { ["totalTokens"] = 2000 } });
        Check(unknown["percent"] == null && unknown["contextWindow"] == null, "unknown_context_capacity_does_not_invent_percentage");
        await app.HandleAsync("task.delete", new() { ["id"] = first["tasks"]![0]!["id"]!.DeepClone() });
        bool deleted = false;
        try { await assistant.InvokeNativeToolAsync("app_task_create", task, user, context, directory, CancellationToken.None); } catch (InvalidOperationException e) { deleted = e.Message.Contains("删除"); }
        Check(deleted && app.Snapshot()["tasks"]!.AsArray().Count == 0, "retry_revalidates_deleted_business_record_instead_of_replaying_stale_chat_receipt");
        result["actionsAndEvents"] = new JsonObject { ["count"] = checks.Count, ["checks"] = checks };
    }

    static async Task Live(string directory, string connectionFrom, JsonObject report)
    {
        // Read connection credentials only. All content and all business writes are synthetic.
        var settings = JsonNode.Parse(File.ReadAllText(Path.Combine(connectionFrom, "state.json")))!["settings"]!;
        string secret = Encoding.UTF8.GetString(ProtectedData.Unprotect(File.ReadAllBytes(Path.Combine(connectionFrom, "secret.dat")), null, DataProtectionScope.CurrentUser));
        var connection = new JsonObject { ["key"] = secret, ["baseUrl"] = settings["baseUrl"]?.DeepClone(), ["model"] = settings["model"]?.DeepClone(), ["apiMode"] = "responses" };
        string data = Path.Combine(directory, "data"); Seed(data); using var app = new AppService(data);
        using var assistant = new AssistantService(data, AppContext.BaseDirectory, app.BuildAssistantContext, () => connection, app.CreateAssistantReminders) { ArchiveWriter = app.ImportAssistantArchive, ReadApiDescription = AppService.DescribeAssistantReads(), ReadDataAsync = app.ReadAssistantDataAsync };
        string code = Guid.NewGuid().ToString("N")[..8].ToUpperInvariant(); string imageCode = Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
        var nativeEvents = new HashSet<string>(); bool sawUsage = false, sawActivity = false;
        assistant.ProgressChanged += () => { var p = assistant.ProgressSnapshot(); sawUsage |= p["usage"] != null; sawActivity |= (p["items"] as JsonArray)?.Count > 0; foreach (var item in (p["items"] as JsonArray ?? new()).OfType<JsonObject>()) { string name = AssistantJson.Text(item["title"]); if (name.StartsWith("app_", StringComparison.Ordinal)) nativeEvents.Add(name); } };
        using var bitmap = new System.Drawing.Bitmap(900, 220); bitmap.SetResolution(96,96); using (var graphics = System.Drawing.Graphics.FromImage(bitmap))
        { graphics.Clear(System.Drawing.Color.White); using var font = new System.Drawing.Font("Arial", 36, System.Drawing.FontStyle.Bold, System.Drawing.GraphicsUnit.Pixel); graphics.DrawString("IMAGE CODE " + imageCode, font, System.Drawing.Brushes.Black, new System.Drawing.PointF(18, 70)); }
        using var imageBytes = new MemoryStream(); bitmap.Save(imageBytes, System.Drawing.Imaging.ImageFormat.Png);
        string day = DateTime.Today.AddDays(2).ToString("yyyy-MM-dd"), next = DateTime.Today.AddDays(1).ToString("yyyy-MM-dd");
        var files = new JsonArray(new JsonObject { ["name"] = "会议资料.txt", ["mime"] = "text/plain", ["dataBase64"] = Convert.ToBase64String(Encoding.UTF8.GetBytes("这是一份合成测试资料。原件末尾编码：" + code)) },
            new JsonObject { ["name"] = "资料图片.png", ["mime"] = "image/png", ["dataBase64"] = Convert.ToBase64String(imageBytes.ToArray()) });
        string question = $"明天提醒我找老师签字。另有通知：拟于{day}下午13:30召开线上项目推进会议，腾讯会议378-806-993。请据此定个线上日程。再分析本条两个附件，把原件及简短分析归档给我。回复中写出文本和图片各自的编码，方便核对。不要替我联系其他人。";
        report["modelCalls"] = 1; report["syntheticDataOnly"] = true;
        var watch = System.Diagnostics.Stopwatch.StartNew(); await assistant.HandleAsync("assistant.send", new() { ["text"] = question, ["files"] = files });
        var limit = DateTimeOffset.UtcNow.AddMinutes(10);
        while (assistant.Snapshot()["busy"]!.GetValue<bool>() && DateTimeOffset.UtcNow < limit) await Task.Delay(400);
        if (assistant.Snapshot()["busy"]!.GetValue<bool>()) { await assistant.HandleAsync("assistant.cancel", new()); throw new TimeoutException("单次真实联调超时，不自动重试。"); }
        var snapshot = assistant.Snapshot(); string error = AssistantJson.Text(snapshot["error"]);
        string answer = AssistantJson.Text(snapshot["messages"]!.AsArray().LastOrDefault(m => AssistantJson.Text(m?["role"]) == "assistant")?["text"]);
        var business = app.Snapshot(); var tasks = business["tasks"]!.AsArray(); var reminders = business["alarms"]!.AsArray(); var archives = business["messages"]!.AsArray();
        report["milliseconds"] = watch.ElapsedMilliseconds; report["answer"] = answer; report["usageVisible"] = sawUsage; report["activityVisible"] = sawActivity; report["tools"] = new JsonArray(nativeEvents.Select(x => (JsonNode?)JsonValue.Create(x)).ToArray());
        report["dateTask"] = tasks.Count == 1 && AssistantJson.Text(tasks[0]?["start"]) == next && AssistantJson.Text(tasks[0]?["title"]).Contains("签字");
        report["timedAlarmOnly"] = reminders.Count == 1 && DateTimeOffset.Parse(AssistantJson.Text(reminders[0]?["when"])).LocalDateTime == DateTime.Parse(day).AddHours(13.5) && AssistantJson.Text(reminders[0]?["title"]).Contains("378-806-993");
        report["originalsArchived"] = archives.Count == 1 && (archives[0]?["attachments"] as JsonArray)?.Count == 2;
        report["originalsRead"] = answer.Contains(code, StringComparison.OrdinalIgnoreCase) && answer.Contains(imageCode, StringComparison.OrdinalIgnoreCase);
        if (error.Length > 0 || !sawUsage || !sawActivity || new[] { "dateTask", "timedAlarmOnly", "originalsArchived", "originalsRead" }.Any(k => !report[k]!.GetValue<bool>()))
            throw new InvalidOperationException(error.Length > 0 ? error : "原生工具/流事件/日程分类/原件分析与归档未全部通过。");
    }
}
