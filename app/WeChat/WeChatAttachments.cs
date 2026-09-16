using System.Security.Cryptography;
using System.Text.Json.Nodes;

namespace StudentProgress.WeChat;

public sealed partial class WeChatService
{
    internal async Task ImportFilesAsync(JsonObject raw, JsonObject message, CancellationToken ct)
    {
        var previous = (store.Message(S(message, "id"), true)?["attachments"] as JsonArray ?? new()).OfType<JsonObject>().ToArray();
        var claimed = new HashSet<string>(); int ordinal = 0;
        foreach (var original in (raw["attachments"] as JsonArray ?? new()).OfType<JsonObject>())
        {
            ct.ThrowIfCancellationRequested();
            string name = Path.GetFileName(S(original, "name", "文件")), readerId = S(original, "id"), type = S(original, "type");
            string legacyId = WeChatStore.Stable(S(message, "id"), name, (ordinal++).ToString());
            var old = previous.FirstOrDefault(f => !claimed.Contains(S(f, "id")) &&
                (readerId.Length > 0 && S(f, "readerId") == readerId || S(f, "id") == legacyId));
            // v11/v12 used the file name as identity. Image decoding changes the extension,
            // so carry the single image attachment forward rather than creating a second file.
            if (old == null && type == "image")
            {
                var images = previous.Where(f => S(f, "type") == "image" && !claimed.Contains(S(f, "id"))).ToArray();
                if (images.Length == 1) old = images[0];
            }
            string id = old != null ? S(old, "id") : legacyId;
            claimed.Add(id);
            string src = S(original, "path"), incomingStatus = S(original, "status");
            bool oldExists = old != null && File.Exists(StoredPath(S(old, "storedName")));
            if (oldExists && (src.Length == 0 || !File.Exists(src))) continue;
            // A temporary key/cache miss must never replace a decoded local image with ciphertext.
            if (oldExists && S(old, "mime").StartsWith("image/") && incomingStatus == "encrypted") continue;
            var sourceInfo = src.Length > 0 && File.Exists(src) ? new FileInfo(src) : null;
            long sourceStamp = sourceInfo?.LastWriteTimeUtc.Ticks ?? 0;
            if (oldExists && S(old, "sourcePath") == src && Long(old!, "sourceWriteTicks", 0) == sourceStamp &&
                Long(old!, "sourceSize", -1) == sourceInfo?.Length && S(old, "readerId") == readerId &&
                S(old, "status") == incomingStatus && S(old, "decodeStatus") == S(original, "decodeStatus")) continue;

            var f = new JsonObject { ["id"] = id, ["messageId"] = S(message, "id"), ["readerId"] = readerId,
                ["name"] = name, ["size"] = Long(original, "size", 0), ["type"] = type,
                ["status"] = incomingStatus, ["decodeStatus"] = S(original, "decodeStatus"),
                ["isThumbnail"] = B(original, "isThumbnail"), ["sourceWriteTicks"] = sourceStamp,
                ["sourceSize"] = sourceInfo?.Length ?? 0 };
            string stored = "", hash = "", extracted = "";
            if (sourceInfo != null)
            {
                try
                {
                    (stored, hash, var size) = await CopyAttachmentAsync(src, name, ct); f["size"] = size;
                    var dest = StoredPath(stored); string mime = MediaPreview.Mime(dest);
                    if (mime.Length > 0) { f["mime"] = mime; f["status"] = "ready"; }
                    var ext = Path.GetExtension(name).ToLowerInvariant();
                    if (old != null && S(old,"hash") == hash) extracted=S(old,"extractedText");
                    else if (size <= 64L * 1024 * 1024 && new[] { ".txt", ".md", ".markdown", ".csv", ".pdf", ".docx", ".pptx" }.Contains(ext))
                        extracted = AttachmentText.ExtractFull(await File.ReadAllBytesAsync(dest, ct), ext, ct).Text;
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
            if (oldExists && stored.Length == 0) continue;
            // Keep the encrypted original in the WeChat-only library and its separate export.
            foreach (string field in new[] { "originalStoredName", "originalName", "originalHash", "originalSize" })
                if (old?[field] != null) f[field] = old[field]!.DeepClone();
            if (oldExists && S(old, "status") == "encrypted" && S(f, "mime").Length > 0 && S(f, "originalStoredName").Length == 0)
            {
                f["originalStoredName"] = S(old, "storedName"); f["originalHash"] = S(old, "hash");
                f["originalName"] = S(old, "name"); f["originalSize"] = old!["size"]?.DeepClone();
            }
            string originalPath = S(original, "originalPath");
            string originalName = Path.GetFileName(S(original, "originalName", Path.GetFileName(originalPath)));
            bool originalNeedsUpdate = S(f, "originalStoredName").Length == 0 || !File.Exists(StoredPath(S(f, "originalStoredName"))) ||
                S(f, "originalName") != originalName || Long(f, "originalSize", -1) != Long(original, "originalSize", -2);
            if (originalPath.Length > 0 && File.Exists(originalPath) && originalNeedsUpdate)
            {
                try
                {
                    // A later high-resolution copy becomes the referenced original. Previously
                    // retained ciphertext remains in the content-addressed library and exports.
                    var originalCopy = await CopyAttachmentAsync(originalPath, originalName, ct);
                    f["originalStoredName"] = originalCopy.Stored; f["originalHash"] = originalCopy.Hash;
                    f["originalName"] = originalName; f["originalSize"] = originalCopy.Size;
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
            f["available"] = stored.Length > 0;
            store.UpsertFile(f, src, stored, hash, extracted);
        }
    }

    async Task<(string Stored, string Hash, long Size)> CopyAttachmentAsync(string source, string name, CancellationToken ct)
    {
        var temporary = Path.Combine(root, "files", Guid.NewGuid().ToString("N") + ".copying");
        try
        {
            long size;
            await using (var from = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 81920, true))
            await using (var to = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true))
            {
                long length = from.Length; var stamp = File.GetLastWriteTimeUtc(source);
                await from.CopyToAsync(to, ct);
                if (from.Length != length || to.Length != length || File.GetLastWriteTimeUtc(source) != stamp)
                    throw new IOException("微信文件仍在写入，稍后重试。");
                size = to.Length;
            }
            string hash;
            await using (var input = File.OpenRead(temporary)) hash = Convert.ToHexString(await SHA256.HashDataAsync(input, ct)).ToLowerInvariant();
            var ext = Path.GetExtension(name);
            if (ext.Length > 20 || ext.Any(c => !char.IsLetterOrDigit(c) && c != '.')) ext = "";
            string stored = hash + ext.ToLowerInvariant(), dest = StoredPath(stored);
            if (!File.Exists(dest)) File.Move(temporary, dest);
            return (stored, hash, size);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public (string Path, string Mime)? ResolvePreviewImage(string id)
    {
        var file = store.File(id, true);
        if (file == null || S(file, "status") == "encrypted") return null;
        string path = StoredPath(S(file, "storedName")), mime = MediaPreview.Mime(path);
        return mime.Length > 0 ? (path, mime) : null;
    }
}
