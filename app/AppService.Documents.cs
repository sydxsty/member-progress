using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using StudentProgress.Assistant;

namespace StudentProgress;

public sealed partial class AppService
{
    sealed record ArchiveSummarySnapshot(string Context, string Fingerprint, JsonObject Scope, JsonArray Messages, JsonArray Tasks, JsonArray SourceMessageIds);

    ArchiveSummarySnapshot CaptureSummary(string type, string id)
    {
        var scope = Scope(type, id);
        var messages = new JsonArray(Rows("messages").Where(m => S(m, "scopeType") == type && S(m, "scopeId") == id).Select(m => m.DeepClone()).ToArray());
        var tasks = new JsonArray(Rows("tasks").Where(t => TaskInScope(t, type, id)).Select(t => t.DeepClone()).ToArray());
        var profile = new JsonObject { ["scopeType"] = type, ["id"] = id, ["name"] = S(scope, "name"), ["bio"] = S(scope, "bio"), ["currentSummary"] = S(scope, "currentSummary") };
        var latest = messages.OfType<JsonObject>().LastOrDefault(m => S(m, "role") == "user");
        string context = "对象：" + S(scope, "name") + "\n此前进展（以原始材料为准）：" + Limit(S(scope, "currentSummary"), 1200) +
            "\n最近任务：" + string.Join("；", tasks.OfType<JsonObject>().TakeLast(5).Select(t => (B(t, "done") ? "已完成：" : "待办：") + Limit(S(t, "title"), 200))) +
            "\n最新归档文字（全文在 archive.json）：" + Limit(latest == null ? "" : S(latest, "text"), 5000);
        var fingerprint = new JsonObject { ["scope"] = profile.DeepClone(), ["messages"] = messages.DeepClone(), ["tasks"] = tasks.DeepClone() };
        return new(context, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(fingerprint.ToJsonString()))), profile, messages, tasks,
            new JsonArray(messages.OfType<JsonObject>().Where(m => S(m, "role") == "user").Select(m => JsonValue.Create(S(m, "id"))).ToArray()));
    }

    // Only copies from the captured archive are exposed to the agent; API settings and the live data path are not included.
    internal static async Task<JsonArray> WriteArchiveWorkspaceAsync(string attachmentRoot, JsonObject profile, JsonArray tasks, JsonArray history, string workspace, CancellationToken cancellation)
    {
        Directory.CreateDirectory(workspace);
        string filesRoot = Path.Combine(workspace, "attachments"); Directory.CreateDirectory(filesRoot);
        var messages = (JsonArray)history.DeepClone(); var coverage = new JsonArray(); int index = 0;
        foreach (var message in messages.OfType<JsonObject>())
        {
            foreach (var file in (message["attachments"] as JsonArray ?? new()).OfType<JsonObject>())
            {
                cancellation.ThrowIfCancellationRequested();
                string stored = S(file, "storedName"), name = S(file, "name");
                file.Remove("storedName"); file.Remove("extractedText");
                string? path = SafeStoredName(stored) ? Path.Combine(attachmentRoot, stored) : null;
                if (path == null || !File.Exists(path) || new FileInfo(path).Length > 20L * 1024 * 1024)
                { file["textStatus"] = "原附件暂时不可读取"; continue; }
                try
                {
                    byte[] bytes = await File.ReadAllBytesAsync(path, cancellation).ConfigureAwait(false);
                    string hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
                    string safeName = string.Concat(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c)).Trim().TrimEnd('.');
                    if (safeName.Length > 100) safeName = safeName[..80] + Path.GetExtension(stored);
                    if (safeName.Length == 0) safeName = "附件" + Path.GetExtension(stored);
                    string relative = $"attachments/{++index:0000}-{safeName}";
                    await File.WriteAllBytesAsync(Path.Combine(workspace, relative), bytes, cancellation).ConfigureAwait(false);
                    file["file"] = relative; file["sha256"] = hash;
                    var extracted = AttachmentText.ExtractFull(bytes, Path.GetExtension(stored), cancellation);
                    file["textStatus"] = extracted.Status;
                    if (extracted.Text.Length > 0)
                    {
                        string textFile = relative + ".extracted.txt";
                        await File.WriteAllTextAsync(Path.Combine(workspace, textFile), extracted.Text, new UTF8Encoding(false), cancellation).ConfigureAwait(false);
                        file["extractedTextFile"] = textFile;
                    }
                    coverage.Add(new JsonObject { ["id"] = S(file, "id"), ["name"] = name, ["status"] = extracted.Status, ["characters"] = extracted.Text.Length, ["sha256"] = hash });
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { file["textStatus"] = "原附件复制失败，当前仍可读取归档文字"; }
            }
        }
        cancellation.ThrowIfCancellationRequested();
        AssistantJson.WriteAtomic(Path.Combine(workspace, "archive.json"), new JsonObject { ["scope"] = profile.DeepClone(), ["tasks"] = tasks.DeepClone(), ["messages"] = messages });
        return coverage;
    }
}
