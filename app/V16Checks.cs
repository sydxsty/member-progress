using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using StudentProgress.WeChat;

namespace StudentProgress;

// Synthetic fixtures only: no live WeChat, remote model or production data access.
internal static class V16Checks
{
    internal static int Run(string directory, string? part = null)
    {
        if (part is not (null or "index" or "suggestions") ||
            Directory.Exists(directory) && Directory.EnumerateFileSystemEntries(directory).Any()) return 1;
        Directory.CreateDirectory(directory);
        var result = new JsonObject { ["ok"] = true, ["modelCalls"] = 0, ["liveWechatReads"] = 0 };
        async Task Check(string name, Func<Task<JsonObject>> run)
        {
            try { var section = await run(); result[name] = section; if (section["ok"]?.GetValue<bool>() == false) result["ok"] = false; }
            catch (Exception error) { result["ok"] = false; result[name] = new JsonObject { ["ok"] = false, ["error"] = error.ToString() }; }
            File.WriteAllText(Path.Combine(directory, "v16-result.json"), result.ToJsonString(new JsonSerializerOptions(JsonSerializerOptions.Default) { WriteIndented = true }), new UTF8Encoding(false));
        }
        async Task All()
        {
            if (part is null or "index") await Check("index", () => WeChatIndexRevisionChecks.RunAsync(Path.Combine(directory, "index"), AppContext.BaseDirectory));
            if (part is null or "suggestions") await Check("suggestions", () => WeChatSuggestionRevisionChecks.RunAsync(Path.Combine(directory, "suggestions"), AppContext.BaseDirectory));
        }
        All().GetAwaiter().GetResult();
        return result["ok"]!.GetValue<bool>() ? 0 : 1;
    }
}
