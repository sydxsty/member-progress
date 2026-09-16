using System.Diagnostics;
using System.Text.Json.Nodes;

namespace StudentProgress.WeChat;

public sealed partial class WeChatService
{
    readonly object progressGate=new();
    JsonObject indexProgress=new(){["phase"]="idle",["running"]=false,["error"]="",["updatedAt"]=DateTimeOffset.Now.ToString("O")};
    long lastProgressPublish;
    int indexRequestVersion;
    string globalIndexFailure="";
    public event Action? ProgressChanged;

    internal static int NormalizeSyncMinutes(JsonNode? value,int fallback=1)
    {
        if(value==null)return fallback;
        if(value is not JsonValue number||!number.TryGetValue<int>(out var minutes)||minutes<1||minutes>1440)
            throw new InvalidDataException("自动同步间隔需为 1 到 1440 分钟的整数。");
        return minutes;
    }
    internal static JsonObject UpgradeConfiguration(JsonObject original)
    {
        var next=UpgradeGlobalArchiveDate(original);next["syncMinutes"]=NormalizeSyncMinutes(next["syncMinutes"]);next["reminderLookbackHours"]=NormalizeReminderLookbackHours(next["reminderLookbackHours"]);return next;
    }
    void RescheduleSync(bool startup=false)
    {
        if(disposed)return;var settings=Configuration;
        var interval=TimeSpan.FromMinutes(NormalizeSyncMinutes(settings["syncMinutes"]));
        bool enabled=!paused&&B(settings,"enabled")&&(settings["selectedConversationIds"] as JsonArray)?.Count>0;
        timer.Change(enabled?(startup?TimeSpan.FromSeconds(8):interval):Timeout.InfiniteTimeSpan,enabled?interval:Timeout.InfiniteTimeSpan);
    }
    async Task ResumeInitialIndexAsync()
    {
        try{await Task.Delay(TimeSpan.FromSeconds(8),lifetime.Token).ConfigureAwait(false);await IndexInBackgroundAsync().ConfigureAwait(false);}
        catch(OperationCanceledException){}
    }

    // Progress is separate from the full state: the UI must not recreate text inputs or chat DOM
    // for every vector. The denominator covers every imported text chunk, including file text.
    public JsonObject ProgressSnapshot()
    {
        JsonObject progress;string failure;lock(progressGate){progress=indexProgress.DeepClone().AsObject();failure=globalIndexFailure;}
        var counts=store.IndexCounts();long total=counts.Total,indexed=counts.Indexed;bool available=embedding.Available;
        string phase=S(progress,"phase");bool running=B(progress,"running");if(failure.Length==0)failure=store.Meta("indexGlobalFailure");
        if(!running&&failure.Length>0){phase="error";progress["error"]=failure;}
        else if(!running&&phase!="paused")phase=total==0?"idle":!available?"unavailable":counts.Pending>0?"waiting":counts.Failed>0?"partial":"ready";
        progress["phase"]=phase;progress["modelAvailable"]=available;progress["indexedChunks"]=indexed;progress["totalChunks"]=total;progress["eligibleChunks"]=total;progress["pendingChunks"]=counts.Pending;progress["failedChunks"]=counts.Failed;progress["skippedVoiceMessages"]=counts.SkippedVoice;
        progress["percent"]=total>0?JsonValue.Create(Math.Clamp(Math.Floor(indexed*1000.0/total)/10.0,0,100)):null;
        progress["canRetry"]=!running&&!paused&&available&&(failure.Length>0||counts.Failed>0||counts.Pending>0);return progress;
    }
    void UpdateIndexProgress(string phase,bool running,string conversationId="",string conversationName="",string fileName="",string error="",bool force=false)
    {
        bool publish;
        lock(progressGate)
        {
            bool changed=S(indexProgress,"phase")!=phase||B(indexProgress,"running")!=running||S(indexProgress,"error")!=error;
            indexProgress["phase"]=phase;indexProgress["running"]=running;indexProgress["conversationId"]=conversationId;indexProgress["conversationName"]=conversationName;indexProgress["fileName"]=fileName;indexProgress["error"]=error;indexProgress["updatedAt"]=DateTimeOffset.Now.ToString("O");
            long now=Stopwatch.GetTimestamp();publish=force||changed||lastProgressPublish==0||Stopwatch.GetElapsedTime(lastProgressPublish,now)>=TimeSpan.FromSeconds(1);
            if(publish)lastProgressPublish=now;
        }
        if(publish)SignalIndexProgress();
    }
    void SignalIndexProgress(){try{ProgressChanged?.Invoke();}catch{}}
    async Task<JsonObject> RetryIndexAsync()
    {
        if(paused)throw new InvalidOperationException("微信资料恢复中，请完成后重试。");
        if(!embedding.Available)throw new InvalidOperationException("本地相似度模型不可用；当前仍可使用关键词搜索。");
        // Return immediately. Background indexing can take a long time; native tools and UI stay responsive.
        _=IndexInBackgroundAsync(manualRetry:true);await Task.CompletedTask;return new(){["progress"]=ProgressSnapshot()};
    }

    async Task IndexInBackgroundAsync(bool manualRetry=false)
    {
        if(disposed||paused)return;
        if(!embedding.Available){UpdateIndexProgress("unavailable",false);return;}
        if(!manualRetry)
        {
            string failure;lock(progressGate)failure=globalIndexFailure;if(failure.Length==0)failure=store.Meta("indexGlobalFailure");
            if(failure.Length>0){UpdateIndexProgress("error",false,error:failure);return;}
        }
        int runVersion=Interlocked.Increment(ref indexRequestVersion);
        if(!await indexGate.WaitAsync(0))return;
        try
        {
            await Task.Run(()=>
            {
                lifetime.Token.ThrowIfCancellationRequested();
                if(manualRetry){store.RetryIndexFailures();lock(progressGate)globalIndexFailure="";}
                UpdateIndexProgress("preparing_index",true);
                embedding.EnsureIndex(store,store.Meta("vectorDirty")=="1"||store.Meta("indexRevision","-1")!=store.Meta("vectorRevision","0"));
                var initial=store.IndexCounts();if(initial.Total==0){UpdateIndexProgress("idle",false,force:true);return;}
                bool modelPrepared=false;
                while(!paused&&!lifetime.IsCancellationRequested)
                {
                    var pending=store.PendingIndexChunks(24);
                    if(pending.Count==0)
                    {
                        // Imports may replace chunks while we encode. Rebuild from the current
                        // durable vectors before declaring readiness, and recheck for new work.
                        if(store.Meta("vectorDirty")=="1")
                        {
                            UpdateIndexProgress("preparing_index",true);embedding.EnsureIndex(store,true);
                            var remaining=store.IndexCounts();if(store.Meta("vectorDirty")=="1"||remaining.Pending>0)continue;
                        }
                        UpdateIndexProgress(store.IndexCounts().Failed>0?"partial":"ready",false,force:true);return;
                    }
                    if(!modelPrepared){UpdateIndexProgress("loading_model",true);embedding.PrepareModel(lifetime.Token);modelPrepared=true;}
                    foreach(var item in pending)
                    {
                        if(paused)break;lifetime.Token.ThrowIfCancellationRequested();
                        UpdateIndexProgress("indexing",true,item.ConversationId,item.ConversationName,item.FileName);
                        float[] vector;
                        try{vector=embedding.Encode(item.Text,lifetime.Token);}
                        catch(Exception e)when(e is WeChatIndexItemException or ArgumentException or FormatException)
                        {
                            store.FailIndexChunk(item.Id,e.Message);UpdateIndexProgress("indexing",true,item.ConversationId,item.ConversationName,item.FileName);continue;
                        }
                        // Runtime/model/storage failures are global; do not blacklist every remaining item.
                        if(store.TrySetVector(item.Id,vector))embedding.Add(item.Id,vector);
                        UpdateIndexProgress("indexing",true,item.ConversationId,item.ConversationName,item.FileName);
                    }
                    UpdateIndexProgress("saving",true);
                    var revision=store.Meta("vectorRevision","0");embedding.SaveIndex();store.MarkIndexSaved(revision,clearDirty:false);
                }
                UpdateIndexProgress("paused",false,force:true);
            },lifetime.Token).ConfigureAwait(false);
        }
        catch(OperationCanceledException){if(!disposed)UpdateIndexProgress("paused",false,force:true);}
        catch(Exception e)
        {
            if(!disposed)
            {
                string error=e.Message[..Math.Min(e.Message.Length,800)];lock(progressGate)globalIndexFailure=error;
                try{store.SetMeta("indexGlobalFailure",error);}catch{}UpdateIndexProgress("error",false,error:error,force:true);
            }
        }
        finally
        {
            indexGate.Release();
            if(!disposed&&!paused&&Volatile.Read(ref indexRequestVersion)!=runVersion)_=IndexInBackgroundAsync();
        }
    }
}
