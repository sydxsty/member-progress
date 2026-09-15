using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using StudentProgress.WeChat;

namespace StudentProgress;

// One isolated host-integration pass. No source reader, local embedding or GPT invocation.
internal static class WeChatHostChecks
{
    public static int Run(string dataDir)
    {
        var checks = new List<string>(); bool ownsDirectory = false;
        try
        {
            if (Directory.Exists(dataDir) && Directory.EnumerateFileSystemEntries(dataDir).Any()) return 1;
            Directory.CreateDirectory(dataDir); ownsDirectory = true;
            RunChecks(dataDir, checks).GetAwaiter().GetResult();
            Write(true, null); return 0;
        }
        catch (Exception e) { if (ownsDirectory) Write(false, e.Message); return 1; }
        void Write(bool ok, string? error) => File.WriteAllText(Path.Combine(dataDir, "wechat-host-result.json"),
            JsonSerializer.Serialize(new { ok, count = checks.Count, checks, error, modelCalls = 0, embeddingCalls = 0, liveWechatReads = 0 },
                new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false));
    }

    static JsonObject Obj(object o) => JsonSerializer.SerializeToNode(o)!.AsObject();
    static string S(JsonNode? n, string key) => n?[key]?.GetValue<string>() ?? "";
    static string Hash(string path) { using var stream = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(stream)); }

    static async Task RunChecks(string directory, List<string> checks)
    {
        void Check(bool value, string name) { if (!value) throw new InvalidOperationException(name); checks.Add(name); }
        var actual = Path.Combine(directory, "data"); Directory.CreateDirectory(actual);
        File.WriteAllText(Path.Combine(actual, "initialized"), "isolated-wechat-host-no-settings-detection");
        JsonObject Member(string id, string name) => Obj(new { id, name, grade = "", tags = Array.Empty<string>(), bio = "", currentSummary = "保持手工维护的进展", summaryStatus = "idle", summaryError = "" });
        File.WriteAllText(Path.Combine(actual, "state.json"), new JsonObject
        {
            ["schemaVersion"] = 1, ["students"] = new JsonArray(Member("current-member", "当前用户"), Member("other-member", "另一位成员")),
            ["projects"] = new JsonArray(Obj(new { id = "project", name = "测试项目", studentIds = new[] { "current-member" }, currentSummary = "项目进展保持原样", summaryStatus = "idle" })),
            ["tasks"] = new JsonArray(), ["messages"] = new JsonArray(), ["alarms"] = new JsonArray(),
            ["settings"] = Obj(new { currentStudentId = "current-member", baseUrl = "http://127.0.0.1:1/v1", model = "", apiMode = "responses" }), ["status"] = new JsonObject()
        }.ToJsonString(), new UTF8Encoding(false));
        string wxRoot = Path.Combine(actual, "wechat"); Directory.CreateDirectory(wxRoot);
        var configuration = Obj(new { enabled = false, checkReminders = false, checkMinutes = 60, selectedConversationIds = Array.Empty<string>(), bindings = Array.Empty<object>() });
        File.WriteAllText(Path.Combine(wxRoot, "config.json"), configuration.ToJsonString());
        string tomorrow = DateTime.Today.AddDays(1).ToString("yyyy-MM-dd"), rangeStart = DateTime.Today.AddDays(7).ToString("yyyy-MM-dd"), rangeDue = DateTime.Today.AddDays(9).ToString("yyyy-MM-dd");
        string alarmAt = new DateTimeOffset(DateTime.Today.AddDays(1).AddHours(14)).ToString("O");
        JsonObject Candidate(string id, string kind, string title) => Obj(new { id, kind, title, currentStudentId = "current-member", status = "pending", sourceReferences = Array.Empty<object>(), sourceMessageIds = new[] { "source-message" } });
        var alarmSuggestion = Candidate("alarm", "reminder", "下午两点叫我"); alarmSuggestion["when"] = alarmAt;
        var taskSuggestion = Candidate("day-task", "task", "去南湖"); taskSuggestion["start"] = tomorrow; taskSuggestion["due"] = tomorrow;
        var rangeSuggestion = Candidate("range-task", "task", "参加会议"); rangeSuggestion["start"] = rangeStart; rangeSuggestion["due"] = rangeDue;
        var missingSuggestion = Candidate("needs-time", "reminder", "补充时刻"); missingSuggestion["needsDetails"] = true;
        var dismissedSuggestion = Candidate("dismiss-me", "task", "本次不需要的建议"); dismissedSuggestion["start"] = tomorrow; dismissedSuggestion["due"] = tomorrow;
        File.WriteAllText(Path.Combine(wxRoot, "suggestions.json"), new JsonObject { ["schemaVersion"] = 1,
            ["suggestions"] = new JsonArray(alarmSuggestion, taskSuggestion, rangeSuggestion, missingSuggestion, dismissedSuggestion) }.ToJsonString());

        using var app = new AppService(actual);
        app.RegisterBackupFiles(new[] { "wechat/config.json", "wechat/suggestions.json" });
        // The absent runtime path prevents even an accidental embedding load from working.
        using var wx = new WeChatService(actual, Path.Combine(directory, "absent-runtime"), app.Snapshot, app.ImportWeChatArchive);
        int connectionRequests = 0;
        wx.ConfigureAgent(() => app.BuildAssistantContext(new JsonObject { ["includeReminders"] = true, ["includeRecent"] = false }),
            () => { connectionRequests++; throw new InvalidOperationException("模型不应在本验收中调用"); }, app.CreateAssistantReminders);
        JsonObject State() => app.Snapshot();
        JsonObject Message(string id) => State()["messages"]!.AsArray().OfType<JsonObject>().Single(m => S(m, "id") == id);
        bool Visible(string id) => State()["messages"]!.AsArray().Any(m => S(m, "id") == id);
        JsonObject Suggestion(string id) => wx.Snapshot()["suggestions"]!.AsArray().OfType<JsonObject>().Single(s => S(s, "id") == id);
        async Task<JsonObject> Do(string action, object payload) => (await app.HandleAsync(action, Obj(payload)))!.AsObject();

        var fileDirectory = Path.Combine(wxRoot, "files"); Directory.CreateDirectory(fileDirectory);
        var original = Path.Combine(fileDirectory, "large-source.bin");
        byte[] block = Enumerable.Range(0, 1024 * 1024).Select(i => (byte)((i * 31 + i / 251) % 251)).ToArray();
        using (var output = File.Create(original)) { for (int i = 0; i < 22; i++) output.Write(block); output.WriteByte(173); }
        string originalHash = Hash(original); long originalSize = new FileInfo(original).Length;
        var importPayload = Obj(new { requestId = "fixture-external-file", scopeType = "student", scopeId = "current-member", text = "微信自动归档原文件", createdAt = DateTimeOffset.Now.AddDays(-2).ToString("O"),
            sourceReferences = new[] { new { scopeType = "wechat", scopeId = "fixture-reference", title = "与成员不同的微信备注" } },
            files = new[] { new { path = original, name = "超过二十兆的原件.bin", mime = "application/octet-stream" } } });
        var imported = app.ImportWeChatArchive(importPayload); string importedId = S(imported, "messageId");
        var attachment = Message(importedId)["attachments"]![0]!; string attachmentId = S(attachment, "id"); string businessFile = app.ResolveAttachment(attachmentId)!;
        Check(originalSize > 20L * 1024 * 1024 && File.Exists(businessFile) && new FileInfo(businessFile).Length == originalSize && Hash(businessFile) == originalHash, "native_import_preserves_file_larger_than_20MB");
        Check(!Path.GetFullPath(businessFile).Equals(Path.GetFullPath(original), StringComparison.OrdinalIgnoreCase) && S(State()["students"]![0], "currentSummary") == "保持手工维护的进展" && S(State()["projects"]![0], "currentSummary") == "项目进展保持原样", "business_archive_has_independent_copy_and_preserves_progress");
        var replay = app.ImportWeChatArchive(importPayload);
        Check(replay["replayed"]!.GetValue<bool>() && State()["messages"]!.AsArray().Count == 1, "external_import_replay_is_idempotent");
        await Do("chat.delete", new { id = importedId });
        replay = app.ImportWeChatArchive(importPayload);
        Check(replay["replayed"]!.GetValue<bool>() && !Visible(importedId) && File.Exists(businessFile), "reimport_does_not_resurrect_deleted_archive");
        await Do("chat.restore", new { id = importedId });
        Check(Visible(importedId) && Hash(app.ResolveAttachment(attachmentId)!) == originalHash, "archive_undo_retains_original_file_bytes");

        string account = "fixture-account", conversation = WeChatStore.Stable(account, "fixture-friend");
        wx.Store.SaveConversation(Obj(new { id = conversation, accountId = account, externalId = "fixture-friend", name = "不等于成员姓名的微信备注", kind = "friend" }));
        wx.Store.UpsertMessage(Obj(new { id = "source-message", accountId = account, conversationId = conversation, senderId = "fixture-friend", senderName = "微信发送人", isSelf = false, kind = "file", text = "原始文件附带的聊天内容", rawContent = "<原始内容>原始文件附带的聊天内容</原始内容>", when = DateTimeOffset.Now.AddMinutes(-1).ToString("O") }));
        wx.Store.UpsertFile(Obj(new { id = "source-file", messageId = "source-message", name = "超过二十兆的原件.bin", size = originalSize, type = "file", available = true }), "C:\\source-private\\unselected-name.bin", "large-source.bin", originalHash.ToLowerInvariant(), "");
        var reference = (await wx.HandleAsync("wechat.reference.create", Obj(new { fileId = "source-file" })))["reference"]!.AsObject();
        var evidenceDirectory = Path.Combine(directory, "evidence");
        var materials = await wx.WriteReferenceWorkspaceAsync(new JsonArray(reference.DeepClone()), evidenceDirectory, CancellationToken.None);
        var evidenceFile = Directory.EnumerateFiles(Path.Combine(evidenceDirectory, "wechat", "originals")).Single();
        var sourceIndex = Directory.EnumerateFiles(Path.Combine(evidenceDirectory, "wechat"), "*.jsonl").Single();
        var evidenceJson = File.ReadAllText(sourceIndex);
        Check(materials["originalFileCount"]!.GetValue<int>() == 1 && new FileInfo(evidenceFile).Length == originalSize && Hash(evidenceFile) == originalHash, "native_reference_contains_complete_original_file");
        var sourceRow = JsonNode.Parse(evidenceJson.Trim())!;
        Check(S(sourceRow, "rawContent") == "<原始内容>原始文件附带的聊天内容</原始内容>" && !evidenceJson.Contains("source-private") && !evidenceJson.Contains("storedName"), "reference_keeps_original_content_and_removes_host_paths");
        File.WriteAllBytes(original, new byte[] { 1, 2, 3 });
        Check(Hash(businessFile) == originalHash && Hash(evidenceFile) == originalHash, "source_change_cannot_change_business_or_agent_file_copy");

        var createdAlarm = await wx.HandleAsync("wechat.suggestion.create", Obj(new { id = "alarm" }));
        var alarm = State()["alarms"]!.AsArray().Single()!;
        Check(createdAlarm["created"]!.GetValue<bool>() && S(alarm, "scopeType") == "student" && S(alarm, "studentId") == "current-member" && DateTimeOffset.Parse(S(alarm, "when")) == DateTimeOffset.Parse(alarmAt), "one_click_alarm_targets_current_member_at_exact_time");
        var secondAlarmClick = await wx.HandleAsync("wechat.suggestion.create", Obj(new { id = "alarm" }));
        Check(secondAlarmClick["replayed"]!.GetValue<bool>() && State()["alarms"]!.AsArray().Count == 1, "repeated_suggestion_click_does_not_duplicate_alarm");
        await wx.HandleAsync("wechat.suggestion.create", Obj(new { id = "day-task" }));
        await wx.HandleAsync("wechat.suggestion.create", Obj(new { id = "range-task" }));
        var tasks = State()["tasks"]!.AsArray().OfType<JsonObject>().ToArray();
        var dayTask = tasks.Single(t => S(t, "title") == "去南湖"); var rangeTask = tasks.Single(t => S(t, "title") == "参加会议");
        Check(S(dayTask, "start") == tomorrow && S(dayTask, "due") == tomorrow && dayTask["studentIds"]!.AsArray().Single()!.GetValue<string>() == "current-member", "date_only_suggestion_creates_current_member_task");
        Check(S(rangeTask, "start") == rangeStart && S(rangeTask, "due") == rangeDue && rangeTask["studentIds"]!.AsArray().Single()!.GetValue<string>() == "current-member", "date_range_suggestion_preserves_both_task_dates");
        var beforeMissing = State()["alarms"]!.AsArray().Count;
        var missing = await wx.HandleAsync("wechat.suggestion.create", Obj(new { id = "needs-time" }));
        Check(missing["needsDetails"]!.GetValue<bool>() && State()["alarms"]!.AsArray().Count == beforeMissing, "missing_time_prompts_for_details_without_guessing");
        string editedWhen = new DateTimeOffset(DateTime.Today.AddDays(2).AddHours(16).AddMinutes(25)).ToString("O");
        await wx.HandleAsync("wechat.suggestion.create", Obj(new { id = "needs-time", title = "补充后的正式事项", when = editedWhen }));
        wx.ReloadSuggestions();
        var finalSuggestion = Suggestion("needs-time"); var editedAlarm = State()["alarms"]!.AsArray().Single(a => S(a, "title") == "补充后的正式事项")!;
        Check(S(finalSuggestion, "when") == editedWhen && S(finalSuggestion, "title") == "补充后的正式事项" && S(finalSuggestion, "status") == "created" && DateTimeOffset.Parse(S(editedAlarm, "when")) == DateTimeOffset.Parse(editedWhen), "edited_final_time_and_title_persist_with_created_receipt");
        await wx.HandleAsync("wechat.suggestion.dismiss", Obj(new { id = "dismiss-me" })); wx.ReloadSuggestions();
        Check(S(Suggestion("dismiss-me"), "status") == "dismissed", "ignored_suggestion_remains_ignored_after_reload");
        await wx.HandleAsync("wechat.suggestion.restore", Obj(new { id = "dismiss-me" }));
        Check(S(Suggestion("dismiss-me"), "status") == "pending", "ignored_suggestion_can_be_restored");
        await wx.HandleAsync("wechat.suggestion.dismiss", Obj(new { id = "dismiss-me" }));

        Directory.CreateDirectory(Path.Combine(wxRoot, "reader-cache")); File.WriteAllText(Path.Combine(wxRoot, "reader-cache", "never-export.sqlite"), "未选择会话的读取缓存哨兵");
        File.WriteAllText(Path.Combine(wxRoot, "similarity.usearch"), "相似度索引哨兵");
        Directory.CreateDirectory(Path.Combine(actual, "runtime")); File.WriteAllText(Path.Combine(actual, "runtime", "never-export.bin"), "运行时哨兵");
        var backup = Path.Combine(directory, "general-backup.zip"); await Do("backup.export", new { path = backup });
        using (var zip = ZipFile.OpenRead(backup))
        {
            Check(zip.GetEntry("wechat/config.json") != null && zip.GetEntry("wechat/suggestions.json") != null, "general_backup_includes_wechat_settings_and_suggestions");
            Check(zip.Entries.Where(e => e.FullName.StartsWith("wechat/", StringComparison.Ordinal)).All(e => e.FullName is "wechat/config.json" or "wechat/suggestions.json") && zip.Entries.All(e => !e.FullName.StartsWith("runtime/", StringComparison.Ordinal)), "general_backup_excludes_wechat_records_files_index_reader_cache_and_runtime");
            Check(zip.GetEntry("attachments/" + Path.GetFileName(businessFile))?.Length == originalSize, "general_backup_keeps_independent_business_file_copy");
        }
        wx.SaveConfiguration(Obj(new { enabled = false, checkReminders = false, checkMinutes = 120 }));
        await Do("backup.import", new { path = backup }); wx.ReloadSuggestions();
        var restoredConfiguration = JsonNode.Parse(File.ReadAllText(Path.Combine(wxRoot, "config.json")))!;
        Check(restoredConfiguration["checkMinutes"]!.GetValue<int>() == 60 && S(Suggestion("dismiss-me"), "status") == "dismissed" && S(Suggestion("needs-time"), "when") == editedWhen, "general_backup_restores_settings_and_suggestion_decisions");
        Check(Hash(app.ResolveAttachment(attachmentId)!) == originalHash && wx.Store.Counts()["messages"]!.GetValue<long>() == 1 && File.Exists(Path.Combine(wxRoot, "reader-cache", "never-export.sqlite")), "general_restore_preserves_separate_wechat_materials_and_business_bytes");
        var replayAfterRestore = await wx.HandleAsync("wechat.suggestion.create", Obj(new { id = "alarm" }));
        Check(replayAfterRestore["replayed"]!.GetValue<bool>() && State()["alarms"]!.AsArray().Count == 2 && State()["tasks"]!.AsArray().Count == 2, "restored_suggestion_receipts_prevent_repeat_creation");
        Check(connectionRequests == 0 && !File.Exists(Path.Combine(actual, "secret.dat")) && !State()["settings"]!["aiConfigured"]!.GetValue<bool>() && S(State()["students"]![0], "currentSummary") == "保持手工维护的进展", "checks_never_load_models_or_change_member_progress");
    }
}
