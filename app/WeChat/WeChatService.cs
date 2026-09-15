using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace StudentProgress.WeChat;

public sealed partial class WeChatService : IDisposable
{
    readonly object gate=new();
    readonly string root,baseDir;
    readonly Func<JsonObject> businessSnapshot;
    readonly Func<JsonObject,JsonObject> archiveImporter;
    readonly WeChatStore store;
    readonly WeChatEmbedding embedding;
    readonly SemaphoreSlim syncGate=new(1,1),indexGate=new(1,1);
    readonly CancellationTokenSource lifetime=new();
    readonly System.Threading.Timer timer;
    JsonObject config,status=new(){["phase"]="idle",["summary"]="选择好友或群后开始同步"};
    JsonArray accounts=new();
    bool disposed,paused;
    public event Action? StateChanged;
    internal Func<string,JsonObject,CancellationToken,Task<JsonObject?>>? AdditionalActionHandler;
    internal Action<JsonObject>? SnapshotEnricher;
    partial void InitializeSuggestions();
    partial void DisposeSuggestions();
    internal WeChatStore Store=>store;
    public JsonObject Configuration {get{lock(gate)return config.DeepClone().AsObject();}}
    public string DataDirectory=>root;
    public WeChatService(string dataRoot,string baseDir,Func<JsonObject> businessSnapshot,Func<JsonObject,JsonObject> archiveImporter)
    {
        root=Path.Combine(Path.GetFullPath(dataRoot),"wechat");this.baseDir=baseDir;this.businessSnapshot=businessSnapshot;this.archiveImporter=archiveImporter;
        Directory.CreateDirectory(root);Directory.CreateDirectory(Path.Combine(root,"files"));config=ReadConfig();store=new(root);embedding=new(baseDir,root);
        if(File.Exists(Path.Combine(root,"accounts.json")))try{accounts=JsonNode.Parse(File.ReadAllText(Path.Combine(root,"accounts.json")))!.AsArray();}catch{}
        InitializeSuggestions();timer=new(_=>{if(!disposed&&!paused&&B(Configuration,"enabled")&&Selected().Count>0)_=BackgroundSyncAsync();},null,TimeSpan.FromSeconds(8),TimeSpan.FromSeconds(60));
    }
    static bool B(JsonNode? n,string k,bool fallback=false)=>n?[k] is JsonValue v&&v.TryGetValue<bool>(out var b)?b:fallback;
    internal static string S(JsonNode? n,string key,string fallback="")=>WeChatStore.S(n,key,fallback);
    internal static JsonObject Clone(JsonObject o)=>o.DeepClone().AsObject();
    internal static int I(JsonNode? n,string k,int fallback)=>n?[k] is JsonValue v&&v.TryGetValue<int>(out var x)?x:fallback;
    static JsonObject Defaults()=>new(){["enabled"]=true,["checkReminders"]=true,["checkMinutes"]=60,["archiveFrom"]=DateTime.Today.ToString("yyyy-MM-dd"),["selectedConversationIds"]=new JsonArray(),["bindings"]=new JsonArray()};
    internal static string NormalizeArchiveFrom(string value)
    {
        if(value is "" or "all")return "all";
        if(!DateOnly.TryParseExact(value,"yyyy-MM-dd",System.Globalization.CultureInfo.InvariantCulture,System.Globalization.DateTimeStyles.None,out _))throw new InvalidOperationException("请选择文件自动归档的起始日期。");
        return value;
    }
    internal static JsonObject UpgradeGlobalArchiveDate(JsonObject original)
    {
        var next=original.DeepClone().AsObject();
        var bindings=(next["bindings"] as JsonArray??new()).OfType<JsonObject>().ToArray();
        if(next["archiveFrom"] is null)
        {
            var dates=bindings.Where(b=>b.ContainsKey("archiveFrom")).Select(b=>NormalizeArchiveFrom(S(b,"archiveFrom"))).ToArray();
            next["archiveFrom"]=dates.Contains("all")?"all":dates.Length>0?dates.Order(StringComparer.Ordinal).First():DateTime.Today.ToString("yyyy-MM-dd");
        }
        else next["archiveFrom"]=NormalizeArchiveFrom(S(next,"archiveFrom"));
        foreach(var binding in bindings)binding.Remove("archiveFrom");
        return next;
    }
    JsonObject ReadConfig()
    {
        var p=Path.Combine(root,"config.json");if(!File.Exists(p))return Defaults();
        try
        {
            var original=JsonNode.Parse(File.ReadAllText(p))!.AsObject();var next=UpgradeGlobalArchiveDate(original);
            if(!JsonNode.DeepEquals(original,next))
            {
                var previous=Path.Combine(root,"config.pre-global-date.json");if(!File.Exists(previous))File.Copy(p,previous);
                WriteJson(p,next);
            }
            return next;
        }
        catch{throw new InvalidDataException("微信设置无法读取，请保留现有 Data/wechat 文件夹。");}
    }
    internal void SaveConfig(){lock(gate)WriteJson(Path.Combine(root,"config.json"),config);}
    internal static void WriteJson(string path,JsonNode value){Directory.CreateDirectory(Path.GetDirectoryName(path)!);string tmp=path+"."+Guid.NewGuid().ToString("N")+".tmp";try{File.WriteAllText(tmp,value.ToJsonString(),new UTF8Encoding(false));File.Move(tmp,path,true);}finally{if(File.Exists(tmp))File.Delete(tmp);}}
    public void SaveConfiguration(JsonObject incoming)
    {
        lock(gate)
        {
            var next=Clone(config);
            next["enabled"]=B(incoming,"enabled",B(config,"enabled",true));
            next["checkReminders"]=B(incoming,"checkReminders",B(config,"checkReminders",true));
            next["archiveFrom"]=NormalizeArchiveFrom(S(incoming,"archiveFrom",S(config,"archiveFrom",DateTime.Today.ToString("yyyy-MM-dd"))));
            var minutes=I(incoming,"checkMinutes",I(config,"checkMinutes",60));if(minutes==0)next["checkReminders"]=false;next["checkMinutes"]=minutes==0?60:Math.Clamp(minutes,5,1440);
            if(incoming["selectedConversationIds"] is JsonArray ids)next["selectedConversationIds"]=new JsonArray(ids.Select(x=>x?.GetValue<string>()??"").Where(x=>store.Conversation(x)!=null).Distinct().Select(x=>(JsonNode?)JsonValue.Create(x)).ToArray());
            if(incoming["bindings"] is JsonArray bindings)next["bindings"]=new JsonArray(bindings.OfType<JsonObject>().Select(ValidateBinding).Cast<JsonNode?>().ToArray());
            WriteJson(Path.Combine(root,"config.json"),next);config=next;
        }
        Changed();if(!paused&&B(Configuration,"enabled")&&Selected().Count>0)_=BackgroundSyncAsync();
    }
    HashSet<string> Selected()=>new((Configuration["selectedConversationIds"] as JsonArray??new()).Select(x=>x?.GetValue<string>()??""),StringComparer.Ordinal);
    JsonObject ValidateBinding(JsonObject item)
    {
        var conv=S(item,"conversationId");if(store.Conversation(conv)==null)throw new InvalidOperationException("请选择已有微信会话。");
        var b=new JsonObject{["conversationId"]=conv};var business=businessSnapshot();string member=S(item,"memberId"),project=S(item,"projectId");
        if(member.Length>0&&!(business["students"] as JsonArray??new()).Any(x=>S(x,"id")==member))throw new InvalidOperationException("关联成员不存在。");
        if(project.Length>0&&!(business["projects"] as JsonArray??new()).Any(x=>S(x,"id")==project))throw new InvalidOperationException("关联项目不存在。");
        b["memberId"]=member;b["projectId"]=project;return b;
    }
    void Changed(){try{StateChanged?.Invoke();}catch{}}
    internal void NotifyChanged()=>Changed();
    void SetStatus(string phase,string summary,string error=""){lock(gate){status["phase"]=phase;status["summary"]=summary;status["error"]=error;}Changed();}
    public JsonObject Snapshot()
    {
        JsonObject result;lock(gate){var counts=store.Counts();var conversations=store.Conversations();var selected=Selected();foreach(var c in conversations.OfType<JsonObject>())c["selected"]=selected.Contains(S(c,"id"));status["lastSync"]=store.Meta("lastSync");status["embeddingReady"]=embedding.Available;status["indexed"]=counts["indexed"]!.DeepClone();status["total"]=counts["chunks"]!.DeepClone();result=new(){["config"]=config.DeepClone(),["status"]=status.DeepClone(),["accounts"]=new JsonArray(accounts.OfType<JsonObject>().Select(a=>(JsonNode?)new JsonObject{["id"]=S(a,"id"),["name"]=S(a,"name"),["version"]=S(a,"version")}).ToArray()),["conversations"]=conversations,["counts"]=counts};}SnapshotEnricher?.Invoke(result);return result;
    }
    public async Task<JsonObject> HandleAsync(string action,JsonObject payload,CancellationToken cancellation=default)
    {
        ObjectDisposedException.ThrowIf(disposed,this);
        switch(action)
        {
            case "wechat.get":case "wechat.state":return Snapshot();
            case "wechat.connect":await ConnectAsync(cancellation);return Snapshot();
            case "wechat.sync":await SyncAsync(cancellation,refreshImages:true);return Snapshot();
            case "wechat.settings.save":SaveConfiguration(payload["config"] as JsonObject??payload);return Snapshot();
            case "wechat.binding.save":
                var b=ValidateBinding(payload);lock(gate){var rows=(config["bindings"] as JsonArray??new());var old=rows.OfType<JsonObject>().FirstOrDefault(x=>S(x,"conversationId")==S(b,"conversationId"));if(old!=null)rows.Remove(old);if(S(b,"memberId").Length>0||S(b,"projectId").Length>0)rows.Add(b);config["bindings"]=rows;SaveConfig();}await ArchiveFilesAsync(cancellation);Changed();return Snapshot();
            case "wechat.messages":if(S(payload,"around").Length>0)return store.MessagesAround(S(payload,"conversationId"),S(payload,"around"),I(payload,"limit",50));return store.Messages(S(payload,"conversationId"),Long(payload,"before",long.MaxValue),I(payload,"limit",50),S(payload,"beforeId"));
            case "wechat.files":var fileLimit=Math.Clamp(I(payload,"limit",200),1,1000);var offset=Math.Max(0,I(payload,"offset",0));var fileRows=store.Files(S(payload,"conversationId"),fileLimit+1,offset);bool moreFiles=fileRows.Count>fileLimit;if(moreFiles)fileRows.RemoveAt(fileRows.Count-1);return new(){["files"]=fileRows,["hasMore"]=moreFiles,["nextOffset"]=offset+fileRows.Count};
            case "wechat.search":return await SearchAsync(payload,cancellation);
            case "wechat.reference.create":return new(){["reference"]=CreateReference(S(payload,"conversationId"),(payload["messageIds"] as JsonArray)?.Select(x=>x!.GetValue<string>()),S(payload,"fileId"))};
            case "wechat.reference.get":return GetReference(S(payload,"scopeId",S(payload,"id")));
            case "wechat.archive.create":return await ArchiveReferenceAsync(payload,cancellation);
            case "wechat.file.open":var file=store.File(S(payload,"id"),true)??throw new InvalidOperationException("文件不存在。");var path=StoredPath(S(file,"storedName"));if(!File.Exists(path))throw new InvalidOperationException("本机尚未保存此文件。");var blocked=new[]{".exe",".com",".bat",".cmd",".ps1",".vbs",".js",".jse",".wsf",".wsh",".hta",".lnk",".url",".scr",".msi",".msp",".reg",".dll",".sys",".cpl",".msc"};if(blocked.Contains(Path.GetExtension(path),StringComparer.OrdinalIgnoreCase))throw new InvalidOperationException("此类文件不能直接打开，请先导出微信资料后查看。");Process.Start(new ProcessStartInfo(path){UseShellExecute=true});return new(){["ok"]=true};
            case "wechat.export":return await ExportAsync(S(payload,"path"),cancellation);
            case "wechat.import":return await ImportAsync(S(payload,"path"),cancellation);
            default:if(AdditionalActionHandler!=null&&await AdditionalActionHandler(action,payload,cancellation) is JsonObject handled)return handled;throw new InvalidOperationException("不支持的微信操作。");
        }
    }
    static long Long(JsonNode n,string key,long fallback)=>n[key] is JsonValue v&&v.TryGetValue<long>(out var x)?x:fallback;
    string Reader=>Path.Combine(baseDir,"runtime","wechat-reader","member-wechat-reader.exe");
    async Task<JsonObject> ReaderAsync(IEnumerable<string> args,CancellationToken ct)
    {
        if(!File.Exists(Reader))throw new InvalidOperationException("安装包中缺少微信读取组件。");
        var start=new ProcessStartInfo(Reader){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true,StandardOutputEncoding=Encoding.UTF8,StandardErrorEncoding=Encoding.UTF8,WorkingDirectory=Path.GetDirectoryName(Reader)!};foreach(var arg in args)start.ArgumentList.Add(arg);
        using var process=new Process{StartInfo=start};process.Start();var output=process.StandardOutput.ReadToEndAsync(ct);var errors=process.StandardError.ReadToEndAsync(ct);
        try{await process.WaitForExitAsync(ct);}catch{try{process.Kill(true);}catch{}throw;}
        var raw=await output;var stderr=await errors;JsonObject? result=null;foreach(var line in raw.Split('\n',StringSplitOptions.RemoveEmptyEntries).Reverse())try{result=JsonNode.Parse(line)?.AsObject();if(result!=null)break;}catch{}
        if(process.ExitCode!=0||result==null||result["ok"]?.GetValue<bool>()==false)throw new InvalidOperationException(S(result,"error",S(result,"message","暂时无法读取微信，请确认微信已登录且版本受到支持。")));
        return result;
    }
    async Task ReadContactsAsync(JsonObject account,CancellationToken ct)
    {
        var workspace=Path.Combine(root,"work",Guid.NewGuid().ToString("N"));Directory.CreateDirectory(workspace);
        try{await ReaderAsync(["read","--account",S(account,"id"),"--output",workspace,"--cache",Path.Combine(root,"reader-cache"),"--contacts-only"],ct);await ImportContactsAsync(Path.Combine(workspace,"contacts.json"),S(account,"id"),ct);}finally{try{Directory.Delete(workspace,true);}catch{}}
    }
    async Task ImportContactsAsync(string file,string accountId,CancellationToken ct)
    {
        if(!File.Exists(file))return;await using var stream=File.OpenRead(file);var parsed=await JsonNode.ParseAsync(stream,cancellationToken:ct);var rows=parsed as JsonArray??parsed?["contacts"] as JsonArray??new();
        foreach(var item in rows.OfType<JsonObject>()){var external=S(item,"conversationId",S(item,"id"));if(external.Length==0)continue;store.SaveConversation(new JsonObject{["id"]=WeChatStore.Stable(accountId,external),["accountId"]=accountId,["externalId"]=external,["name"]=new[]{S(item,"remark"),S(item,"name"),S(item,"nickname"),external}.First(x=>!string.IsNullOrWhiteSpace(x)),["kind"]=S(item,"type",external.EndsWith("@chatroom")?"group":"friend"),["sourceLastMessageAt"]=S(item,"sourceLastMessageAt")});}
    }
    public async Task ConnectAsync(CancellationToken ct=default)
    {
        using var linked=CancellationTokenSource.CreateLinkedTokenSource(ct,lifetime.Token);ct=linked.Token;await syncGate.WaitAsync(ct);
        try{SetStatus("connecting","正在读取本机微信会话");var detected=await ReaderAsync(["discover"],ct);accounts=(detected["accounts"] as JsonArray??new()).DeepClone().AsArray();WriteJson(Path.Combine(root,"accounts.json"),accounts);if(accounts.Count==0)throw new InvalidOperationException("未找到已登录的本机微信账号。");foreach(var a in accounts.OfType<JsonObject>())await ReadContactsAsync(a,ct);SetStatus("idle",Selected().Count==0?"选择需要同步的好友或群":"已连接本机微信");}
        catch(Exception e)when(e is not OperationCanceledException){SetStatus("error","连接微信未完成",e.Message);throw;}finally{syncGate.Release();}
    }
    async Task BackgroundSyncAsync(){try{await SyncAsync(lifetime.Token);}catch(OperationCanceledException){}catch{}}
    public async Task SyncAsync(CancellationToken ct=default,bool refreshImages=false)
    {
        if(paused)return;using var linked=CancellationTokenSource.CreateLinkedTokenSource(ct,lifetime.Token);ct=linked.Token;if(!await syncGate.WaitAsync(0,ct))return;
        try
        {
            var selected=Selected();if(selected.Count==0){SetStatus("idle","选择需要同步的好友或群");return;}SetStatus("syncing","正在同步微信记录");
            var conversations=store.Conversations().OfType<JsonObject>().Where(x=>selected.Contains(S(x,"id"))).ToArray();long changed=0;
            foreach(var group in conversations.GroupBy(x=>S(x,"accountId")))
            {
                var workspace=Path.Combine(root,"work",Guid.NewGuid().ToString("N"));Directory.CreateDirectory(workspace);
                try
                {
                    var args=new List<string>{"read","--account",group.Key,"--output",workspace,"--cache",Path.Combine(root,"reader-cache")};foreach(var c in group){args.Add("--conversation");args.Add(S(c,"externalId"));}
                    if(refreshImages)args.Add("--refresh-images");var readResult=await ReaderAsync(args,ct);if(readResult["complete"] is JsonValue complete&&complete.TryGetValue<bool>(out var isComplete)&&!isComplete)throw new InvalidOperationException("微信本次读取未完成，将保留上次结果并重试。");await ImportContactsAsync(Path.Combine(workspace,"contacts.json"),group.Key,ct);
                    var records=Path.Combine(workspace,"messages.jsonl");if(!File.Exists(records))throw new InvalidDataException("微信读取组件未生成聊天记录。");
                    using var reader=new StreamReader(records,Encoding.UTF8);while(await reader.ReadLineAsync(ct) is string line){ct.ThrowIfCancellationRequested();if(string.IsNullOrWhiteSpace(line))continue;var raw=JsonNode.Parse(line)!.AsObject();var convId=WeChatStore.Stable(group.Key,S(raw,"conversationId"));if(!selected.Contains(convId))continue;var message=NormalizeMessage(raw,group.Key,convId);if(store.UpsertMessage(message))changed++;await ImportFilesAsync(raw,message,ct);}
                }
                finally{try{Directory.Delete(workspace,true);}catch{}}
            }
            await ArchiveFilesAsync(ct);await BackfillReferencedImagesAsync(ct);store.SetMeta("lastSync",DateTimeOffset.Now.ToString("O"));if(changed>0)store.SetMeta("lastImport",DateTimeOffset.UtcNow.ToString("O"));SetStatus("idle",changed>0?$"已同步 {changed} 条新增或更新记录":"已同步");_ = IndexInBackgroundAsync();
        }
        catch(Exception e)when(e is not OperationCanceledException){SetStatus("error","同步暂未完成，将自动重试",e.Message);throw;}
        finally{syncGate.Release();}
    }
    JsonObject NormalizeMessage(JsonObject raw,string accountId,string conversationId)
    {
        var time=S(raw,"time");if(!DateTimeOffset.TryParse(time,out var when))when=DateTimeOffset.FromUnixTimeSeconds(Long(raw,"timestamp",0));var extId=S(raw,"id");if(extId.Length==0)throw new InvalidDataException("微信读取组件返回了缺少稳定标识的消息。");
        return new(){["id"]=store.CanonicalMessageId(accountId,conversationId,extId,S(raw["source"],"serverId")),["externalId"]=extId,["accountId"]=accountId,["conversationId"]=conversationId,["senderId"]=S(raw,"senderId"),["senderName"]=S(raw,"senderName"),["isSelf"]=B(raw,"isSelf"),["when"]=when.ToString("O"),["kind"]=S(raw,"type","text"),["text"]=S(raw,"text"),["rawContent"]=S(raw,"rawContent"),["source"]=raw["source"]?.DeepClone()};
    }
    internal string StoredPath(string name)
    {
        if(name.Length==0)return "";if(name!=Path.GetFileName(name)||name.Any(c=>c is '/' or '\\'))throw new InvalidDataException("微信资料路径无效。");return Path.Combine(root,"files",name);
    }
    async Task IndexInBackgroundAsync()
    {
        if(paused||!embedding.Available||!await indexGate.WaitAsync(0))return;
        try
        {
            await Task.Run(()=>{embedding.EnsureIndex(store,store.Meta("vectorDirty")=="1");store.SetMeta("vectorDirty","0");int count=0;while(!paused&&!lifetime.IsCancellationRequested){var pending=store.PendingChunks(24);if(pending.Count==0)break;foreach(var (id,text) in pending){lifetime.Token.ThrowIfCancellationRequested();var vector=embedding.Encode(text,lifetime.Token);store.SetVector(id,vector);embedding.Add(id,vector);count++;}var revision=store.Meta("vectorRevision","0");embedding.SaveIndex();store.SetMeta("indexRevision",revision);Changed();}if(count>0)Changed();},lifetime.Token);
        }
        catch(OperationCanceledException){}catch(Exception e){lock(gate)status["indexError"]=e.Message;Changed();}finally{indexGate.Release();}
    }
    async Task<JsonObject> SearchAsync(JsonObject payload,CancellationToken ct)
    {
        var query=S(payload,"query").Trim();if(query.Length==0)return new(){["results"]=new JsonArray()};if(query.Length>2000)query=query[..2000];string conv=S(payload,"conversationId"),kind=S(payload,"kind");DateTimeOffset.TryParse(S(payload,"from"),out var from);DateTimeOffset.TryParse(S(payload,"to"),out var to);long fromMs=from==default?long.MinValue:from.ToUnixTimeMilliseconds(),throughMs=to==default?long.MaxValue:to.AddDays(1).ToUnixTimeMilliseconds();
        var keyword=store.KeywordCandidates(query,120,conv,kind,fromMs,throughMs);List<long> semantic=[];string warning="";
        if(embedding.Available)try{semantic=await Task.Run(()=>{embedding.EnsureIndex(store);var vector=embedding.Encode(query,ct);return conv.Length>0||kind=="file"||from!=default||to!=default?store.FilteredSimilarity(vector,conv,kind,fromMs,throughMs):embedding.Search(vector,320);},ct);}catch(Exception e)when(e is not OperationCanceledException){warning=e.Message;}
        var scores=new Dictionary<long,double>();void Add(List<long> ids){for(int i=0;i<ids.Count;i++)scores[ids[i]]=scores.GetValueOrDefault(ids[i])+1.0/(60+i+1);}Add(keyword);Add(semantic);
        var seen=new HashSet<string>();var results=new JsonArray();
        foreach(var (id,score) in scores.OrderByDescending(x=>x.Value))
        {
            var chunk=store.Chunk(id);if(chunk==null)continue;var message=store.Message(S(chunk,"messageId"));if(message==null||conv.Length>0&&S(message,"conversationId")!=conv)continue;
            if(kind=="file"&&S(chunk,"fileId").Length==0)continue;if(kind.Length>0&&kind is not ("all" or "file" or "message")&&S(message,"kind")!=kind)continue;
            var when=DateTimeOffset.Parse(S(message,"when"));if(from!=default&&when<from||to!=default&&when>=to.AddDays(1))continue;
            var resultKey=S(message,"id")+":"+S(chunk,"fileId");if(!seen.Add(resultKey))continue;message["score"]=score;message["excerpt"]=S(chunk,"text");message["fileId"]=S(chunk,"fileId");message["conversationName"]=S(store.Conversation(S(message,"conversationId")),"name");results.Add(message);if(results.Count>=Math.Clamp(I(payload,"limit",30),1,100))break;
        }
        _=IndexInBackgroundAsync();return new(){["results"]=results,["warning"]=warning,["indexing"]=store.Counts()["chunks"]!.GetValue<long>()>store.Counts()["indexed"]!.GetValue<long>()};
    }
    public async Task PauseForRestoreAsync(){paused=true;await syncGate.WaitAsync();await indexGate.WaitAsync();}
    public void ResumeAfterRestore(){lock(gate){config=ReadConfig();store.Reload();embedding.Reset();}indexGate.Release();syncGate.Release();paused=false;Changed();}
    public void Dispose(){if(disposed)return;disposed=true;paused=true;timer.Dispose();lifetime.Cancel();DisposeSuggestions();_ = Task.Run(async()=>{await syncGate.WaitAsync();await indexGate.WaitAsync();embedding.Dispose();store.Dispose();lifetime.Dispose();});}
}
