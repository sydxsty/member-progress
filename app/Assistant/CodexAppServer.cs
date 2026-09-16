using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace StudentProgress.Assistant;

/// <summary>A single native turn, with thread persistence owned by Codex in the app's private home.</summary>
internal sealed class CodexAppServer
{
    internal static ProcessStartInfo CreateStartInfo(string executable, string workRoot, string stateRoot, JsonObject connection, bool nativeTools = true)
    {
        string model = AssistantJson.Text(connection["model"]), key = AssistantJson.Text(connection["key"]);
        string baseUrl = AssistantJson.Text(connection["baseUrl"]).TrimEnd('/');
        if (AssistantJson.Text(connection["apiMode"]) is not ("" or "responses"))
            throw new InvalidOperationException("GPT 需要 Responses 接口，请检查连接设置。");
        if (model.Length == 0 || key.Length == 0) throw new InvalidOperationException("请先配置 GPT 接口、模型和密钥。");
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") || uri.UserInfo.Length > 0)
            throw new InvalidOperationException("GPT 接口地址无效，请检查连接设置。");
        var start = new ProcessStartInfo(executable)
        {
            WorkingDirectory = Path.GetFullPath(workRoot), UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false), StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
        };
        start.ArgumentList.Add("app-server"); start.ArgumentList.Add("--stdio");
        void Config(string value) { start.ArgumentList.Add("-c"); start.ArgumentList.Add(value); }
        static string Quote(string value) => JsonSerializer.Serialize(value);
        string privateHome = Path.GetFullPath(stateRoot), database = Path.Combine(privateHome, "sqlite");
        // These overrides belong only to this child. Nothing is written to the user's Codex config.
        start.Environment["CODEX_HOME"] = privateHome;
        start.Environment["CODEX_SQLITE_HOME"] = database;
        foreach (string name in new[] { "OPENAI_API_KEY", "CODEX_API_KEY", "OPENAI_BASE_URL", "CODEX_THREAD_ID", "CODEX_SESSION_ID", "CODEX_INTERNAL_ORIGINATOR_OVERRIDE", "CODEX_PROFILE" })
            start.Environment.Remove(name);
        start.Environment[CodexRunner.KeyEnvironmentName] = key;
        Config("sqlite_home=" + Quote(database));
        Config("log_dir=" + Quote(Path.Combine(privateHome, "log")));
        Config("model_provider=\"student_progress\"");
        Config("model=" + Quote(model));
        Config("model_providers.student_progress={name=\"Student Progress\",base_url=" + Quote(baseUrl) + ",wire_api=\"responses\",env_key=\"" + CodexRunner.KeyEnvironmentName + "\",requires_openai_auth=false,request_max_retries=1,stream_max_retries=1,stream_idle_timeout_ms=120000}");
        Config("approval_policy=\"never\"");
        Config("web_search=\"disabled\""); Config("project_doc_max_bytes=0");
        Config("shell_environment_policy.inherit=\"core\"");
        // The provider credential is for app-server only, never for native shell/command tools.
        Config("shell_environment_policy.exclude=[\"" + CodexRunner.KeyEnvironmentName + "\",\"OPENAI_API_KEY\",\"CODEX_API_KEY\",\"*TOKEN*\",\"*SECRET*\",\"*PASSWORD*\"]");
        Config("shell_environment_policy.ignore_default_excludes=false");
        Config("model_reasoning_summary=\"auto\""); Config("model_verbosity=\"low\"");
        string effort = AssistantJson.Text(connection["reasoningEffort"]);
        if (effort.Length > 0) Config("model_reasoning_effort=" + Quote(effort));
        else if (model.StartsWith("gpt-5", StringComparison.OrdinalIgnoreCase) || model.StartsWith("gpt-6", StringComparison.OrdinalIgnoreCase)) Config("model_reasoning_effort=\"low\"");
        if (nativeTools) Config("windows.sandbox=\"unelevated\"");
        string[] disabled = ["multi_agent", "multi_agent_v2", "apps", "plugins", "hooks", "browser_use", "browser_use_external", "browser_use_full_cdp_access", "computer_use", "image_generation", "memories", "skill_search", "skill_mcp_dependency_install", "sleep_tool", "in_app_local_automation", "goals"];
        foreach (string feature in disabled.Concat(nativeTools ? [] : new[] { "shell_tool", "unified_exec", "code_mode", "code_mode_host", "view_image", "workspace_dependencies" }))
        { start.ArgumentList.Add("--disable"); start.ArgumentList.Add(feature); }
        start.ArgumentList.Add("--enable"); start.ArgumentList.Add("skip_host_skill_discovery");
        return start;
    }

    internal async Task<CodexTurnResult> RunAsync(string executable, string workRoot, string stateRoot, JsonObject connection,
        string? threadId, string instructions, JsonArray input, JsonArray tools,
        Func<CodexToolCall, CancellationToken, Task<CodexToolResult>>? toolHandler,
        Action<CodexProgressEvent>? onProgress, CancellationToken cancellation, bool ephemeral = false, bool nativeTools = true)
    {
        if (!File.Exists(executable)) throw new InvalidOperationException("内置 GPT 组件缺失，请检查组件更新。");
        Directory.CreateDirectory(workRoot); Directory.CreateDirectory(stateRoot);
        var schemas = NormalizeTools(tools);
        string secret = AssistantJson.Text(connection["key"]);
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(nativeTools ? 12 : 4));
        using var request = CancellationTokenSource.CreateLinkedTokenSource(cancellation, deadline.Token);
        using var process = new Process { StartInfo = CreateStartInfo(executable, workRoot, stateRoot, connection, nativeTools) };
        try
        {
            if (!process.Start()) throw new InvalidOperationException("GPT 内核暂时无法启动。");
            await using var rpc = new Connection(process, secret, schemas, toolHandler, request.Token, nativeTools, onProgress);
            await rpc.RequestAsync("initialize", new JsonObject
            {
                ["clientInfo"] = new JsonObject { ["name"] = "member_progress", ["title"] = "成员进度", ["version"] = "1.0.15" },
                ["capabilities"] = new JsonObject { ["experimentalApi"] = true,
                    ["optOutNotificationMethods"] = new JsonArray("item/reasoning/textDelta") }
            }, request.Token).ConfigureAwait(false);
            await rpc.NotifyAsync("initialized", null, request.Token).ConfigureAwait(false);
            var threadParams = new JsonObject
            {
                ["model"] = AssistantJson.Text(connection["model"]), ["modelProvider"] = "student_progress",
                ["cwd"] = Path.GetFullPath(workRoot), ["approvalPolicy"] = "never", ["sandbox"] = nativeTools ? "workspace-write" : "read-only",
                ["developerInstructions"] = instructions
            };
            bool resume = !string.IsNullOrWhiteSpace(threadId);
            if (resume) { threadParams["threadId"] = threadId; threadParams["excludeTurns"] = true; }
            else { threadParams["ephemeral"] = ephemeral; threadParams["dynamicTools"] = schemas.DeepClone(); threadParams["serviceName"] = "member_progress"; }
            var opened = await rpc.RequestAsync(resume ? "thread/resume" : "thread/start", threadParams, request.Token).ConfigureAwait(false);
            string actualThreadId = AssistantJson.Text(opened?["thread"]?["id"]);
            if (actualThreadId.Length == 0) throw new InvalidOperationException("GPT 内核没有返回有效会话。");
            rpc.SetThread(actualThreadId);
            var turn = new JsonObject { ["threadId"] = actualThreadId, ["input"] = input.DeepClone(), ["summary"] = "auto" };
            string effort = AssistantJson.Text(connection["reasoningEffort"]);
            if (effort.Length > 0) turn["effort"] = effort;
            try
            {
                var started = await rpc.RequestAsync("turn/start", turn, request.Token).ConfigureAwait(false);
                rpc.SetTurn(AssistantJson.Text(started?["turn"]?["id"]));
                await rpc.Completion.WaitAsync(request.Token).ConfigureAwait(false);
                request.Token.ThrowIfCancellationRequested();
                if (rpc.Events.Failed) throw new InvalidOperationException(CodexRunner.FriendlyError(rpc.Events.Error, -1));
                if (rpc.Events.Status == "interrupted") throw new OperationCanceledException("回答已停止。", cancellation);
                return new(actualThreadId, rpc.Events.TurnId, rpc.Events.Answer, rpc.Events.TokenUsage?.DeepClone().AsObject());
            }
            catch (OperationCanceledException)
            {
                // Stop the native turn before tearing down stdio so its persisted history stays coherent.
                await rpc.InterruptAsync().ConfigureAwait(false);
                if (cancellation.IsCancellationRequested) throw new OperationCanceledException("回答已取消，已保存的操作会保留。", cancellation);
                throw new TimeoutException("GPT 回答超时，已保存的操作会保留，可以继续会话。");
            }
        }
        catch (System.ComponentModel.Win32Exception) { throw new InvalidOperationException("GPT 内核无法启动，请检查组件更新。"); }
        finally { Stop(process); }
    }

    internal static JsonArray NormalizeTools(JsonArray tools)
    {
        var output = new JsonArray(); var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in tools)
        {
            if (entry is not JsonObject tool) throw new InvalidOperationException("GPT 工具定义无效。");
            var item = tool.DeepClone().AsObject(); string name = AssistantJson.Text(item["name"]);
            if (name.Length == 0 || !names.Add(name) || item["inputSchema"] is not JsonObject || AssistantJson.Text(item["description"]).Length == 0)
                throw new InvalidOperationException("GPT 工具定义不完整或重名。");
            item["type"] = "function"; item["deferLoading"] ??= false; output.Add(item);
        }
        return output;
    }

    static void Stop(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException) { }
    }

    sealed class Connection : IAsyncDisposable
    {
        readonly Process process;
        readonly string secret;
        readonly HashSet<string> tools;
        readonly Func<CodexToolCall, CancellationToken, Task<CodexToolResult>>? handler;
        readonly CancellationToken actionCancellation;
        readonly CancellationTokenSource lifetime = new();
        readonly SemaphoreSlim writer = new(1, 1), toolGate = new(1, 1);
        readonly ConcurrentDictionary<string, TaskCompletionSource<JsonNode?>> pending = new();
        readonly ConcurrentDictionary<int, Task> incoming = new();
        readonly TaskCompletionSource completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        readonly StringBuilder diagnostics = new();
        readonly Task reader, errors;
        int sequence, incomingSequence;
        internal CodexEvents Events { get; }
        internal Task Completion => completion.Task;

        internal Connection(Process process, string secret, JsonArray tools, Func<CodexToolCall, CancellationToken, Task<CodexToolResult>>? handler,
            CancellationToken actionCancellation, bool nativeTools, Action<CodexProgressEvent>? progress)
        {
            this.process = process; this.secret = secret; this.handler = handler; this.actionCancellation = actionCancellation;
            this.tools = tools.OfType<JsonObject>().Select(t => AssistantJson.Text(t["name"])).ToHashSet(StringComparer.Ordinal);
            Events = new CodexEvents(nativeTools, e => progress?.Invoke(new(e.Kind, Clean(e.Data))));
            reader = ReadAsync(); errors = ReadErrorsAsync();
        }
        JsonObject Clean(JsonObject data) => secret.Length == 0 ? data : JsonNode.Parse(data.ToJsonString().Replace(secret, "[凭据已隐藏]", StringComparison.Ordinal))!.AsObject();
        string Clean(string value) => secret.Length == 0 ? value : value.Replace(secret, "[凭据已隐藏]", StringComparison.Ordinal);
        internal void SetThread(string id) => Events.SetThread(id);
        internal void SetTurn(string id) => Events.SetTurn(id);
        internal async Task<JsonNode?> RequestAsync(string method, JsonObject parameters, CancellationToken ct)
        {
            string id = Interlocked.Increment(ref sequence).ToString(System.Globalization.CultureInfo.InvariantCulture);
            var result = new TaskCompletionSource<JsonNode?>(TaskCreationOptions.RunContinuationsAsynchronously);
            pending[id] = result;
            try
            {
                await SendAsync(new JsonObject { ["id"] = id, ["method"] = method, ["params"] = parameters }, ct).ConfigureAwait(false);
                return await result.Task.WaitAsync(ct).ConfigureAwait(false);
            }
            finally { pending.TryRemove(id, out _); }
        }
        internal Task NotifyAsync(string method, JsonObject? parameters, CancellationToken ct) => SendAsync(new JsonObject { ["method"] = method, ["params"] = parameters }, ct);
        async Task SendAsync(JsonObject message, CancellationToken ct)
        {
            await writer.WaitAsync(ct).ConfigureAwait(false);
            try { await process.StandardInput.WriteLineAsync(message.ToJsonString().AsMemory(), ct).ConfigureAwait(false); await process.StandardInput.FlushAsync(ct).ConfigureAwait(false); }
            finally { writer.Release(); }
        }
        async Task ReadAsync()
        {
            try
            {
                while (await process.StandardOutput.ReadLineAsync(lifetime.Token).ConfigureAwait(false) is { } line)
                {
                    if (line.Length > 32_000_000) throw new InvalidOperationException("GPT 单条输出过长。");
                    JsonObject? message;
                    try { message = JsonNode.Parse(line) as JsonObject; } catch (JsonException) { continue; }
                    if (message == null) continue;
                    string method = AssistantJson.Text(message["method"]);
                    if (method.Length == 0 && message["id"] != null)
                    {
                        string id = AssistantJson.Text(message["id"]);
                        if (pending.TryGetValue(id, out var result))
                        {
                            if (message["error"] != null) result.TrySetException(new InvalidOperationException(Clean(AssistantJson.Text(message["error"]?["message"]))));
                            else result.TrySetResult(message["result"]?.DeepClone());
                        }
                    }
                    else if (method.Length > 0 && message["id"] != null)
                    {
                        int key = Interlocked.Increment(ref incomingSequence);
                        Task task = HandleRequestAsync(message); incoming[key] = task;
                        _ = task.ContinueWith(_ => incoming.TryRemove(key, out var ignored), CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                    }
                    else if (method.Length > 0)
                    {
                        Events.AcceptNotification(method, Clean(message["params"] as JsonObject ?? new()));
                        if (Events.UnexpectedTool) throw new InvalidOperationException("GPT 请求了当前应用未开放的工具。");
                        if (Events.Completed) completion.TrySetResult();
                    }
                }
                if (!Events.Completed && !lifetime.IsCancellationRequested) throw new InvalidOperationException(CodexRunner.FriendlyError(diagnostics.ToString(), -1));
            }
            catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
            catch (Exception e)
            {
                completion.TrySetException(e);
                foreach (var request in pending.Values) request.TrySetException(e);
            }
        }
        async Task HandleRequestAsync(JsonObject request)
        {
            var response = new JsonObject { ["id"] = request["id"]?.DeepClone() };
            string method = AssistantJson.Text(request["method"]); var args = request["params"] as JsonObject ?? new();
            try
            {
                if (method == "item/tool/call")
                {
                    CodexToolResult result;
                    string name = AssistantJson.Text(args["tool"]);
                    if (handler == null || !tools.Contains(name) || args["arguments"] is not JsonObject arguments)
                        result = CodexToolResult.Text("这个工具或参数未开放，请使用当前列出的工具。", false);
                    else
                    {
                        await toolGate.WaitAsync(actionCancellation).ConfigureAwait(false);
                        try
                        {
                            actionCancellation.ThrowIfCancellationRequested();
                            result = await handler(new(AssistantJson.Text(args["threadId"]), AssistantJson.Text(args["turnId"]), AssistantJson.Text(args["callId"]), name,
                                arguments.DeepClone().AsObject(), AssistantJson.Text(args["namespace"])), actionCancellation).ConfigureAwait(false);
                        }
                        finally { toolGate.Release(); }
                    }
                    response["result"] = new JsonObject { ["success"] = result.Success, ["contentItems"] = result.ContentItems.DeepClone() };
                }
                else if (method is "item/commandExecution/requestApproval" or "item/fileChange/requestApproval")
                    response["result"] = new JsonObject { ["decision"] = "decline" };
                else if (method == "item/permissions/requestApproval")
                    response["result"] = new JsonObject { ["permissions"] = new JsonObject(), ["scope"] = "turn" };
                else if (method == "currentTime/read")
                    response["result"] = new JsonObject { ["currentTimeAt"] = DateTimeOffset.Now.ToUnixTimeSeconds() };
                else response["error"] = new JsonObject { ["code"] = -32601, ["message"] = "This app does not implement this request; ask the user in the conversation if clarification is needed." };
            }
            catch (OperationCanceledException)
            { response["result"] = new JsonObject { ["success"] = false, ["contentItems"] = CodexToolResult.Text("用户已停止本轮；不要继续执行操作。", false).ContentItems }; }
            catch (Exception e)
            { response["result"] = new JsonObject { ["success"] = false, ["contentItems"] = CodexToolResult.Text(Clean(e.Message), false).ContentItems }; }
            try { await SendAsync(response, lifetime.Token).ConfigureAwait(false); }
            catch (Exception e) when (e is IOException or OperationCanceledException or ObjectDisposedException) { }
        }
        async Task ReadErrorsAsync()
        {
            try
            {
                char[] buffer = new char[2048]; int count;
                while ((count = await process.StandardError.ReadAsync(buffer.AsMemory(), lifetime.Token).ConfigureAwait(false)) > 0)
                    if (diagnostics.Length < 16000) diagnostics.Append(Clean(new string(buffer, 0, Math.Min(count, 16000 - diagnostics.Length))));
            }
            catch (Exception e) when (e is IOException or OperationCanceledException or ObjectDisposedException) { }
        }
        internal async Task InterruptAsync()
        {
            if (Events.ThreadId.Length == 0 || Events.TurnId.Length == 0 || Events.Completed) return;
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            try
            {
                await RequestAsync("turn/interrupt", new JsonObject { ["threadId"] = Events.ThreadId, ["turnId"] = Events.TurnId }, timeout.Token).ConfigureAwait(false);
                await completion.Task.WaitAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (Exception e) when (e is IOException or OperationCanceledException or InvalidOperationException) { }
        }
        public async ValueTask DisposeAsync()
        {
            // EOF permits the server to flush the native rollout before the fallback process stop.
            try { process.StandardInput.Close(); } catch (IOException) { }
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            try { await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false); } catch (OperationCanceledException) { Stop(process); }
            lifetime.Cancel();
            try { await Task.WhenAll(reader, errors).ConfigureAwait(false); } catch (OperationCanceledException) { }
            try { await Task.WhenAll(incoming.Values).WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false); } catch (Exception) { }
            lifetime.Dispose();
        }
    }
}
