using System.Diagnostics;
using System.Text.Json.Nodes;

namespace StudentProgress.Assistant;

internal sealed class CodexRunner
{
    internal const string KeyEnvironmentName = "STUDENT_PROGRESS_CODEX_KEY";
    internal int LastArchiveToolCalls { get; private set; }

    internal static ProcessStartInfo CreateStartInfo(string executable, string workRoot, JsonObject connection, bool archiveMode = false)
        => CodexAppServer.CreateStartInfo(executable, workRoot, Path.Combine(workRoot, ".codex-state"), connection, archiveMode);

    internal async Task<CodexTurnResult> RunConversationAsync(string executable, string workRoot, string stateRoot, JsonObject connection,
        string? threadId, string instructions, JsonArray input, JsonArray tools,
        Func<CodexToolCall, CancellationToken, Task<CodexToolResult>> toolHandler,
        Action<CodexProgressEvent>? onProgress, CancellationToken cancellation)
    {
        LastArchiveToolCalls = 0;
        return await new CodexAppServer().RunAsync(executable, workRoot, stateRoot, connection, threadId, instructions, input, tools, toolHandler,
            e => { if(e.Kind == "tool.completed") LastArchiveToolCalls++; onProgress?.Invoke(e); }, cancellation).ConfigureAwait(false);
    }

    // Background jobs share the exact same transport but have an ephemeral native conversation.
    internal Task<string> RunAsync(string executable, string workRoot, JsonObject connection, string prompt, CancellationToken cancellation)
        => RunSingleAsync(executable, workRoot, connection, prompt, false, cancellation);
    internal Task<string> RunArchiveAsync(string executable, string workRoot, JsonObject connection, string prompt, CancellationToken cancellation)
        => RunSingleAsync(executable, workRoot, connection, prompt, true, cancellation);

    async Task<string> RunSingleAsync(string executable, string workRoot, JsonObject connection, string prompt, bool nativeTools, CancellationToken cancellation)
    {
        LastArchiveToolCalls = 0;
        var result = await new CodexAppServer().RunAsync(executable, workRoot, Path.Combine(workRoot, ".codex-state"), connection,
            null, "", new JsonArray(new JsonObject { ["type"] = "text", ["text"] = prompt }), new JsonArray(), null,
            e => { if (e.Kind == "tool.completed") LastArchiveToolCalls++; }, cancellation, ephemeral: true, nativeTools: nativeTools).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(result.Answer)) throw new InvalidOperationException("GPT 没有返回完整回答，原问题和资料已保留。");
        return result.Answer;
    }

    internal static string FriendlyError(string diagnostic, int exit)
    {
        string s = diagnostic.ToLowerInvariant();
        if (s.Contains("401") || s.Contains("unauthorized") || s.Contains("authentication") || s.Contains("api key") || s.Contains("not logged in"))
            return "GPT 连接凭据不可用，请检查设置中的密钥；问题已保留。";
        if (s.Contains("429") || s.Contains("rate limit") || s.Contains("quota"))
            return "GPT 接口当前额度不足或请求过于频繁，请稍后重试。";
        if (s.Contains("404") || s.Contains("model") && (s.Contains("not found") || s.Contains("not supported")))
            return "GPT 模型或 Responses 接口不可用，请检查设置。";
        if (s.Contains("context") && (s.Contains("length") || s.Contains("maximum") || s.Contains("exceed")))
            return "本次资料超过模型可接收的长度，请减少归档引用后重试。";
        if (s.Contains("connect") || s.Contains("network") || s.Contains("timeout") || s.Contains("timed out"))
            return "暂时无法连接 GPT 接口，请检查网络或接口设置后重试。";
        return $"GPT 未能完成本次回答（状态 {exit}），问题已保留，请检查设置后重试。";
    }
}
