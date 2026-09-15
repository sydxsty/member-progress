using System.IO.Compression;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using StudentProgress.Remote;

namespace StudentProgress;

// A focused pass over new fixture data; never imports settings or invokes a model.
internal static class ArchiveChecks
{
    public static int Run(string dataDir)
    {
        var passed = new List<string>(); var ownsDirectory = false;
        try
        {
            if (Directory.Exists(dataDir) && Directory.EnumerateFileSystemEntries(dataDir).Any()) return 1;
            Directory.CreateDirectory(dataDir); ownsDirectory = true;
            RunChecks(dataDir, passed);
            Write(true, null); return 0;
        }
        catch (Exception error) { if (ownsDirectory) Write(false, error.Message); return 1; }
        void Write(bool ok, string? error) => File.WriteAllText(Path.Combine(dataDir, "archive-result.json"),
            JsonSerializer.Serialize(new { ok, checks = passed, error, liveModelCalls = 0 }, new JsonSerializerOptions { WriteIndented = true }));
    }

    static JsonObject Obj(object value) => JsonSerializer.SerializeToNode(value)!.AsObject();
    static string Text(JsonNode? value) => value?.GetValue<string>() ?? "";
    static void RunChecks(string dataDir, List<string> passed)
    {
        void Check(bool condition, string label)
        { if (!condition) throw new InvalidOperationException(label); passed.Add(label); }
        var actual = Path.Combine(dataDir, "data"); Directory.CreateDirectory(actual);
        Directory.CreateDirectory(Path.Combine(actual, "attachments"));
        File.WriteAllText(Path.Combine(actual, "initialized"), "archive-check-no-settings-detection");
        const string attachmentName = "archive-document.txt";
        File.WriteAllText(Path.Combine(actual, "attachments", attachmentName), "不可丢失的原始文档", new UTF8Encoding(false));
        JsonObject Member(string id, string name, string current = "", string source = "") => Obj(new
        { id, name, grade = "", tags = Array.Empty<string>(), bio = "", currentSummary = current, currentSummaryMessageId = source, currentSummaryRevision = 1, summaryStatus = "idle", summaryError = "" });
        JsonObject Message(string id, string scopeId, string role, string text, string generated = "", string[]? sources = null, string type = "student")
        {
            var row = Obj(new { id, scopeType = type, scopeId, role, text, createdAt = "2026-09-15T12:00:00+08:00", attachments = Array.Empty<object>(), suggestions = Array.Empty<object>() });
            if (role == "assistant") { row["generatedCurrentTask"] = generated; row["sourceMessageIds"] = new JsonArray((sources ?? []).Select(x => JsonValue.Create(x)).ToArray()); }
            return row;
        }
        var own = Message("u1", "s1", "user", "只出现在被删除消息中的特有内容");
        own["attachments"] = new JsonArray(Obj(new { id = "file1", name = "文档.txt", storedName = attachmentName, textStatus = "已提取", extractedText = "不可丢失的原始文档", mime = "text/plain", size = 30 }));
        var fixture = new JsonObject
        {
            ["schemaVersion"] = 1,
            ["students"] = new JsonArray(Member("s1", "归档测试成员", "最新任务", "a2")),
            ["archivedStudents"] = new JsonArray(Member("retired", "已归档成员")),
            ["projects"] = new JsonArray(Obj(new { id = "p1", name = "项目测试", studentIds = new[] { "s1" }, currentSummary = "项目下一步", currentSummaryMessageId = "pa", currentSummaryRevision = 1, summaryStatus = "idle", summaryError = "" })),
            ["tasks"] = new JsonArray(), ["alarms"] = new JsonArray(),
            ["messages"] = new JsonArray(own, Message("a1", "s1", "assistant", "旧摘要", "旧任务", ["u1"]),
                Message("u2", "s1", "user", "第二次进展"), Message("a2", "s1", "assistant", "最新摘要", "最新任务", ["u1", "u2"]),
                Message("retired-u", "retired", "user", "历史成员归档"), Message("pu", "p1", "user", "项目资料", type: "project"),
                Message("pa", "p1", "assistant", "项目摘要", "项目下一步", ["pu"], "project")),
            ["settings"] = Obj(new { baseUrl = "http://127.0.0.1:1/v1", model = "", apiMode = "responses" }),
            ["status"] = new JsonObject()
        };
        File.WriteAllText(Path.Combine(actual, "state.json"), fixture.ToJsonString());
        using var app = new AppService(actual);
        JsonObject Do(string action, object payload) => app.HandleAsync(action, Obj(payload)).GetAwaiter().GetResult()!.AsObject();
        JsonObject State() => (JsonObject)typeof(AppService).GetField("state", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(app)!;
        JsonObject Scope() => app.Snapshot()["students"]![0]!.AsObject();
        bool Visible(string id) => app.Snapshot()["messages"]!.AsArray().Any(x => Text(x!["id"]) == id);
        var pending = (Dictionary<string, CancellationTokenSource>)typeof(AppService).GetField("pending", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(app)!;
        var revisions = (Dictionary<string, long>)typeof(AppService).GetField("revisions", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(app)!;
        Check(!app.Snapshot().ContainsKey("deletedMessages") && !app.MobileSnapshot().ContainsKey("deletedMessages"), "legacy_state_migrates_and_tombstones_are_private");

        // Persist failure must roll back content and keep its active summary request intact.
        using (var failedSource = new CancellationTokenSource())
        {
            pending["student:s1"] = failedSource;
            var before = app.Snapshot().ToJsonString(); var revision = revisions.GetValueOrDefault("student:s1");
            Directory.CreateDirectory(Path.Combine(actual, "state.tmp")); var rejected = false;
            try { Do("chat.delete", new { id = "u1" }); } catch (Exception) { rejected = true; }
            Directory.Delete(Path.Combine(actual, "state.tmp"));
            Check(rejected && app.Snapshot().ToJsonString() == before && !failedSource.IsCancellationRequested
                && revisions.GetValueOrDefault("student:s1") == revision, "failed_save_preserves_message_and_inflight_summary");
            pending.Remove("student:s1");
        }
        using (var source = new CancellationTokenSource())
        {
            pending["student:s1"] = source; var revision = revisions.GetValueOrDefault("student:s1");
            Do("chat.delete", new { id = "u1" });
            Check(source.IsCancellationRequested && !pending.ContainsKey("student:s1") && revisions["student:s1"] > revision,
                "successful_delete_cancels_inflight_generation_and_revision");
        }
        Check(!Visible("u1") && File.ReadAllText(Path.Combine(actual, "attachments", attachmentName)) == "不可丢失的原始文档"
            && app.ResolveAttachment("file1") == null, "own_message_hidden_original_attachment_retained");
        var context = app.BuildAssistantContext(Obj(new { references = new[] { new { scopeType = "student", scopeId = "s1" } } }));
        var contextMessages = context["recentArchives"]!.AsArray().Concat(context["referencedArchives"]!.AsArray()).SelectMany(x => x!["messages"]!.AsArray());
        Check(contextMessages.All(x => Text(x!["id"]) != "u1") && app.MobileSnapshot()["messages"]!.AsArray().All(x => Text(x!["id"]) != "u1"),
            "deleted_message_excluded_from_gpt_context_and_mobile");
        Check(Text(Scope()["currentSummary"]) == "" && Visible("a1") && Visible("a2"), "deleted_source_invalidates_generated_progress_without_deleting_other_messages");
        Do("chat.restore", new { id = "u1" });
        Check(Visible("u1") && Text(app.Snapshot()["messages"]![0]!["id"]) == "u1" && File.Exists(app.ResolveAttachment("file1"))
            && Text(Scope()["currentSummary"]) == "最新任务", "undo_restores_original_order_attachment_and_unchanged_generated_progress");

        Do("chat.delete", new { id = "a2" });
        Check(!Visible("a2") && Text(Scope()["currentSummary"]) == "旧任务", "delete_latest_gpt_summary_uses_previous_surviving_generated_progress");
        Do("chat.restore", new { id = "a2" });
        Check(Text(Scope()["currentSummary"]) == "最新任务", "undo_gpt_summary_restores_its_progress");
        Do("chat.delete", new { id = "a2" });
        Do("student.patch", new { id = "s1", field = "currentSummary", value = "老师手工安排" });
        Do("chat.restore", new { id = "a2" });
        Do("chat.delete", new { id = "a2" });
        Check(Text(Scope()["currentSummary"]) == "老师手工安排", "manual_progress_survives_delete_and_late_undo");
        Do("chat.restore", new { id = "a2" });
        var liveScope = State()["students"]![0]!.AsObject();
        liveScope["currentSummary"] = "最新任务"; liveScope["currentSummaryMessageId"] = "a2";
        Do("chat.delete", new { id = "a1" });
        Check(Text(Scope()["currentSummary"]) == "最新任务", "deleting_older_summary_preserves_newer_progress");
        Do("chat.restore", new { id = "a1" });
        Do("chat.delete", new { id = "retired-u" }); Do("chat.restore", new { id = "retired-u" });
        Check(Visible("retired-u") && app.Snapshot()["archivedStudents"]!.AsArray().Count == 1, "archived_member_messages_can_delete_and_restore");
        Do("chat.delete", new { id = "pa" });
        Check(Text(app.Snapshot()["projects"]![0]!["currentSummary"]) == "", "project_summary_deletion_updates_only_its_scope");
        Do("chat.restore", new { id = "pa" });
        Check(Text(app.Snapshot()["projects"]![0]!["currentSummary"]) == "项目下一步", "project_summary_undo_restores_progress");

        JsonObject Request(string action, string id, string? version = null)
        {
            var request = Obj(new { action, payload = new { id }, requestId = Guid.NewGuid().ToString("N") });
            if (version != null) request["expectedVersion"] = version;
            return request;
        }
        var mobile = app.MobileSnapshot(); var version = Text(mobile["recordVersions"]!["message:u2"]);
        var stale = false;
        try { app.HandleMobileAction(Request("chat.delete", "u2", new string('0', 64))); }
        catch (MobileApiException error) { stale = error.Status == 409; }
        Check(version.Length == 64 && stale && Visible("u2"), "mobile_message_versions_reject_stale_deletion");
        var delete = Request("chat.delete", "u2", version);
        app.HandleMobileAction(delete); var replay = app.HandleMobileAction(delete);
        Check(!Visible("u2") && replay["replayed"]!.GetValue<bool>() && !replay["recordVersions"]!.AsObject().ContainsKey("message:u2"), "mobile_delete_retry_is_idempotent_and_hides_message_version");
        var restore = Request("chat.restore", "u2"); app.HandleMobileAction(restore);
        Check(Visible("u2") && app.HandleMobileAction(restore)["replayed"]!.GetValue<bool>(), "mobile_restore_retry_is_idempotent");
        Do("chat.delete", new { id = "u2" }); app.HandleMobileAction(restore);
        Check(!Visible("u2"), "old_restore_request_retry_cannot_undo_a_later_deletion");
        Do("chat.restore", new { id = "u2" });

        Do("chat.delete", new { id = "u1" });
        var backup = Path.Combine(dataDir, "archive-backup.zip"); Do("backup.export", new { path = backup });
        using (var zip = ZipFile.OpenRead(backup))
            Check(zip.GetEntry("attachments/" + attachmentName) != null, "backup_contains_deleted_message_original_attachment");
        Do("backup.import", new { path = backup });
        Check(!Visible("u1"), "backup_restore_preserves_hidden_message_state");
        Do("chat.restore", new { id = "u1" });
        Check(File.ReadAllText(app.ResolveAttachment("file1")!) == "不可丢失的原始文档", "undo_after_backup_restore_recovers_original_attachment");
        Check(!File.Exists(Path.Combine(actual, "secret.dat")) && !app.Snapshot()["settings"]!["aiConfigured"]!.GetValue<bool>(), "all_archive_checks_keep_real_model_disabled");
    }
}
