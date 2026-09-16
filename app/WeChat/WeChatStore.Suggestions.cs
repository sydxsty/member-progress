using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace StudentProgress.WeChat;

internal sealed record WeChatAnalysisMessage(JsonObject Message, string Fingerprint);

internal sealed partial class WeChatStore
{
    // Select the batch under one store lock. Import timestamps, index revisions and local
    // cache repairs are deliberately not eligibility signals for reminder analysis.
    internal IReadOnlyList<WeChatAnalysisMessage> SuggestionMessages(ISet<string> selected, DateTimeOffset from, DateTimeOffset through)
    {
        lock (gate)
        {
            using var command = Command("SELECT id FROM messages WHERE whenMs >= $from AND whenMs <= $through ORDER BY whenMs,id",
                ("$from", from.ToUnixTimeMilliseconds()), ("$through", through.ToUnixTimeMilliseconds()));
            using var reader = command.ExecuteReader(); var ids = new List<string>();
            while (reader.Read()) ids.Add(reader.GetString(0)); reader.Close();
            var result = new List<WeChatAnalysisMessage>();
            foreach (string id in ids)
            {
                var message = Message(id, includeHostPaths: true);
                if (message == null || !selected.Contains(S(message, "conversationId")) ||
                    message["isSelf"] is JsonValue own && own.TryGetValue<bool>(out bool isSelf) && isSelf) continue;
                // Existing local WeChat transcripts are useful evidence; raw voice bytes are
                // not an invitation to perform ASR in a background reminder check.
                if (S(message, "kind") == "voice" && (S(message, "voiceTranscriptStatus") != "completed" || string.IsNullOrWhiteSpace(S(message, "voiceTranscript")))) continue;
                string fingerprint = SuggestionFingerprint(message);
                message["conversationName"] = S(Conversation(S(message, "conversationId")), "name");
                result.Add(new(message, fingerprint));
            }
            return result;
        }
    }

    internal static string SuggestionFingerprint(JsonObject message)
    {
        var content = new JsonObject();
        foreach (string field in new[] { "accountId", "conversationId", "senderId", "when", "kind", "text", "rawContent", "voiceTranscript" })
            content[field] = S(message, field);
        content["files"] = new JsonArray((message["attachments"] as JsonArray ?? new()).OfType<JsonObject>()
            .OrderBy(f => S(f, "id"), StringComparer.Ordinal).Select(file => (JsonNode?)new JsonObject
            {
                ["id"] = S(file, "id"), ["type"] = S(file, "type"),
                ["hash"] = S(file, "originalHash") is { Length: >0 } original ? original : S(file, "hash"),
                ["readable"] = file["available"] is JsonValue available && available.TryGetValue<bool>(out bool exists) && exists && S(file, "status") != "encrypted"
            }).ToArray());
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content.ToJsonString()))).ToLowerInvariant();
    }
}
