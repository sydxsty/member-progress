using System.Text.Json.Nodes;

namespace StudentProgress.Assistant;

/// <summary>Offline native action schemas and host receipts. No language-based authorization simulator.</summary>
public static class AssistantActionChecks
{
    public static int Run(string dataRoot)
    {
        var passed = new List<string>();
        try
        {
            if (Directory.Exists(dataRoot) && Directory.EnumerateFileSystemEntries(dataRoot).Any())
                throw new InvalidOperationException("Assistant action checks require an empty isolated directory.");
            Directory.CreateDirectory(dataRoot);
            void Check(bool ok, string name) { if (!ok) throw new InvalidOperationException(name); passed.Add(name); }
            void Reject(string input, string name)
            {
                bool rejected = false;
                try { AssistantResponse.Parse(input); } catch (InvalidOperationException) { rejected = true; }
                Check(rejected, name);
            }

            // Retained only to make old offline responseProvider fixtures readable.
            string fixture = new JsonObject { ["text"] = "", ["reminders"] = new JsonArray(new JsonObject
            { ["scopeType"] = "global", ["title"] = "核对进展", ["when"] = "2030-09-15T09:00:00+08:00", ["repeat"] = "once" }) }.ToJsonString();
            Check(AssistantResponse.Parse(fixture).Reminders.Count == 1 && AssistantResponse.Parse("```json\n" + fixture + "\n```").Reminders.Count == 1, "legacy_offline_fixture_reader");
            Reject("{\"text\":\"ok\",\"text\":\"other\",\"reminders\":[]}", "legacy_fixture_duplicate_fields_rejected");
            Reject(fixture.Replace("2030-09-15T09:00:00+08:00", "2030-09-15T09:00:00"), "legacy_fixture_timezone_required");
            Reject(fixture.Replace("\"repeat\":\"once\"", "\"repeat\":\"once\",\"command\":\"ignored\""), "legacy_fixture_unknown_action_fields_rejected");

            var writes = AssistantNativeTools.Writes(schedule: true, archive: true);
            var names = writes.OfType<JsonObject>().Select(t => AssistantJson.Text(t["name"])).ToHashSet(StringComparer.Ordinal);
            Check(names.SetEquals(new[] { "app_task_create", "app_reminder_create", "app_archive_append" }), "native_writes_are_explicit_tools");
            Check(AssistantNativeTools.Writes(false, false).Count == 0 && AssistantNativeTools.Writes(false, true).Count == 1, "native_catalog_tracks_host_capabilities");
            var normalized = CodexAppServer.NormalizeTools(writes);
            Check(normalized.All(t => AssistantJson.Text(t?["type"]) == "function") && !writes.OfType<JsonObject>().Any(t => t.ContainsKey("type")), "native_protocol_schemas_do_not_mutate_business_catalog");
            var task = writes.OfType<JsonObject>().Single(t => AssistantJson.Text(t["name"]) == "app_task_create")["inputSchema"]!.AsObject();
            var alarm = writes.OfType<JsonObject>().Single(t => AssistantJson.Text(t["name"]) == "app_reminder_create")["inputSchema"]!.AsObject();
            Check(task["required"]!.AsArray().Select(AssistantJson.Text).SequenceEqual(new[] { "title", "start", "due" }) &&
                alarm["required"]!.AsArray().Select(AssistantJson.Text).SequenceEqual(new[] { "title", "when" }), "date_tasks_and_clock_reminders_have_separate_native_schemas");

            var receipt = new JsonObject
            {
                ["reminders"] = new JsonArray(new JsonObject { ["id"] = "alarm-1", ["title"] = "每月核对", ["scopeType"] = "student", ["studentId"] = "self", ["repeat"] = "monthly", ["monthDay"] = 9 }),
                ["tasks"] = new JsonArray(new JsonObject { ["id"] = "task-1", ["title"] = "会议", ["start"] = "2030-10-22", ["due"] = "2030-10-24", ["studentIds"] = new JsonArray("self"), ["scopeLabel"] = "本人" })
            };
            var reminders = AssistantResponse.ReadReceipt(receipt); var tasks = AssistantResponse.ReadTaskReceipt(receipt);
            Check(reminders.Single().Id == "alarm-1" && reminders.Single().MonthDay == 9 && tasks.Single().Id == "task-1" && tasks.Single().StudentIds.SequenceEqual(new[] { "self" }), "host_receipt_fields_preserved_for_visible_cards");
            bool missingAlarmId = false, missingTaskId = false;
            var invalid = receipt.DeepClone().AsObject(); invalid["reminders"]![0]!.AsObject().Remove("id"); invalid["tasks"]![0]!.AsObject().Remove("id");
            try { AssistantResponse.ReadReceipt(invalid); } catch (InvalidOperationException) { missingAlarmId = true; }
            try { AssistantResponse.ReadTaskReceipt(invalid); } catch (InvalidOperationException) { missingTaskId = true; }
            Check(missingAlarmId && missingTaskId, "unsaved_records_cannot_be_shown_as_saved_cards");
            Check(AssistantResponse.ReadTaskReceipt(new JsonObject { ["reminders"] = new JsonArray() }).Count == 0, "old_reminder_only_receipts_stay_readable");
            AssistantJson.WriteAtomic(Path.Combine(dataRoot, "assistant-action-result.json"), new { ok = true, checks = passed, liveModelCalls = 0 }); return 0;
        }
        catch (Exception e)
        {
            Directory.CreateDirectory(dataRoot);
            AssistantJson.WriteAtomic(Path.Combine(dataRoot, "assistant-action-result.json"), new { ok = false, checks = passed, error = e.Message, liveModelCalls = 0 }); return 1;
        }
    }
}
