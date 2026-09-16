using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using StudentProgress.Assistant;

namespace StudentProgress.WeChat;

internal static class WeChatSuggestionRevisionChecks
{
    internal static async Task<JsonObject> RunAsync(string directory, string baseDirectory)
    {
        if (Directory.Exists(directory) && Directory.EnumerateFileSystemEntries(directory).Any()) throw new InvalidOperationException("Suggestion checks require an empty isolated directory.");
        Directory.CreateDirectory(directory); var checks = new JsonArray();
        void Check(bool ok, string name) { if (!ok) throw new InvalidOperationException(name); checks.Add(name); }
        bool RejectHours(JsonNode value) { try { WeChatService.NormalizeReminderLookbackHours(value); return false; } catch (InvalidDataException) { return true; } }
        Check(WeChatService.NormalizeReminderLookbackHours(null) == 24 && WeChatService.NormalizeReminderLookbackHours(JsonValue.Create(1)) == 1 && WeChatService.NormalizeReminderLookbackHours(JsonValue.Create(720)) == 720 &&
            RejectHours(JsonValue.Create(0)!) && RejectHours(JsonValue.Create(721)!) && RejectHours(JsonValue.Create("24")!), "lookback_defaults_and_exact_integer_bounds");

        var now = DateTimeOffset.Now;
        var oldState = new JsonObject { ["lastImportedAt"] = now.ToString("O"), ["suggestions"] = new JsonArray(
            new JsonObject { ["id"] = "old", ["status"] = "pending", ["sourceTime"] = now.AddDays(-3).ToString("O") },
            new JsonObject { ["id"] = "created", ["status"] = "created", ["sourceTime"] = now.AddDays(-3).ToString("O") },
            new JsonObject { ["id"] = "dismissed", ["status"] = "dismissed", ["sourceTime"] = now.AddDays(-3).ToString("O") },
            new JsonObject { ["id"] = "new-future", ["status"] = "pending", ["policyVersion"] = 2, ["sourceTime"] = now.AddDays(-3).ToString("O"), ["when"] = now.AddDays(5).ToString("O") }) };
        var upgraded = WeChatService.UpgradeSuggestionState(oldState, now, 24);
        var old = upgraded["suggestions"]![0]!;
        Check(old["popupSuppressed"]!.GetValue<bool>() && AssistantJson.Text(old["status"]) == "pending" &&
            upgraded["suggestions"]![1]!["popupSuppressed"] == null && upgraded["suggestions"]![2]!["popupSuppressed"] == null && upgraded["suggestions"]![3]!["popupSuppressed"] == null &&
            !upgraded.ContainsKey("lastImportedAt") && oldState["suggestions"]![0]!["popupSuppressed"] == null, "one_time_legacy_popup_migration_preserves_records_and_future_new_policy_items");
        old.AsObject().Remove("popupSuppressed"); old.AsObject().Remove("suppressionReason");
        Check(WeChatService.UpgradeSuggestionState(upgraded, now.AddDays(4), 24)["suggestions"]![0]!["popupSuppressed"] == null, "restored_legacy_suggestion_not_suppressed_again_by_reload");

        string migrationRoot = Path.Combine(directory, "settings-compat"); Directory.CreateDirectory(Path.Combine(migrationRoot, "wechat"));
        var legacyConfig = new JsonObject { ["enabled"] = false, ["checkReminders"] = false, ["syncMinutes"] = 1, ["archiveFrom"] = "all", ["selectedConversationIds"] = new JsonArray(), ["bindings"] = new JsonArray() };
        AssistantJson.WriteAtomic(Path.Combine(migrationRoot, "wechat", "config.json"), legacyConfig);
        using (var compatibility = new WeChatService(migrationRoot, baseDirectory, () => new(), _ => new()))
        {
            Check(compatibility.Configuration["reminderLookbackHours"]!.GetValue<int>() == 24 && File.Exists(Path.Combine(migrationRoot, "wechat", "config.pre-v16.json")), "old_settings_upgrade_preserves_pre_v16_copy");
            compatibility.SaveConfiguration(new JsonObject { ["reminderLookbackHours"] = 72 });
            Check(compatibility.Configuration["reminderLookbackHours"]!.GetValue<int>() == 72, "lookback_setting_saved_without_overwriting_other_settings");
            string backup = Path.Combine(directory, "settings-backup.json"); AssistantJson.WriteAtomic(backup, compatibility.Configuration); WeChatService.ValidateSettingsBackup(backup);
            var invalid = compatibility.Configuration; invalid["reminderLookbackHours"] = 721; AssistantJson.WriteAtomic(backup, invalid);
            bool rejected = false; try { WeChatService.ValidateSettingsBackup(backup); } catch (InvalidDataException) { rejected = true; }
            Check(rejected, "backup_rejects_invalid_lookback");
        }

        string root = Path.Combine(directory, "service");
        var context = new JsonObject { ["generatedAt"] = now.ToString("O"), ["currentUser"] = new JsonObject { ["id"] = "self", ["name"] = "测试本人" }, ["tasks"] = new JsonArray(), ["reminders"] = new JsonArray() };
        using var service = new WeChatService(root, baseDirectory, () => new(), _ => throw new InvalidOperationException("Checks never archive business data."));
        service.ConfigureAgent(() => context.DeepClone().AsObject(), () => new(), _ => throw new InvalidOperationException("Checks never create business reminders."));
        var store = service.Store; const string conversation = "selected-chat";
        store.SaveConversation(new JsonObject { ["id"] = conversation, ["accountId"] = "account", ["externalId"] = "friend", ["name"] = "测试会话", ["kind"] = "friend" });
        service.SaveConfiguration(new JsonObject { ["enabled"] = false, ["checkReminders"] = false, ["selectedConversationIds"] = new JsonArray(conversation) });
        JsonObject Message(string id, DateTimeOffset when, bool self = false, string kind = "text") => new()
        { ["id"] = id, ["accountId"] = "account", ["conversationId"] = conversation, ["senderId"] = self ? "me" : "friend", ["senderName"] = self ? "本人" : "对方", ["isSelf"] = self, ["when"] = when.ToString("O"), ["kind"] = kind, ["text"] = "明天下午开会", ["rawContent"] = "原始正文" };
        var received = Message("received", now.AddHours(-2)); store.UpsertMessage(received);
        store.UpsertMessage(Message("old-backfill", now.AddHours(-48))); store.UpsertMessage(Message("sent", now.AddHours(-1), true)); store.UpsertMessage(Message("future", now.AddHours(2)));
        var noTranscript = Message("voice-empty", now.AddMinutes(-30), kind: "voice"); noTranscript["text"] = "[语音]"; noTranscript["voiceTranscriptStatus"] = "no_text"; store.UpsertMessage(noTranscript);
        var unknownTranscript = Message("voice-unknown", now.AddMinutes(-25), kind: "voice"); unknownTranscript["voiceTranscript"] = "不应作为原文"; unknownTranscript["voiceTranscriptStatus"] = "unknown"; store.UpsertMessage(unknownTranscript);
        var transcript = Message("voice-ready", now.AddMinutes(-20), kind: "voice"); transcript["voiceTranscript"] = "明天带上项目报告"; transcript["voiceTranscriptStatus"] = "completed"; store.UpsertMessage(transcript);
        var initial = service.AnalysisMessages(now.AddHours(-24), now);
        Check(initial.Select(x => AssistantJson.Text(x.Message["id"])).ToHashSet().SetEquals(new[] { "received", "voice-ready" }), "window_received_and_completed_local_transcript_only");
        Check(service.AnalysisMessages(now.AddHours(-72), now).Count == 3, "wider_window_includes_recently_imported_older_received_message");

        var fingerprintFixture = received.DeepClone().AsObject(); fingerprintFixture["attachments"] = new JsonArray(new JsonObject
        { ["id"] = "file", ["type"] = "file", ["hash"] = "original-hash", ["available"] = true, ["sourcePath"] = "old", ["sourceWriteTicks"] = 1, ["name"] = "旧显示名" });
        string fingerprint = WeChatStore.SuggestionFingerprint(fingerprintFixture);
        fingerprintFixture["source"] = new JsonObject { ["database"] = "new-cache" }; fingerprintFixture["senderName"] = "备注变化";
        var metadata = fingerprintFixture["attachments"]![0]!.AsObject(); metadata["sourcePath"] = "new"; metadata["sourceWriteTicks"] = 2; metadata["name"] = "新显示名"; metadata["previewUrl"] = "changed";
        Check(WeChatStore.SuggestionFingerprint(fingerprintFixture) == fingerprint, "cache_paths_timestamps_display_names_do_not_retrigger_analysis");
        metadata["hash"] = "changed-original";
        Check(WeChatStore.SuggestionFingerprint(fingerprintFixture) != fingerprint, "actual_original_hash_changes_analysis_fingerprint");
        var rawChange = received.DeepClone().AsObject(); rawChange["rawContent"] = "分享卡片实际变化";
        Check(WeChatStore.SuggestionFingerprint(rawChange) != WeChatStore.SuggestionFingerprint(received), "original_rich_message_content_changes_fingerprint");
        var receiptState = WeChatService.UpgradeSuggestionState(new JsonObject(), now, 24);
        WeChatService.RecordSuccessfulAnalysis(receiptState, initial, "self", now);
        Check(WeChatService.UnreviewedAnalysisMessages(initial, receiptState, "self").Count == 0 && WeChatService.UnreviewedAnalysisMessages(initial, receiptState, "other-user").Count == 2, "checked_content_receipts_are_bound_to_current_user");

        JsonObject Saved() => JsonNode.Parse(File.ReadAllText(Path.Combine(root, "wechat", "suggestions.json")))!.AsObject();
        async Task Run() { await service.HandleAsync("wechat.suggestions.check", new()); await service.SuggestionCheckCompletion.WaitAsync(TimeSpan.FromSeconds(10)); }
        var phases = new ConcurrentQueue<string>(); service.AnalysisProgressChanged += () => phases.Enqueue(AssistantJson.Text(service.AnalysisProgressSnapshot()["phase"]));
        int simulatedCalls = 0;
        service.SuggestionResponseProvider = (_, _) => { simulatedCalls++; throw new InvalidOperationException("离线测试失败"); };
        await Run();
        Check(Saved()["analysisReceipts"]!.AsObject().Count == 0 && AssistantJson.Text(service.AnalysisProgressSnapshot()["phase"]) == "error", "failed_analysis_does_not_consume_messages_and_emits_error");
        service.SuggestionResponseProvider = (_, _) => { simulatedCalls++; return Task.FromResult("{\"suggestions\":[{\"title\":\"不合法的旧消息建议\",\"kind\":\"task\",\"sourceMessageIds\":[\"old-backfill\"]}],\"resolvedSuggestionIds\":[]}"); };
        await Run();
        Check(Saved()["analysisReceipts"]!.AsObject().Count == 0 && Saved()["suggestions"]!.AsArray().Count == 0, "suggestion_from_outside_fixed_batch_is_rejected");

        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        service.SuggestionResponseProvider = async (_, ct) => { simulatedCalls++; entered.TrySetResult(); await Task.Delay(Timeout.InfiniteTimeSpan, ct); return ""; };
        await service.HandleAsync("wechat.suggestions.check", new()); await entered.Task.WaitAsync(TimeSpan.FromSeconds(10)); await service.CancelSuggestionsAsync();
        Check(Saved()["analysisReceipts"]!.AsObject().Count == 0 && AssistantJson.Text(service.AnalysisProgressSnapshot()["phase"]) == "cancelled", "cancelled_analysis_does_not_consume_messages");

        service.SuggestionResponseProvider = (workspace, _) =>
        {
            simulatedCalls++; var window = JsonNode.Parse(File.ReadAllText(Path.Combine(workspace, "analysis-window.json")))!.AsObject();
            if (window["messageCount"]!.GetValue<int>() != 2) throw new InvalidOperationException("Initial fixed batch should contain exactly two messages.");
            store.UpsertMessage(Message("arrived-during-analysis", now.AddMinutes(-1)));
            return Task.FromResult(new JsonObject { ["suggestions"] = new JsonArray(new JsonObject
            { ["title"] = "准备项目报告", ["kind"] = "task", ["start"] = DateTime.Today.AddDays(1).ToString("yyyy-MM-dd"), ["due"] = DateTime.Today.AddDays(1).ToString("yyyy-MM-dd"), ["sourceMessageIds"] = new JsonArray("received") }), ["resolvedSuggestionIds"] = new JsonArray() }.ToJsonString());
        };
        await Run();
        var success = Saved(); var successfulProgress = service.AnalysisProgressSnapshot();
        Check(success["analysisReceipts"]!.AsObject().Count == 2 && success["suggestions"]!.AsArray().Count == 1 && successfulProgress["suggestionCount"]!.GetValue<int>() == 1 && successfulProgress["preparedMessages"]!.GetValue<long>() == 2,
            "suggestions_and_checked_fingerprints_saved_together");
        Check(WeChatService.UnreviewedAnalysisMessages(service.AnalysisMessages(now.AddHours(-24), DateTimeOffset.Now), success, "self").Single().Message["id"]!.GetValue<string>() == "arrived-during-analysis", "messages_arriving_during_model_check_wait_for_next_batch");
        service.SuggestionResponseProvider = (_, _) => { simulatedCalls++; return Task.FromResult("{\"suggestions\":[],\"resolvedSuggestionIds\":[]}"); };
        await Run(); int afterCompleted = simulatedCalls; await Run();
        Check(simulatedCalls == afterCompleted && service.AnalysisProgressSnapshot()["messageCount"]!.GetValue<int>() == 0, "unchanged_successful_messages_do_not_call_model_again");
        Check(phases.Contains("selecting") && phases.Contains("preparing") && phases.Contains("analyzing") && phases.Contains("saving") && phases.Contains("completed") &&
            !service.AnalysisProgressSnapshot().ContainsKey("percent"), "analysis_progress_uses_real_stages_and_counts_without_fake_percent");
        string suggestionId = success["suggestions"]![0]!["id"]!.GetValue<string>();
        var later = WeChatService.UpgradeSuggestionState(Saved(), now.AddDays(3), 24);
        Check(later["suggestions"]!.AsArray().OfType<JsonObject>().Single(x => AssistantJson.Text(x["id"]) == suggestionId)["popupSuppressed"] == null, "new_suggestion_not_hidden_only_because_source_gets_older");
        return new JsonObject { ["ok"] = true, ["count"] = checks.Count, ["checks"] = checks, ["modelCalls"] = 0 };
    }
}
