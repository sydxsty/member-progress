using System.Text.Json;
using System.Text.Json.Nodes;

namespace StudentProgress.Assistant;

internal sealed class AssistantReference
{
    public string ScopeType { get; set; } = "student";
    public string ScopeId { get; set; } = "";
    public string Title { get; set; } = "";
}

internal sealed class AssistantMessage
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Role { get; set; } = "user";
    public string Text { get; set; } = "";
    public string CreatedAt { get; set; } = DateTimeOffset.Now.ToString("O");
    public List<AssistantReference> References { get; set; } = [];
    public bool IncludeReminders { get; set; } = true;
    public bool IncludeRecent { get; set; } = true;
    public string ReplyToMessageId { get; set; } = "";
    public List<AssistantReminderResult> ReminderResults { get; set; } = [];
    public List<AssistantTaskResult> TaskResults { get; set; } = [];
    // Persisted locally for the next immediate clarification; stripped from UI snapshots.
    public bool AwaitingReminderDetails { get; set; }
    public string ReminderRequestText { get; set; } = "";
}

internal sealed class AssistantReminderResult
{
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    public string When { get; set; } = "";
    public string ScopeType { get; set; } = "global";
    public string StudentId { get; set; } = "";
    public string ProjectId { get; set; } = "";
    public string ScopeLabel { get; set; } = "全局";
    public string Repeat { get; set; } = "once";
    public int? MonthDay { get; set; }
}

internal sealed class AssistantTaskResult
{
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    public string Start { get; set; } = "";
    public string Due { get; set; } = "";
    public List<string> StudentIds { get; set; } = [];
    public string ProjectId { get; set; } = "";
    public string ScopeLabel { get; set; } = "";
}

internal sealed class AssistantState
{
    public int SchemaVersion { get; set; } = 1;
    public List<AssistantMessage> Messages { get; set; } = [];
    [System.Text.Json.Serialization.JsonIgnore]
    public bool AutoUpdate { get; set; }
    public string Error { get; set; } = "";
    public bool Interrupted { get; set; }
    // The recoverable cleared conversation is kept in the same atomic history file.
    public List<AssistantMessage> ClearedMessages { get; set; } = [];
    public string ClearedError { get; set; } = "";
    // Kept outside Messages so clearing/restarting cannot replay a mobile write.
    public List<AssistantMobileReceipt> MobileReceipts { get; set; } = [];
}

internal sealed class AssistantMobileReceipt
{
    public string RequestId { get; set; } = "";
    public string Fingerprint { get; set; } = "";
}

internal static class AssistantJson
{
    internal static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };
    internal static string Text(JsonNode? node) => node is JsonValue value && value.TryGetValue<string>(out var s) ? s : "";
    internal static JsonObject Object(object value) => JsonSerializer.SerializeToNode(value, Options)!.AsObject();
    internal static void WriteAtomic(string path, object value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var file = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(file, value, Options);
                file.Flush(flushToDisk: true);
            }
            File.Move(temp, path, overwrite: true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
}

internal sealed class AssistantStore
{
    private readonly string path;
    private readonly string settingsPath;
    internal AssistantStore(string moduleRoot)
    {
        path = Path.Combine(moduleRoot, "history.json");
        settingsPath = Path.Combine(moduleRoot, "settings.json");
    }
    internal AssistantState Load()
    {
        if (!File.Exists(path)) return new() { AutoUpdate = ReadAutoUpdate() };
        try
        {
            var state = JsonSerializer.Deserialize<AssistantState>(File.ReadAllText(path), AssistantJson.Options)
                ?? throw new InvalidDataException("聊天记录为空。");
            if (state.SchemaVersion != 1 || state.Messages == null)
                throw new InvalidDataException("不支持的聊天记录版本。");
            if (state.MobileReceipts == null || state.MobileReceipts.Any(r => r == null || r.RequestId.Length == 0 || r.Fingerprint.Length != 64))
                throw new InvalidDataException("手机聊天的保存记录格式无效。");
            state.AutoUpdate = ReadAutoUpdate();
            if (state.Interrupted)
            {
                state.Interrupted = false;
                state.Error = "上次回答因应用退出而中断，问题已保留，可以重试。";
                SaveHistory(state);
            }
            return state;
        }
        catch (Exception e) when (e is JsonException or InvalidDataException)
        {
            // Preserve the unreadable file; never replace it with an empty conversation.
            throw new InvalidDataException("GPT 聊天记录暂时无法读取，原文件已保留。", e);
        }
    }
    internal void Save(AssistantState state)
    {
        SaveSettings(state.AutoUpdate);
        SaveHistory(state);
    }
    internal void SaveHistory(AssistantState state) => AssistantJson.WriteAtomic(path, state);
    internal void SaveSettings(bool autoUpdate) => AssistantJson.WriteAtomic(settingsPath, new {schemaVersion = 1, autoUpdate});
    private bool ReadAutoUpdate()
    {
        if (!File.Exists(settingsPath)) return false;
        var settings = JsonNode.Parse(File.ReadAllText(settingsPath)) as JsonObject;
        return settings?["autoUpdate"]?.GetValue<bool>() ?? false;
    }
}
