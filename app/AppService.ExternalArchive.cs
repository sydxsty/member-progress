using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace StudentProgress;

public sealed partial class AppService
{
    // In-process module boundary; never exposed as a file-path accepting WebView action.
    public JsonObject ImportWeChatArchive(JsonObject payload)
    {
        JsonObject result;
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            string requestId = Required(payload, "requestId", 500);
            string messageId = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("wechat:" + requestId))).ToLowerInvariant()[..32];
            var existing = AllArchiveMessages(state).FirstOrDefault(m => S(m, "id") == messageId);
            if (existing != null)
                return new JsonObject { ["messageId"] = messageId, ["scopeType"] = S(existing, "scopeType"), ["scopeId"] = S(existing, "scopeId"), ["replayed"] = true };
            string type = Required(payload, "scopeType"), id = Required(payload, "scopeId");
            Scope(type, id);
            string text = S(payload, "text");
            var files = payload["files"] as JsonArray ?? new();
            if (text.Length == 0 && files.Count == 0) throw new InvalidOperationException("没有可归档的微信内容。");
            var refs = new JsonArray();
            foreach (var reference in (payload["sourceReferences"] as JsonArray ?? new()).OfType<JsonObject>())
            {
                if (S(reference, "scopeType") != "wechat") throw new InvalidOperationException("微信来源引用无效。");
                refs.Add(new JsonObject { ["scopeType"] = "wechat", ["scopeId"] = Required(reference, "scopeId", 500), ["title"] = S(reference, "title") });
            }
            var attachments = new JsonArray();
            var staged = new List<string>();
            var previous = (JsonObject)state.DeepClone();
            try
            {
                foreach (var file in files.OfType<JsonObject>())
                {
                    string path = Path.GetFullPath(Required(file, "path", 32768));
                    string allowed = Path.Combine(root, "wechat") + Path.DirectorySeparatorChar;
                    if (!path.StartsWith(allowed, StringComparison.OrdinalIgnoreCase) || !File.Exists(path))
                        throw new InvalidOperationException("微信文件暂时不可读取，请重新同步后重试。");
                    for (var directory = new DirectoryInfo(Path.GetDirectoryName(path)!); directory != null && directory.FullName.StartsWith(allowed, StringComparison.OrdinalIgnoreCase); directory = directory.Parent)
                        if (directory.Attributes.HasFlag(FileAttributes.ReparsePoint)) throw new InvalidOperationException("微信文件位置无效。");
                    if (File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint)) throw new InvalidOperationException("微信文件位置无效。");
                    string name = Path.GetFileName(S(file, "name", "附件").Replace('\\', '/'));
                    string extension = Path.GetExtension(name);
                    if (extension.Length > 12 || extension.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '.')) extension = "";
                    string hash;
                    using (var source = File.OpenRead(path)) hash = Convert.ToHexString(SHA256.HashData(source)).ToLowerInvariant();
                    string stored = "wx-" + hash + extension;
                    string destination = Path.Combine(root, "attachments", stored);
                    if (!File.Exists(destination))
                    {
                        string temporary = destination + ".copying";
                        try { File.Copy(path, temporary, true); File.Move(temporary, destination, false); staged.Add(destination); }
                        finally { if (File.Exists(temporary)) File.Delete(temporary); }
                    }
                    var info = new FileInfo(destination);
                    attachments.Add(new JsonObject { ["id"] = NewId(), ["name"] = name, ["storedName"] = stored,
                        ["size"] = info.Length, ["mime"] = S(file, "mime", "application/octet-stream"), ["textStatus"] = "原文件已归档", ["extractedText"] = "" });
                }
                string time = DateTimeOffset.TryParse(S(payload, "createdAt"), out var at) ? at.ToString("O") : Now();
                var message = new JsonObject { ["id"] = messageId, ["scopeType"] = type, ["scopeId"] = id, ["role"] = "user",
                    ["text"] = text, ["createdAt"] = time, ["importedAt"] = Now(), ["sourceType"] = "wechat", ["sourceReferences"] = refs,
                    ["attachments"] = attachments, ["suggestions"] = new JsonArray() };
                int insert = 0;
                var messages = Arr("messages");
                while (insert < messages.Count && (!DateTimeOffset.TryParse(S((JsonObject)messages[insert]!, "createdAt"), out var itemTime) || itemTime <= DateTimeOffset.Parse(time))) insert++;
                messages.Insert(insert, message);
                Save();
                result = new JsonObject { ["messageId"] = messageId, ["scopeType"] = type, ["scopeId"] = id, ["replayed"] = false };
            }
            catch
            {
                state = previous;
                foreach (string path in staged) try { File.Delete(path); } catch { }
                throw;
            }
        }
        Notify();
        return result;
    }
}
