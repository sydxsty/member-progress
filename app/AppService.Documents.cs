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
                if (path == null || !File.Exists(path))
                { file["textStatus"] = "原附件暂时不可读取"; continue; }
                try
                {
                    string hash;
                    using (var source = File.OpenRead(path)) hash = Convert.ToHexString(await SHA256.HashDataAsync(source, cancellation).ConfigureAwait(false)).ToLowerInvariant();
                    string safeName = string.Concat(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c)).Trim().TrimEnd('.');
                    if (safeName.Length > 100) safeName = safeName[..80] + Path.GetExtension(stored);
                    if (safeName.Length == 0) safeName = "附件" + Path.GetExtension(stored);
                    string relative = $"attachments/{++index:0000}-{safeName}";
                    using (var source = File.OpenRead(path))
                    using (var target = new FileStream(Path.Combine(workspace, relative), FileMode.Create, FileAccess.Write))
                        await source.CopyToAsync(target, cancellation).ConfigureAwait(false);
                    file["file"] = relative; file["sha256"] = hash;
                    if (S(file, "mime").StartsWith("image/", StringComparison.OrdinalIgnoreCase) || Path.GetExtension(stored).ToLowerInvariant() is ".png" or ".jpg" or ".jpeg" or ".gif" or ".webp" or ".bmp" or ".avif")
                    {
                        file["textStatus"] = "图片原文件已提供，可直接查看图像内容；未预先提取文字";
                        coverage.Add(new JsonObject { ["id"] = S(file, "id"), ["name"] = name, ["status"] = S(file, "textStatus"), ["sha256"] = hash });
                        continue;
                    }
                    if (new FileInfo(path).Length > 100L * 1024 * 1024)
                    {
                        file["textStatus"] = "原文件已提供，可按需读取；较大文件不预先展开全文";
                        coverage.Add(new JsonObject { ["id"] = S(file, "id"), ["name"] = name, ["status"] = S(file, "textStatus"), ["sha256"] = hash });
                        continue;
                    }
                    byte[] bytes = await File.ReadAllBytesAsync(path, cancellation).ConfigureAwait(false);
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

    public async Task<JsonArray> WriteBusinessReferenceMaterialsAsync(JsonObject selection, string workspace, CancellationToken cancellation)
    {
        var captured = new List<(JsonObject Profile, JsonArray Tasks, JsonArray Messages)>();
        lock (gate)
        {
            foreach (var reference in (selection["references"] as JsonArray ?? new()).OfType<JsonObject>())
            {
                string type = S(reference, "scopeType"), id = S(reference, "scopeId");
                if (type == "wechat") continue;
                if (type is not ("student" or "project")) throw new InvalidOperationException("引用类型无效。");
                var scope = Rows(type == "student" ? "students" : "projects").FirstOrDefault(x => S(x, "id") == id)
                    ?? (type == "student" ? Rows("archivedStudents").FirstOrDefault(x => S(x, "id") == id) : null)
                    ?? throw new InvalidOperationException("引用的归档已不存在。");
                captured.Add((new JsonObject { ["scopeType"] = type, ["id"] = id, ["name"] = S(scope, "name"), ["bio"] = S(scope, "bio"), ["currentSummary"] = S(scope, "currentSummary") },
                    new JsonArray(Rows("tasks").Where(t => TaskInScope(t, type, id)).Select(t => t.DeepClone()).ToArray()),
                    new JsonArray(Rows("messages").Where(m => S(m, "scopeType") == type && S(m, "scopeId") == id).Select(m => m.DeepClone()).ToArray())));
            }
        }
        var manifest = new JsonArray();
        foreach (var item in captured)
        {
            string relative = "archives/" + (manifest.Count + 1).ToString("0000");
            await WriteArchiveWorkspaceAsync(Path.Combine(root, "attachments"), item.Profile, item.Tasks, item.Messages, Path.Combine(workspace, relative), cancellation).ConfigureAwait(false);
            manifest.Add(new JsonObject { ["scopeType"] = S(item.Profile, "scopeType"), ["scopeId"] = S(item.Profile, "id"), ["title"] = S(item.Profile, "name"), ["file"] = relative + "/archive.json" });
        }
        return manifest;
    }
}
