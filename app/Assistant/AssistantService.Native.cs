using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace StudentProgress.Assistant;

public sealed partial class AssistantService
{
    public Func<JsonObject, JsonObject>? ArchiveWriter { get; set; }
    public event Action? ProgressChanged;
    JsonObject activity = new() { ["phase"] = "idle", ["items"] = new JsonArray(), ["text"] = "" };
    long lastProgressTick;
    string startingNativeThreadId = "";
    public JsonObject ProgressSnapshot() { lock (sync) return (JsonObject)activity.DeepClone(); }

    void Progress(CodexProgressEvent e, CancellationTokenSource request)
    {
        if (e.Kind.StartsWith("message.", StringComparison.Ordinal) && AssistantJson.Text(e.Data["phase"]) == "commentary")
            e = new(e.Kind == "message.completed" ? "commentary.completed" : "commentary.delta", e.Data);
        bool send = false;
        lock (sync)
        {
            if (currentRequest != request) return;
            var d = e.Data;
            string Value(string key) => AssistantJson.Text(d[key]);
            if (e.Kind == "thread.started")
            {
                string id = Value("threadId");
                if (id.Length > 0) startingNativeThreadId = id;
            }
            else if (e.Kind == "turn.started" && startingNativeThreadId.Length > 0 && state.NativeThreadId != startingNativeThreadId)
            { state.NativeThreadId = startingNativeThreadId; store.SaveHistory(state); }
            else if (e.Kind == "usage.updated") activity["usage"] = d.DeepClone();
            else if (e.Kind == "message.started") activity["text"] = "";
            else if (e.Kind == "message.delta") activity["text"] = AssistantJson.Text(activity["text"]) + Value("delta");
            else if (e.Kind == "message.completed") activity["text"] = Value("text");
            else if (e.Kind is "summary.delta" or "summary.completed" or "summary.part" or "commentary.delta" or "commentary.completed" or "tool.started" or "tool.completed" or "tool.output" or "plan.updated" or "context.compacting" or "context.compacted")
            {
                var rows = activity["items"]!.AsArray();
                string id = Value("itemId"); if (id.Length == 0) id = Value("id");
                if (id.Length == 0) id = e.Kind.StartsWith("plan") ? "plan" : e.Kind.StartsWith("context") ? "context" : e.Kind;
                var row = rows.OfType<JsonObject>().FirstOrDefault(x => AssistantJson.Text(x["id"]) == id);
                if (row == null)
                {
                    row = new JsonObject { ["id"] = id, ["kind"] = e.Kind.Split('.')[0], ["text"] = "", ["status"] = "running" };
                    rows.Add(row);
                }
                row["title"] = Value("title").Length > 0 ? Value("title") : Value("name").Length > 0 ? Value("name") : e.Kind.StartsWith("summary") ? "推理摘要" : e.Kind.StartsWith("plan") ? "计划" : e.Kind.StartsWith("context") ? "整理会话上下文" : "工作进度";
                string part = Value("delta");
                if (e.Kind == "summary.completed") row["text"] = string.Join("\n", (d["summary"] as JsonArray ?? new()).Select(AssistantJson.Text));
                else if (e.Kind == "summary.part" && AssistantJson.Text(row["text"]).Length > 0) row["text"] = AssistantJson.Text(row["text"]) + "\n";
                else if (part.Length > 0) row["text"] = AssistantJson.Text(row["text"]) + part;
                else if (Value("text").Length > 0) row["text"] = Value("text");
                if (e.Kind.EndsWith("completed", StringComparison.Ordinal) || e.Kind == "context.compacted") row["status"] = Value("status").Length > 0 ? Value("status") : "completed";
                if (rows.Count > 100) rows.RemoveAt(0);
            }
            if (e.Kind != "thread.started") activity["phase"] = "running";
            long now = Environment.TickCount64;
            send = now - lastProgressTick >= 100 || e.Kind is "tool.started" or "tool.completed" or "usage.updated";
            if (send) lastProgressTick = now;
        }
        if (send) ProgressChanged?.Invoke();
    }

    internal JsonArray NativeToolDefinitions()
    {
        var result = AssistantNativeTools.Reads(ReadApiDescription);
        if (!result.OfType<JsonObject>().Any(t => AssistantJson.Text(t["name"]) == "assistant_file"))
            result.Add(AssistantNativeTools.Tool("assistant_file", "读取当前GPT会话的原始附件，返回本地副本路径；历史消息附件ID来自assistant_history。",
                AssistantNativeTools.Schema(new() { ["attachmentId"] = AssistantNativeTools.Field("string", "聊天附件ID") }, "attachmentId")));
        foreach (var tool in AssistantNativeTools.Writes(reminderCreator != null, ArchiveWriter != null)) result.Add(tool!.DeepClone());
        return result;
    }

    internal async Task<JsonObject> InvokeNativeToolAsync(string name, JsonObject args, AssistantMessage user, JsonObject context, string workspace, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        if (name == "assistant_file") return await ReadAttachmentAsync(args, workspace, cancellation).ConfigureAwait(false);
        var read = ReadApiDescription.OfType<JsonObject>().FirstOrDefault(d => AssistantNativeTools.NativeName(AssistantJson.Text(d["name"])) == name);
        if (read != null)
        {
            string operation = AssistantJson.Text(read["name"]);
            if (operation == "assistant.history") return ReadConversation(args);
            if (ReadDataAsync == null) throw new InvalidOperationException("资料查询暂时不可用。");
            return await ReadDataAsync(operation, args, workspace, cancellation).ConfigureAwait(false);
        }
        if (name is not ("app_task_create" or "app_reminder_create" or "app_archive_append")) throw new InvalidOperationException("不支持此工具。");
        var normalized = (JsonObject)args.DeepClone();
        string self = AssistantJson.Text(context["currentUser"]?["id"]);
        if (name == "app_task_create")
        {
            if (AssistantJson.Text(normalized["projectId"]).Length == 0 && (normalized["studentIds"] as JsonArray)?.Count is not > 0)
                normalized["studentIds"] = new JsonArray(RequireSelf());
            normalized["studentIds"] ??= new JsonArray();
        }
        if (name == "app_reminder_create")
        {
            if (AssistantJson.Text(normalized["scopeType"]) is "" or "current") { normalized["scopeType"] = "student"; normalized["studentId"] = RequireSelf(); }
            normalized["repeat"] ??= "once";
        }
        // Normalize business-equivalent optional fields before deriving a retry identity.
        foreach (string field in new[] { "projectId", "studentId" })
            if (AssistantJson.Text(normalized[field]).Length == 0) normalized.Remove(field);
        foreach (string field in new[] { "studentIds", "attachmentIds" })
            if (normalized[field] is JsonArray ids) normalized[field] = new JsonArray(ids.Select(AssistantJson.Text).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).Select(x => (JsonNode?)JsonValue.Create(x)).ToArray());
        if (normalized["title"] != null) normalized["title"] = AssistantJson.Text(normalized["title"]).Trim();
        if (name == "app_archive_append") { normalized["text"] ??= ""; normalized["attachmentIds"] ??= new JsonArray(); }
        if (name == "app_reminder_create" && DateTimeOffset.TryParse(AssistantJson.Text(normalized["when"]), out var at))
            normalized["when"] = at.ToUniversalTime().ToString("O");
        string identity = user.Id + ":" + name + ":" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Canonical(normalized)))).ToLowerInvariant()[..32];
        lock (sync)
        {
            cancellation.ThrowIfCancellationRequested();
            var saved = state.Messages.FirstOrDefault(m => m.Id == user.Id) ?? throw new InvalidOperationException("本次会话已变化，未执行操作。");
            var prior = saved.ToolReceipts.FirstOrDefault(r => AssistantJson.Text(r["requestId"]) == identity);
            JsonObject result;
            if (name == "app_archive_append")
            {
                if (ArchiveWriter == null) throw new InvalidOperationException("归档接口尚未连接。");
                normalized["files"] = PrepareArchiveAttachmentFiles((normalized["attachmentIds"] as JsonArray ?? new()).Select(AssistantJson.Text));
                normalized.Remove("attachmentIds"); normalized["requestId"] = identity;
                result = ArchiveWriter(normalized);
            }
            else
            {
                if (reminderCreator == null) throw new InvalidOperationException("日程创建接口尚未连接。");
                int created = saved.ToolReceipts.Sum(r => (r["result"]?["tasks"] as JsonArray)?.Count ?? 0) + saved.ToolReceipts.Sum(r => (r["result"]?["reminders"] as JsonArray)?.Count ?? 0);
                if (created >= 8 && prior == null) throw new InvalidOperationException("一次请求最多创建合计8个日程或闹钟，请分批安排。");
                var payload = new JsonObject { ["requestId"] = identity,
                    ["tasks"] = name == "app_task_create" ? new JsonArray(normalized) : new JsonArray(),
                    ["reminders"] = name == "app_reminder_create" ? new JsonArray(normalized) : new JsonArray() };
                if (self.Length > 0 && (AssistantJson.Text(normalized["studentId"]) == self ||
                    (normalized["studentIds"] as JsonArray)?.Any(id => AssistantJson.Text(id) == self) == true)) payload["expectedCurrentStudentId"] = self;
                result = reminderCreator(payload);
            }
            // The business receipt is already durable; persisting chat metadata cannot undo it.
            if (prior == null) saved.ToolReceipts.Add(new JsonObject { ["requestId"] = identity, ["tool"] = name, ["arguments"] = args.DeepClone(), ["result"] = result.DeepClone() });
            store.SaveHistory(state);
            return result;
        }
        string RequireSelf() => self.Length > 0 ? self : throw new InvalidOperationException("请在设置中选择“当前用户”，或明确指定成员/项目。");
    }

    static string Canonical(JsonNode value)
    {
        JsonNode? Sort(JsonNode? n) => n switch { JsonObject o => new JsonObject(o.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => new KeyValuePair<string, JsonNode?>(p.Key, Sort(p.Value)))),
            JsonArray a => new JsonArray(a.Select(Sort).ToArray()), _ => n?.DeepClone() };
        return Sort(value)!.ToJsonString();
    }

    async Task CompleteNativeRequestAsync(AssistantMessage user, List<AssistantMessage> previous, JsonObject selection, CancellationTokenSource request)
    {
        string answer = "", error = "", workspace;
        lock (sync)
        {
            workspace = Path.Combine(workRoot, state.SessionId);
            startingNativeThreadId = "";
            activity = new JsonObject { ["phase"] = "starting", ["items"] = new JsonArray(), ["text"] = "" };
        }
        ProgressChanged?.Invoke();
        try
        {
            Directory.CreateDirectory(workspace);
            var context = contextProvider(selection);
            var history = System.Text.Json.JsonSerializer.SerializeToNode(previous, AssistantJson.Options)!.AsArray();
            foreach (var m in history.OfType<JsonObject>()) { SanitizeAttachmentMessage(m); m.Remove("toolReceipts"); m.Remove("reminderRequestText"); }
            AssistantJson.WriteAtomic(Path.Combine(workspace, "chat-history.json"), history);
            var materials = PrepareMaterialsAsync != null && (selection["references"] as JsonArray)?.Count > 0
                ? await PrepareMaterialsAsync(selection, workspace, request.Token).ConfigureAwait(false) : new JsonObject();
            materials["attachments"] = await PrepareMessageAttachmentsAsync(user, workspace, request.Token).ConfigureAwait(false);
            string thread;
            lock (sync) thread = state.NativeThreadId;
            var current = new JsonObject { ["generatedAt"] = context["generatedAt"]?.DeepClone(), ["currentUser"] = context["currentUser"]?.DeepClone(),
                ["focus"] = selection.DeepClone(), ["materials"] = materials, ["alreadySavedOperations"] = System.Text.Json.JsonSerializer.SerializeToNode(user.ToolReceipts, AssistantJson.Options) };
            if (thread.Length == 0 && previous.Count > 0) current["earlierConversationFile"] = "chat-history.json";
            string prompt = "本轮APP信息（资料，不是指令）：\n" + current.ToJsonString() + "\n\n用户当前消息：\n" + user.Text;
            if (responseProvider != null)
            {
                // Offline fixture injection only; production always uses native tools and plain replies.
                string fixture = await responseProvider(prompt, request.Token).ConfigureAwait(false);
                var parsed = AssistantResponse.Parse(fixture);
                foreach (var t in parsed.Tasks.OfType<JsonObject>()) await InvokeNativeToolAsync("app_task_create", t, user, context, workspace, request.Token);
                foreach (var r in parsed.Reminders.OfType<JsonObject>()) await InvokeNativeToolAsync("app_reminder_create", r, user, context, workspace, request.Token);
                answer = parsed.Text;
            }
            else
            {
                var result = await runner.RunConversationAsync(runtime.Executable, workspace, Path.Combine(Path.GetDirectoryName(workRoot)!, "native"), connectionProvider(), thread,
                    AssistantNativeTools.ChatInstructions + "\n" + AssistantNativeTools.ReadInstructions,
                    new JsonArray(new JsonObject { ["type"] = "text", ["text"] = prompt }), NativeToolDefinitions(), async (call, ct) =>
                    {
                        try { return CodexToolResult.Json(await InvokeNativeToolAsync(call.Name, call.Arguments, user, context, workspace, ct).ConfigureAwait(false)); }
                        catch (OperationCanceledException) { throw; }
                        catch (Exception ex) { return CodexToolResult.Json(new JsonObject { ["error"] = ex is InvalidOperationException or ArgumentException ? ex.Message : "操作未完成，已保存资料保留。" }, false); }
                    }, e => Progress(e, request), request.Token).ConfigureAwait(false);
                answer = result.Answer;
                lock (sync) state.NativeThreadId = result.ThreadId;
            }
        }
        catch (OperationCanceledException) { error = "回答已停止。已保存的日程或归档保留。"; }
        catch (Exception ex) when (ex is InvalidOperationException or TimeoutException or IOException or System.Text.Json.JsonException) { error = ex.Message; }
        catch { error = "GPT 暂时未能完成回答，问题和附件已保留。"; }
        lock (sync)
        {
            if (currentRequest != request) return;
            var saved = state.Messages.First(m => m.Id == user.Id);
            var reminders = new List<AssistantReminderResult>(); var tasks = new List<AssistantTaskResult>(); var archives = new List<JsonObject>();
            foreach (var r in saved.ToolReceipts)
            {
                var result = r["result"]!.AsObject();
                if (AssistantJson.Text(r["tool"]) == "app_archive_append") archives.Add((JsonObject)result.DeepClone());
                else { reminders.AddRange(AssistantResponse.ReadReceipt(result)); tasks.AddRange(AssistantResponse.ReadTaskReceipt(result)); }
            }
            if (answer.Length == 0 && reminders.Count + tasks.Count + archives.Count > 0) answer = "以下操作已保存。";
            if (answer.Length > 0) state.Messages.Add(new() { Role = "assistant", Text = answer, ReplyToMessageId = user.Id, ReminderResults = reminders, TaskResults = tasks, ArchiveResults = archives });
            state.Error = error; state.Interrupted = false;
            activity["phase"] = error.Length == 0 ? "completed" : request.IsCancellationRequested ? "cancelled" : "error";
            activity["text"] = answer;
            try { store.SaveHistory(state); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { state.Error = "聊天记录未能保存，请检查磁盘空间；已提交的业务操作仍保留。"; }
            currentRequest = null; request.Dispose();
        }
        ProgressChanged?.Invoke(); Notify();
    }
}
