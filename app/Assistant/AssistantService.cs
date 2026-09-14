using System.Text.Json.Nodes;

namespace StudentProgress.Assistant;

/// <summary>Independent chat and Codex lifecycle. Core business data is supplied only by delegates.</summary>
public sealed partial class AssistantService : IDisposable
{
    private readonly object sync = new();
    private readonly AssistantStore store;
    private readonly CodexRuntime runtime;
    private readonly CodexRunner runner = new();
    private readonly Func<JsonObject, JsonObject> contextProvider;
    private readonly Func<JsonObject> connectionProvider;
    private readonly string workRoot;
    private readonly Func<JsonObject, JsonObject>? reminderCreator;
    private readonly Func<string, CancellationToken, Task<string>>? responseProvider;
    private AssistantState state;
    private CancellationTokenSource? currentRequest;
    private Task? activeTask;
    private readonly CancellationTokenSource lifetime = new();
    private bool maintenance, disposed, clearing;
    public event Action? StateChanged;

    public AssistantService(string dataRoot, string appRoot, Func<JsonObject, JsonObject> contextProvider, Func<JsonObject> connectionProvider,
        Func<JsonObject, JsonObject>? reminderCreator = null)
        : this(dataRoot, appRoot, contextProvider, connectionProvider, reminderCreator, null) { }

    internal AssistantService(string dataRoot, string appRoot, Func<JsonObject, JsonObject> contextProvider, Func<JsonObject> connectionProvider,
        Func<JsonObject, JsonObject>? reminderCreator, Func<string, CancellationToken, Task<string>>? responseProvider)
    {
        this.reminderCreator = reminderCreator;
        this.responseProvider = responseProvider;
        this.contextProvider = contextProvider ?? throw new ArgumentNullException(nameof(contextProvider));
        this.connectionProvider = connectionProvider ?? throw new ArgumentNullException(nameof(connectionProvider));
        string moduleRoot = Path.Combine(Path.GetFullPath(dataRoot), "assistant");
        Directory.CreateDirectory(moduleRoot);
        workRoot = Path.Combine(moduleRoot, "work");
        store = new AssistantStore(moduleRoot);
        state = store.Load();
        runtime = new CodexRuntime(moduleRoot, Path.GetFullPath(appRoot));
        runtime.StateChanged += Notify;
    }

    public JsonObject Snapshot()
    {
        lock(sync)
        {
            var messages=System.Text.Json.JsonSerializer.SerializeToNode(state.Messages,AssistantJson.Options)!.AsArray();
            foreach(var message in messages.OfType<JsonObject>())
            {
                message.Remove("awaitingReminderDetails");message.Remove("reminderRequestText");
            }
            return new JsonObject
            {
                ["messages"] = messages,
                ["busy"] = currentRequest != null || clearing,
                ["canUndoClear"] = !clearing && state.Messages.Count == 0 && state.ClearedMessages.Count > 0,
                ["error"] = state.Error,
                ["runtime"] = runtime.Snapshot(state.AutoUpdate)
            };
        }
    }
    public async Task<JsonNode?> HandleAsync(string action, JsonObject payload)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        switch(action)
        {
            case "assistant.get": return Snapshot();
            case "assistant.send": return StartRequest(payload);
            case "assistant.clear": return await ClearAsync().ConfigureAwait(false);
            case "assistant.undo-clear": return UndoClear();
            case "assistant.cancel":
            {
                Task? task;
                lock(sync) { currentRequest?.Cancel(); task=activeTask; }
                if (task != null) await task.ConfigureAwait(false);
                return Snapshot();
            }
            case "assistant.settings":
            {
                lock(sync)
                {
                    if (payload["autoUpdate"] is not JsonValue v || !v.TryGetValue<bool>(out var enabled))
                        throw new InvalidOperationException("请选择是否自动更新组件。");
                    store.SaveSettings(enabled); state.AutoUpdate=enabled;
                }
                Notify();
                _ = InitializeAsync();
                return Snapshot();
            }
            case "assistant.check-update": StartMaintenance(install:false, automatic:false); return Snapshot();
            case "assistant.update": StartMaintenance(install:true, automatic:false); return Snapshot();
            default: throw new InvalidOperationException("不支持的 GPT 聊天操作。");
        }
    }
    public Task InitializeAsync()
    {
        lock(sync)
        {
            if (disposed || !state.AutoUpdate || runtime.CheckedToday || currentRequest != null || clearing || maintenance) return Task.CompletedTask;
        }
        StartMaintenance(install:true, automatic:true);
        return Task.CompletedTask;
    }
    public void Reload()
    {
        lock(sync)
        {
            if (currentRequest != null || clearing) throw new InvalidOperationException("请先停止当前回答，再恢复聊天记录。");
            state=store.Load();
        }
        Notify();
    }
    private static AssistantState CopyState(AssistantState original)
    {
        var clone=System.Text.Json.JsonSerializer.Deserialize<AssistantState>(
            System.Text.Json.JsonSerializer.Serialize(original,AssistantJson.Options),AssistantJson.Options)!;
        clone.AutoUpdate=original.AutoUpdate;
        return clone;
    }
    private async Task<JsonObject> ClearAsync(JsonObject? mobileRequest = null)
    {
        Task? task;
        lock(sync)
        {
            if (mobileRequest != null)
            {
                if (ReplayMobile(mobileRequest)) return MobileSnapshot();
                CheckMobileVersion(mobileRequest);
            }
            if(clearing)throw new InvalidOperationException("正在清空本次会话，请稍候。");
            clearing=true;
            currentRequest?.Cancel(); task=activeTask;
        }
        try
        {
            if(task!=null)await task.ConfigureAwait(false);
            lock(sync)
            {
                if(state.Messages.Count>0 || mobileRequest != null)
                {
                    var next=CopyState(state);
                    if (state.Messages.Count > 0)
                    {
                        next.ClearedMessages=next.Messages;
                        next.ClearedError=next.Error;
                    }
                    next.Messages=[]; next.Error="";next.Interrupted=false;
                    AddMobileReceipt(next, mobileRequest);
                    store.SaveHistory(next); state=next;
                }
            }
        }
        finally {lock(sync)clearing=false;Notify();}
        return Snapshot();
    }
    private JsonObject UndoClear(JsonObject? mobileRequest = null)
    {
        lock(sync)
        {
            if (mobileRequest != null)
            {
                if (ReplayMobile(mobileRequest)) return MobileSnapshot();
                CheckMobileVersion(mobileRequest);
            }
            if(currentRequest!=null||clearing||state.Messages.Count!=0||state.ClearedMessages.Count==0)
                throw new InvalidOperationException("当前没有可恢复的会话；发送新问题后无法撤销清空。");
            var next=CopyState(state);
            next.Messages=next.ClearedMessages;next.Error=next.ClearedError;
            next.ClearedMessages=[];next.ClearedError="";
            AddMobileReceipt(next, mobileRequest);
            store.SaveHistory(next);state=next;
        }
        Notify();return Snapshot();
    }
    private JsonObject StartRequest(JsonObject payload, JsonObject? mobileRequest = null)
    {
        lock(sync)
        {
            if (mobileRequest != null)
            {
                if (ReplayMobile(mobileRequest)) return MobileSnapshot();
                CheckMobileVersion(mobileRequest, required: false);
            }
            if(currentRequest!=null||clearing)throw new InvalidOperationException("正在回答或整理会话，可以等待或先停止。");
            if(maintenance)throw new InvalidOperationException("GPT 组件正在检查或更新，请稍后再发送。");
            string retryId=AssistantJson.Text(payload["retryMessageId"]);
            AssistantMessage user;
            var next=CopyState(state);
            List<AssistantMessage> previous;
            if(retryId.Length>0)
            {
                user=next.Messages.LastOrDefault(m=>m.Role=="user"&&m.Id==retryId)
                    ??throw new InvalidOperationException("原问题不存在，请重新发送。");
                if(next.Messages.LastOrDefault()?.Id!=retryId)
                    throw new InvalidOperationException("只能重试最近一条尚未得到回答的问题。");
                if(payload.ContainsKey("text")&&AssistantJson.Text(payload["text"]).Trim()!=user.Text||
                    payload.ContainsKey("includeReminders")&&payload["includeReminders"]?.GetValue<bool>()!=user.IncludeReminders||
                    payload.ContainsKey("includeRecent")&&payload["includeRecent"]?.GetValue<bool>()!=user.IncludeRecent)
                    throw new InvalidOperationException("重试的问题已发生变化，请作为新问题发送。");
                if(payload.ContainsKey("references"))
                {
                    var requested=ReadReferences(payload);
                    if(!requested.Select(r=>r.ScopeType+":"+r.ScopeId).SequenceEqual(user.References.Select(r=>r.ScopeType+":"+r.ScopeId)))
                        throw new InvalidOperationException("重试的引用已发生变化，请作为新问题发送。");
                }
                if(user.ReminderRequestText.Length==0&&AssistantResponse.HasExplicitCreationRequest(user.Text))user.ReminderRequestText=user.Text;
                previous=next.Messages.Take(next.Messages.Count-1).ToList();
            }
            else
            {
                string text=AssistantJson.Text(payload["text"]).Trim();
                if(string.IsNullOrEmpty(text))throw new InvalidOperationException("先写下想问的问题。");
                if(text.Length>20_000)throw new InvalidOperationException("问题过长，请缩短到 20000 字以内；长资料可以引用归档。");
                user=new(){Role="user",Text=text,References=ReadReferences(payload),
                    IncludeReminders=payload["includeReminders"]?.GetValue<bool>()??true,
                    IncludeRecent=payload["includeRecent"]?.GetValue<bool>()??true};
                previous=next.Messages.ToList();
                if(AssistantResponse.HasExplicitCreationRequest(text))user.ReminderRequestText=text;
                else if(previous.LastOrDefault() is {Role:"assistant",AwaitingReminderDetails:true} pending&&pending.ReminderRequestText.Length>0)
                {
                    var names=contextProvider(new JsonObject{["includeReminders"]=false,["includeRecent"]=false,["references"]=new JsonArray()});
                    if(AssistantResponse.IsReminderDetailReply(text,names))user.ReminderRequestText=pending.ReminderRequestText;
                }
                next.Messages.Add(user);
                next.ClearedMessages=[];next.ClearedError="";
            }
            var selection=new JsonObject{["includeReminders"]=user.IncludeReminders,["includeRecent"]=user.IncludeRecent,
                ["references"]=System.Text.Json.JsonSerializer.SerializeToNode(user.References,AssistantJson.Options)};
            next.Error="";next.Interrupted=true;
            AddMobileReceipt(next, mobileRequest);
            store.SaveHistory(next);state=next;
            currentRequest=CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
            var request=currentRequest;
            activeTask=Task.Run(()=>CompleteRequestAsync(user,previous,selection,request));
        }
        Notify();return Snapshot();
    }
    private static List<AssistantReference> ReadReferences(JsonObject payload)
    {
        var references=new List<AssistantReference>();
        foreach(var reference in (payload["references"] as JsonArray??[]).OfType<JsonObject>())
        {
            string type=AssistantJson.Text(reference["scopeType"]),id=AssistantJson.Text(reference["scopeId"]);
            if(type is not ("student" or "project")||string.IsNullOrEmpty(id))throw new InvalidOperationException("归档引用无效，请重新选择。");
            if(references.Any(r=>r.ScopeType==type&&r.ScopeId==id))continue;
            references.Add(new(){ScopeType=type,ScopeId=id,Title=AssistantJson.Text(reference["title"])});
        }
        if(references.Count>40)throw new InvalidOperationException("请一次引用不超过 40 份归档。");
        return references;
    }
    private async Task CompleteRequestAsync(AssistantMessage user,List<AssistantMessage> previous,JsonObject selection,CancellationTokenSource request)
    {
        AssistantResponse? response=null;
        string error="";
        try
        {
            request.Token.ThrowIfCancellationRequested();
            var context=contextProvider(selection);
            string prompt=AssistantPrompt.Build(user.Text,previous,context,reminderCreator!=null,user.ReminderRequestText);
            string output=responseProvider!=null
                ?await responseProvider(prompt,request.Token).ConfigureAwait(false)
                :await runner.RunAsync(runtime.Executable,workRoot,connectionProvider(),prompt,request.Token).ConfigureAwait(false);
            request.Token.ThrowIfCancellationRequested();
            response=AssistantResponse.Parse(output);
        }
        catch(OperationCanceledException){error="回答已取消，问题已保留。";}
        catch(Exception e) when(e is InvalidOperationException or TimeoutException or IOException or System.Text.Json.JsonException){error=e.Message;}
        catch{error="GPT 暂时无法完成回答，问题已保留，请重试。";}
        lock(sync)
        {
            if(currentRequest!=request)return;
            var results=new List<AssistantReminderResult>();
            string answer="";
            try
            {
                // Cancellation/clear and the short atomic host write share this lock. A request
                // cancelled before this boundary cannot commit, and clear waits for persistence.
                if(request.IsCancellationRequested){response=null;error="回答已取消，问题已保留。";}
                if(response!=null)
                {
                    if(response.Reminders.Count>0)
                    {
                        if(reminderCreator==null||user.ReminderRequestText.Length==0)
                            answer="本次未创建提醒。需要创建时，请直接告诉我提醒事项和日期时间。";
                        else
                        {
                            request.Token.ThrowIfCancellationRequested();
                            var receipt=reminderCreator(new JsonObject{["requestId"]=user.Id,["reminders"]=response.Reminders.DeepClone()});
                            results=AssistantResponse.ReadReceipt(receipt);
                            answer=receipt["replayed"]?.GetValue<bool>()==true
                                ?$"这条请求已创建 {results.Count} 条提醒，本次没有重复添加。"
                                :$"已创建 {results.Count} 条提醒。";
                        }
                    }
                    else answer=AssistantResponse.SafeAdvice(response.Text);
                }
            }
            catch(OperationCanceledException){error="回答已取消，问题已保留。";}
            catch(Exception e) when(e is InvalidOperationException or IOException or System.Text.Json.JsonException){error=e.Message;}
            catch{error="本次提醒未能完成，请查看提醒列表后重试。";}
            var next=CopyState(state);
            if(answer.Length>0)next.Messages.Add(new(){Role="assistant",Text=answer,ReplyToMessageId=user.Id,ReminderResults=results,
                AwaitingReminderDetails=response?.NeedsReminderDetails==true&&reminderCreator!=null&&user.ReminderRequestText.Length>0,
                ReminderRequestText=response?.NeedsReminderDetails==true&&reminderCreator!=null?user.ReminderRequestText:""});
            next.Error=error;next.Interrupted=false;
            try{store.SaveHistory(next);state=next;}
            catch(Exception e) when(e is IOException or UnauthorizedAccessException)
            {
                // Keep both old disk history and in-memory messages intact. A retry reuses the
                // saved user id, so a committed host receipt cannot create duplicate reminders.
                state.Error=results.Count>0?"提醒已创建，但回答暂时无法写入本机；重试不会重复创建提醒。":"本次聊天暂时无法写入本机，请检查磁盘空间或资料目录权限。";
                state.Interrupted=false;
            }
            currentRequest=null;request.Dispose();
        }
        Notify();
    }
    private void StartMaintenance(bool install,bool automatic)
    {
        lock(sync)
        {
            if(disposed)return;
            if(maintenance||currentRequest!=null||clearing)
            {
                if(automatic)return;
                throw new InvalidOperationException("请等待当前回答或组件操作结束。");
            }
            maintenance=true;
        }
        _=Task.Run(async()=>
        {
            try
            {
                if(automatic || !install)await runtime.CheckAsync(lifetime.Token).ConfigureAwait(false);
                bool canInstall;
                lock(sync)canInstall=install&&(!automatic||state.AutoUpdate);
                if(canInstall)await runtime.UpdateAsync(lifetime.Token).ConfigureAwait(false);
            }
            catch(OperationCanceledException) { }
            catch(InvalidOperationException) { /* Runtime snapshot retains a concise, actionable status. */ }
            catch { /* Keep the previously working executable selected. */ }
            finally {lock(sync)maintenance=false;Notify();}
        });
    }
    private void Notify()
    {
        if(disposed)return;
        try {StateChanged?.Invoke();} catch(ObjectDisposedException) { }
    }
    public void Dispose()
    {
        lock(sync)
        {
            if(disposed)return;disposed=true;
            lifetime.Cancel();
        }
        runtime.StateChanged-=Notify;
        // The process reads this token and kills only its own child tree.
        // HttpClient is disposed after cancellation; persisted Interrupted detects an immediate app exit.
        runtime.Dispose();
        lifetime.Dispose();
    }
}
