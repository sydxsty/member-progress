using System.Text.Json;
using System.Text.Json.Nodes;
using StudentProgress.Assistant;

namespace StudentProgress.Remote;

/// <summary>One isolated offline pass; no listeners, keys, notifications or model calls.</summary>
public static class MobileChecks
{
    public static int Run(string dataRoot)
    {
        var passed = new List<string>();
        try
        {
            if (Directory.Exists(dataRoot) && Directory.EnumerateFileSystemEntries(dataRoot).Any())
                throw new InvalidOperationException("Mobile checks require an empty isolated directory.");
            Directory.CreateDirectory(dataRoot);
            var coreRoot = Path.Combine(dataRoot, "core"); Directory.CreateDirectory(coreRoot);
            File.WriteAllText(Path.Combine(coreRoot, "initialized"), "1");
            void Check(bool ok, string label) { if (!ok) throw new InvalidOperationException(label); passed.Add(label); }
            static JsonObject Request(string action, JsonObject payload, string? version = null, string? id = null)
                => new() { ["action"] = action, ["payload"] = payload, ["requestId"] = id ?? Guid.NewGuid().ToString("N"), ["expectedVersion"] = version };
            static string Text(JsonNode? node) => node is JsonValue value && value.TryGetValue<string>(out var text) ? text : "";
            static string Version(AppService service, string type, string id) => Text(service.MobileSnapshot()["recordVersions"]![type + ":" + id]);
            static JsonObject Desktop(AppService service, string action, JsonObject payload)
                => service.HandleAsync(action, payload).GetAwaiter().GetResult()!.AsObject();
            void Reject(Action action, int status, string label)
            { bool rejected = false; try { action(); } catch (MobileApiException e) { rejected = e.Status == status; } Check(rejected, label); }
            string studentId, taskId; JsonObject createTask;
            using (var core = new AppService(coreRoot))
            {
                var state = Desktop(core, "student.save", new() { ["name"] = "离线成员甲" });
                studentId = Text(state["students"]![0]!["id"]);
                var projected = core.MobileSnapshot();
                Check(!projected.ContainsKey("settings") && !projected.ContainsKey("status") && !projected.ContainsKey("archivedStudents")
                    && !projected.ToJsonString().Contains(coreRoot), "mobile_projection_excludes_desktop_settings_and_paths");
                var version = Version(core, "student", studentId);
                Desktop(core, "student.patch", new() { ["id"] = studentId, ["field"] = "currentSummary", ["value"] = "电脑刚修改的进展" });
                Reject(() => core.HandleMobileAction(Request("student.patch", new() { ["id"] = studentId, ["field"] = "currentSummary", ["value"] = "旧手机输入" }, version)),
                    409, "stale_member_edit_rejected_inside_core_transaction");
                Check(Text(core.MobileSnapshot()["students"]![0]!["currentSummary"]) == "电脑刚修改的进展", "conflict_preserves_desktop_content");
                Reject(() => core.HandleMobileAction(Request("settings.save", new())), 400, "desktop_only_actions_not_exposed");
                Reject(() => core.HandleMobileAction(Request("chat.send", new() { ["scopeType"] = "student", ["scopeId"] = studentId, ["text"] = "text", ["files"] = new JsonArray() })),
                    400, "remote_archive_cannot_upload_local_file_payloads");
                createTask = Request("task.add", new() { ["scopeType"] = "student", ["scopeId"] = studentId, ["title"] = "离线任务", ["start"] = "2030-01-30", ["due"] = "2030-02-01" });
                core.HandleMobileAction(createTask); core.HandleMobileAction(createTask);
                Check(core.MobileSnapshot()["tasks"]!.AsArray().Count == 1, "replayed_create_does_not_duplicate_task");
                var modified = createTask.DeepClone().AsObject(); modified["payload"]!["title"] = "changed";
                Reject(() => core.HandleMobileAction(modified), 409, "same_request_id_with_changed_body_rejected");
                taskId = Text(core.MobileSnapshot()["tasks"]![0]!["id"]);
                var complete = Request("task.update", new() { ["id"] = taskId, ["done"] = true }, Version(core, "task", taskId));
                core.HandleMobileAction(complete); core.HandleMobileAction(complete);
                Check(core.MobileSnapshot()["tasks"]![0]!["done"]!.GetValue<bool>(), "task_completion_sets_explicit_state_once");
            }
            using (var core = new AppService(coreRoot))
            {
                core.HandleMobileAction(createTask);
                Check(core.MobileSnapshot()["tasks"]!.AsArray().Count == 1, "mutation_receipt_survives_restart");
                var deleted = Request("task.delete", new() { ["id"] = taskId }, Version(core, "task", taskId));
                core.HandleMobileAction(deleted);
                var restored = Request("task.restore", new() { ["id"] = taskId });
                core.HandleMobileAction(restored); core.HandleMobileAction(restored);
                Check(core.MobileSnapshot()["tasks"]!.AsArray().Count == 1, "delete_and_restore_remain_idempotent");
                static DateTimeOffset Local(int month, int day) { var local = new DateTime(2030, month, day, 9, 0, 0); return new(local, TimeZoneInfo.Local.GetUtcOffset(local)); }
                var recurring = Request("alarm.save", new() { ["scopeType"] = "student", ["studentId"] = studentId, ["title"] = "每月月底", ["when"] = Local(1, 31).ToString("O"), ["repeat"] = "monthly", ["enabled"] = true });
                core.HandleMobileAction(recurring); core.HandleMobileAction(recurring);
                Check(core.MobileSnapshot()["alarms"]!.AsArray().Count == 1, "reminder_create_deduplicates");
                var before = core.Snapshot().ToJsonString(); var plan = core.MobileReminderPlan(Local(1, 30));
                var occurrences = plan["occurrences"]!.AsArray().OfType<JsonObject>().ToArray();
                Check(before == core.Snapshot().ToJsonString() && occurrences.Any(o => DateTimeOffset.Parse(Text(o["when"])).LocalDateTime.Date == new DateTime(2030, 2, 28))
                    && occurrences.Any(o => DateTimeOffset.Parse(Text(o["when"])).LocalDateTime.Date == new DateTime(2030, 3, 31)), "read_only_plan_preserves_monthly_31st_anchor");
                var firstId = Text(occurrences[0]["id"]);
                core.HandleAsync("reminder.poll", new() { ["now"] = Local(1, 31).AddMinutes(1).ToString("O") }).GetAwaiter().GetResult();
                Check(core.MobileReminderPlan(Local(1, 31).AddMinutes(1))["occurrences"]!.AsArray().OfType<JsonObject>().Any(o => Text(o["id"]) == firstId),
                    "windows_claim_preserves_mobile_occurrence_identity");
                var alarmId = Text(core.MobileSnapshot()["alarms"]![0]!["id"]);
                core.HandleMobileAction(Request("alarm.enabled", new() { ["id"] = alarmId, ["enabled"] = false }, Version(core, "alarm", alarmId)));
                Check(!core.MobileReminderPlan(Local(2, 1).AddMinutes(2))["occurrences"]!.AsArray().Any(), "disabled_reminders_cancel_future_phone_schedule");
            }
            var assistantRoot = Path.Combine(dataRoot, "assistant-check"); int modelInvocations = 0;
            JsonObject Context(JsonObject _) => new() { ["generatedAt"] = "2030-01-30T09:00:00+08:00" };
            Task<string> Response(string _, CancellationToken __) { Interlocked.Increment(ref modelInvocations); return Task.FromResult("{\"text\":\"离线回答\",\"reminders\":[]}"); }
            static void Finish(AssistantService assistant)
            {
                var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                void Changed() { if (!assistant.MobileSnapshot()["busy"]!.GetValue<bool>()) completion.TrySetResult(); }
                assistant.StateChanged += Changed;
                try { Changed(); completion.Task.WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult(); }
                finally { assistant.StateChanged -= Changed; }
            }
            JsonObject send = Request("assistant.send", new() { ["text"] = "给出简单建议" });
            using (var assistant = new AssistantService(assistantRoot, dataRoot, Context, () => new(), null, Response))
            {
                assistant.HandleMobileAsync(send).GetAwaiter().GetResult(); Finish(assistant);
                assistant.HandleMobileAsync(send).GetAwaiter().GetResult();
                Check(modelInvocations == 1 && assistant.MobileSnapshot()["messages"]!.AsArray().Count == 2, "network_send_retry_does_not_call_model_twice");
                var oldVersion = Text(assistant.MobileSnapshot()["version"]);
                var clear = Request("assistant.clear", new(), oldVersion);
                assistant.HandleMobileAsync(clear).GetAwaiter().GetResult(); assistant.HandleMobileAsync(clear).GetAwaiter().GetResult();
                Check(assistant.MobileSnapshot()["messages"]!.AsArray().Count == 0 && assistant.MobileSnapshot()["canUndoClear"]!.GetValue<bool>(), "clear_is_recoverable_and_replay_safe");
                Reject(() => assistant.HandleMobileAsync(Request("assistant.undo-clear", new(), oldVersion)).GetAwaiter().GetResult(),
                    409, "stale_chat_controls_rejected");
                assistant.HandleMobileAsync(Request("assistant.undo-clear", new(), Text(assistant.MobileSnapshot()["version"]))).GetAwaiter().GetResult();
                Check(assistant.MobileSnapshot()["messages"]!.AsArray().Count == 2 && !assistant.MobileSnapshot().ContainsKey("runtime"), "chat_undo_restores_messages_without_runtime_details");
            }
            using (var assistant = new AssistantService(assistantRoot, dataRoot, Context, () => new(), null, Response))
            {
                assistant.HandleMobileAsync(send).GetAwaiter().GetResult();
                Check(modelInvocations == 1, "chat_request_receipt_survives_restart");
            }
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using (var assistant = new AssistantService(Path.Combine(dataRoot, "cancel-check"), dataRoot, Context, () => new(), null,
                async (_, token) => { entered.TrySetResult(); await Task.Delay(Timeout.Infinite, token); return ""; }))
            {
                assistant.HandleMobileAsync(Request("assistant.send", new() { ["text"] = "等待取消" })).GetAwaiter().GetResult();
                entered.Task.WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
                var cancel = Request("assistant.cancel", new(), Text(assistant.MobileSnapshot()["version"]));
                assistant.HandleMobileAsync(cancel).WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
                Check(!assistant.MobileSnapshot()["busy"]!.GetValue<bool>() && assistant.MobileSnapshot()["messages"]!.AsArray().Count == 1,
                    "mobile_cancel_stops_answer_but_preserves_question");
                assistant.HandleAsync("assistant.send", new() { ["text"] = "电脑上的新问题" }).GetAwaiter().GetResult();
                assistant.HandleMobileAsync(cancel).GetAwaiter().GetResult();
                Check(assistant.MobileSnapshot()["busy"]!.GetValue<bool>(), "replayed_cancel_does_not_cancel_new_desktop_question");
                assistant.HandleAsync("assistant.cancel", new()).WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
            }
            File.WriteAllText(Path.Combine(dataRoot, "mobile-result.json"), JsonSerializer.Serialize(new { ok = true, checks = passed, liveModelCalls = 0, listeners = 0 }, new JsonSerializerOptions { WriteIndented = true }));
            return 0;
        }
        catch (Exception e)
        {
            Directory.CreateDirectory(dataRoot);
            File.WriteAllText(Path.Combine(dataRoot, "mobile-result.json"), JsonSerializer.Serialize(new { ok = false, checks = passed, error = e.Message, liveModelCalls = 0 }, new JsonSerializerOptions { WriteIndented = true }));
            return 1;
        }
    }
}
