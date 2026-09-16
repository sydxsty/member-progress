using System.Text.Json.Nodes;

namespace StudentProgress.WeChat;

public sealed partial class WeChatService
{
    const int SuggestionPolicyVersion = 2;
    internal static JsonObject UpgradeSuggestionState(JsonObject original, DateTimeOffset now, int lookbackHours)
    {
        var next = original.DeepClone().AsObject();
        next["suggestions"] ??= new JsonArray(); next["analysisReceipts"] ??= new JsonObject();
        if (next["analysisReceipts"] is not JsonObject) throw new InvalidDataException("微信提醒核对记录格式无效。");
        next.Remove("lastImportedAt");
        if (I(next, "analysisPolicyVersion", 0) < SuggestionPolicyVersion)
        {
            var cutoff = now.AddHours(-lookbackHours);
            foreach (var item in next["suggestions"]!.AsArray().OfType<JsonObject>())
                if (Text(item["status"]) == "pending" && I(item, "policyVersion", 0) < SuggestionPolicyVersion &&
                    DateTimeOffset.TryParse(Text(item["sourceTime"]), out var sourceAt) && sourceAt < cutoff)
                {
                    item["popupSuppressed"] = true; item["suppressionReason"] = "legacy_window";
                }
            next["analysisPolicyVersion"] = SuggestionPolicyVersion;
        }
        return next;
    }

    internal static IReadOnlyList<WeChatAnalysisMessage> UnreviewedAnalysisMessages(IReadOnlyList<WeChatAnalysisMessage> candidates, JsonObject state, string currentUserId)
    {
        var receipts = state["analysisReceipts"] as JsonObject;
        return candidates.Where(m => Text(receipts?[WeChatStore.Stable(currentUserId, Text(m.Message["id"]))]?["fingerprint"]) != m.Fingerprint).ToArray();
    }

    // Called in the same atomic suggestions.json save as the validated suggestions. A failed
    // or interrupted batch never consumes messages. Identity is part of every receipt key.
    internal static void RecordSuccessfulAnalysis(JsonObject next, IReadOnlyList<WeChatAnalysisMessage> batch, string currentUserId, DateTimeOffset at)
    {
        var receipts = next["analysisReceipts"] as JsonObject ?? new();
        next["analysisReceipts"] ??= receipts;
        foreach (var message in batch)
        {
            string id = Text(message.Message["id"]);
            receipts[WeChatStore.Stable(currentUserId, id)] = new JsonObject
            {
                ["currentStudentId"] = currentUserId, ["messageId"] = id,
                ["fingerprint"] = message.Fingerprint, ["when"] = message.Message["when"]?.DeepClone(), ["checkedAt"] = at.ToString("O")
            };
        }
        // Retain the maximum configurable window, including receipts made under other users.
        // Widening the setting cannot immediately re-analyze already checked content.
        var cutoff = at.AddHours(-720);
        foreach (string key in receipts.Where(x => DateTimeOffset.TryParse(Text(x.Value?["when"]), out var when) && when < cutoff).Select(x => x.Key).ToArray())
            receipts.Remove(key);
        next["analysisPolicyVersion"] = SuggestionPolicyVersion;
    }
}
