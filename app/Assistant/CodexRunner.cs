using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace StudentProgress.Assistant;

internal sealed class CodexRunner
{
    internal const string KeyEnvironmentName = "STUDENT_PROGRESS_CODEX_KEY";
    internal static ProcessStartInfo CreateStartInfo(string executable, string workRoot, JsonObject connection)
    {
        string model = AssistantJson.Text(connection["model"]);
        string key = AssistantJson.Text(connection["key"]);
        string baseUrl = AssistantJson.Text(connection["baseUrl"]).TrimEnd('/');
        string apiMode = AssistantJson.Text(connection["apiMode"]);
        if (apiMode is not ("" or "responses"))
            throw new InvalidOperationException("GPT 聊天需要支持 Responses 的接口。请在 GPT 设置中选择 Responses 接口；现有归档记录不受影响。");
        if (string.IsNullOrWhiteSpace(model) || string.IsNullOrWhiteSpace(key))
            throw new InvalidOperationException("请先在设置中配置可用的 GPT 接口、模型和密钥。");
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri) || (uri.Scheme != "https" && uri.Scheme != "http") || !string.IsNullOrEmpty(uri.UserInfo))
            throw new InvalidOperationException("GPT 接口地址无效，请检查设置。");
        var start = new ProcessStartInfo
        {
            FileName = executable,
            WorkingDirectory = workRoot,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        foreach (string arg in new[] { "-a", "never", "exec", "--json", "--ephemeral", "--skip-git-repo-check", "--ignore-user-config", "--ignore-rules", "--sandbox", "read-only", "--color", "never" })
            start.ArgumentList.Add(arg);
        // These feature names were verified against the bundled 0.154.0 executable.
        foreach (string feature in new[] { "shell_tool", "unified_exec", "multi_agent", "multi_agent_v2", "apps", "plugins", "hooks", "browser_use", "browser_use_external", "browser_use_full_cdp_access", "computer_use", "image_generation", "memories", "skill_search", "skill_mcp_dependency_install", "code_mode", "code_mode_host", "view_image", "sleep_tool", "workspace_dependencies", "in_app_local_automation", "goals" })
        {
            start.ArgumentList.Add("--disable");
            start.ArgumentList.Add(feature);
        }
        start.ArgumentList.Add("--enable"); start.ArgumentList.Add("skip_host_skill_discovery");
        void Config(string value) { start.ArgumentList.Add("-c"); start.ArgumentList.Add(value); }
        static string Quote(string value) => JsonSerializer.Serialize(value);
        Config("model_provider=\"student_progress\"");
        Config("model=" + Quote(model));
        Config("model_providers.student_progress={name=\"Student Progress\",base_url=" + Quote(baseUrl) + ",wire_api=\"responses\",env_key=\"" + KeyEnvironmentName + "\",requires_openai_auth=false,request_max_retries=1,stream_max_retries=1,stream_idle_timeout_ms=120000}");
        Config("web_search=\"disabled\"");
        Config("project_doc_max_bytes=0");
        Config("shell_environment_policy.inherit=\"none\"");
        if (model.StartsWith("gpt-5", StringComparison.OrdinalIgnoreCase) || model.StartsWith("gpt-6", StringComparison.OrdinalIgnoreCase))
            Config("model_reasoning_effort=\"low\"");
        Config("model_verbosity=\"low\"");
        start.ArgumentList.Add("-");
        start.Environment.Remove("OPENAI_API_KEY");
        start.Environment.Remove("CODEX_API_KEY");
        start.Environment.Remove("OPENAI_BASE_URL");
        start.Environment.Remove("CODEX_THREAD_ID");
        start.Environment[KeyEnvironmentName] = key;
        return start;
    }

    internal async Task<string> RunAsync(string executable, string workRoot, JsonObject connection, string prompt, CancellationToken cancellation)
    {
        if (!File.Exists(executable)) throw new InvalidOperationException("内置 GPT 组件缺失，请在设置中检查组件或重新解压应用。");
        Directory.CreateDirectory(workRoot);
        var start = CreateStartInfo(executable, workRoot, connection);
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(4));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellation, timeout.Token);
        using var process = new Process { StartInfo = start };
        bool started = false;
        try
        {
            started = process.Start();
            if (!started) throw new InvalidOperationException("GPT 组件暂时无法启动。");
            using var stopRegistration = linked.Token.Register(() => Stop(process));
            var parser = new CodexEvents();
            var stdout = ReadEventsAsync(process.StandardOutput, parser, () => Stop(process), linked.Token);
            var stderr = ReadDiagnosticsAsync(process.StandardError, linked.Token);
            await process.StandardInput.WriteAsync(prompt.AsMemory(), linked.Token).ConfigureAwait(false);
            process.StandardInput.Close();
            await Task.WhenAll(stdout, stderr, process.WaitForExitAsync(linked.Token)).ConfigureAwait(false);
            if (parser.UnexpectedTool) throw new InvalidOperationException("本次回答尝试使用资料范围外的工具，已停止。请只询问当前成员、任务和归档。");
            if (process.ExitCode != 0 || parser.Failed)
                throw new InvalidOperationException(FriendlyError(parser.Error + "\n" + stderr.Result, process.ExitCode));
            if (!parser.Completed || string.IsNullOrWhiteSpace(parser.Answer))
                throw new InvalidOperationException("GPT 没有返回完整回答，问题已保留，请重试。");
            string key = AssistantJson.Text(connection["key"]);
            return string.IsNullOrEmpty(key) ? parser.Answer : parser.Answer.Replace(key, "[凭据已隐藏]", StringComparison.Ordinal);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        { throw new OperationCanceledException("回答已取消，问题已保留。", cancellation); }
        catch (OperationCanceledException)
        { throw new TimeoutException("回答超时，问题已保留。请减少引用资料后重试。"); }
        catch (System.ComponentModel.Win32Exception)
        { throw new InvalidOperationException("GPT 组件无法启动，请检查组件更新或重新解压应用。"); }
        finally { if (started) Stop(process); }
    }

    private static async Task ReadEventsAsync(StreamReader reader, CodexEvents parser, Action stop, CancellationToken cancellation)
    {
        long characters = 0;
        while (await reader.ReadLineAsync(cancellation).ConfigureAwait(false) is { } line)
        {
            characters += line.Length;
            if (characters > 8_000_000 || line.Length > 1_000_000)
                { stop(); throw new InvalidOperationException("GPT 返回的内容过长，本次回答已停止。"); }
            parser.Accept(line);
            if (parser.UnexpectedTool) { stop(); throw new InvalidOperationException("GPT 请求了资料范围外的工具，本次回答已停止。"); }
        }
    }
    private static async Task<string> ReadDiagnosticsAsync(StreamReader reader, CancellationToken cancellation)
    {
        var saved = new StringBuilder();
        char[] buffer = new char[2048]; int n;
        while ((n = await reader.ReadAsync(buffer.AsMemory(), cancellation).ConfigureAwait(false)) > 0)
            if (saved.Length < 16_000) saved.Append(buffer, 0, Math.Min(n, 16_000 - saved.Length));
        return saved.ToString();
    }
    private static void Stop(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException) { }
    }
    private static string FriendlyError(string diagnostic, int exit)
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
