using System.Security.Cryptography;
using System.Text.Json;

namespace StudentProgress.Assistant;

internal sealed class AssistantAttachment
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Mime { get; set; } = "application/octet-stream";
    public long Size { get; set; }
    public string StoredName { get; set; } = "";
    public string Sha256 { get; set; } = "";

    internal static bool SafeStoredName(string name) => name.Length is > 0 and < 100 && !name.Contains("..")
        && name.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_');
}

// The attachment directory is deliberately the only new backup prefix; native runtime, session
// databases and credentials are never collected. The manifest includes undoable cleared messages.
internal static class AssistantAttachmentBackup
{
    internal const string Prefix = "assistant/files/";
    internal static bool IsAttachmentPath(string relative) => relative.StartsWith(Prefix, StringComparison.Ordinal)
        && AssistantAttachment.SafeStoredName(relative[Prefix.Length..]);

    internal static void Validate(string stage)
    {
        string history = Path.Combine(stage,"assistant","history.json");
        if (!File.Exists(history))
        {
            string files = Path.Combine(stage,"assistant","files");
            if (Directory.Exists(files) && Directory.EnumerateFiles(files).Any()) throw new InvalidDataException("备份缺少 GPT 附件对应的会话记录。");
            return;
        }
        using var document = JsonDocument.Parse(File.ReadAllText(history));
        foreach (string list in new[] { "messages", "clearedMessages" })
        {
            if (!document.RootElement.TryGetProperty(list,out var messages)) continue;
            if (messages.ValueKind != JsonValueKind.Array) throw new InvalidDataException("GPT 会话记录格式无效。");
            foreach (var message in messages.EnumerateArray())
            {
                if (!message.TryGetProperty("attachments",out var attachments)) continue;
                if (attachments.ValueKind != JsonValueKind.Array) throw new InvalidDataException("GPT 附件记录格式无效。");
                foreach (var file in attachments.EnumerateArray())
                {
                    string stored=file.TryGetProperty("storedName",out var n)&&n.ValueKind==JsonValueKind.String?n.GetString()??"":"";
                    if (!AssistantAttachment.SafeStoredName(stored)) throw new InvalidDataException("GPT 附件路径无效。");
                    string path=Path.Combine(stage,"assistant","files",stored);
                    if(!File.Exists(path))throw new InvalidDataException("备份缺少 GPT 聊天附件，尚未恢复。");
                    if(!file.TryGetProperty("size",out var size)||!size.TryGetInt64(out var expectedSize)||new FileInfo(path).Length!=expectedSize)
                        throw new InvalidDataException("GPT 聊天附件大小不一致，尚未恢复。");
                    if(file.TryGetProperty("sha256",out var hash)&&hash.ValueKind==JsonValueKind.String&&hash.GetString() is {Length:>0} expectedHash)
                    {
                        using var stream=File.OpenRead(path);
                        if(!Convert.ToHexString(SHA256.HashData(stream)).Equals(expectedHash,StringComparison.OrdinalIgnoreCase))
                            throw new InvalidDataException("GPT 聊天附件校验失败，尚未恢复。");
                    }
                }
            }
        }
    }
}
