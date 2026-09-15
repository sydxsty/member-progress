using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;

namespace StudentProgress.WeChat;

// A read surface over imported evidence. It does not connect to WeChat, import records,
// create references, bind identities or mutate reminders. Search may maintain its derived index.
public sealed partial class WeChatService
{
    public static JsonArray DescribeAssistantReads()
    {
        static JsonObject P(string type,string description)=>new(){["type"]=type,["description"]=description};
        static JsonObject Api(string name,string description,JsonObject properties,params string[] required)=>new(){["name"]=name,["description"]=description,["parameters"]=new JsonObject{["type"]="object",["properties"]=properties,["required"]=new JsonArray(required.Select(x=>(JsonNode?)JsonValue.Create(x)).ToArray()),["additionalProperties"]=false}};
        static JsonObject Page()=>new(){["limit"]=new JsonObject{["type"]="integer",["minimum"]=1,["maximum"]=100,["description"]="每页数量，默认 40。"},["offset"]=new JsonObject{["type"]="integer",["minimum"]=0,["description"]="从 0 开始；后续页使用 nextOffset。"}};
        var conv=Page();conv["query"]=P("string","按好友/群名称或微信 ID 进行字面包含匹配；不猜测 APP 成员身份。");
        var messages=new JsonObject
        {
            ["conversationId"]=P("string","会话 ID；省略时查看所有已导入消息。"),["query"]=P("string","正文、原始内容或发送人名称的字面包含匹配，可完整翻页；语义问题用 wechat.search。"),
            ["kind"]=P("string","精确消息类型，例如 text/image/file/video/voice。"),["senderId"]=P("string","精确发送人 ID。"),
            ["from"]=P("string","含起点，YYYY-MM-DD 或带时区的 ISO 时间。"),["to"]=P("string","YYYY-MM-DD 含当天；ISO 时间为不含终点。"),
            ["before"]=P("integer","向更早消息翻页的 Unix 毫秒；同时传 beforeId。"),["beforeId"]=P("string","同一时间戳消息的稳定游标。"),["after"]=P("integer","向更新消息翻页的 Unix 毫秒；同时传 afterId。"),["afterId"]=P("string","同一时间戳消息的稳定游标。"),
            ["order"]=new JsonObject{["type"]="string",["enum"]=new JsonArray("newest_first","oldest_first"),["description"]="默认最新在前；使用 after 时最早在前。"},
            ["around"]=P("string","查看此消息及前后原文。可配 conversationId/limit，不能同时配搜索、日期、发送人、类型或翻页参数。"),["limit"]=new JsonObject{["type"]="integer",["minimum"]=1,["maximum"]=100,["description"]="默认 40；around 模式最少 3。"}
        };
        var search=new JsonObject{["query"]=P("string","自然语言语义/关键词查询，最多 2000 字符。"),["conversationId"]=P("string","可选：仅此好友/群。"),["kind"]=new JsonObject{["type"]="string",["enum"]=new JsonArray("all","file"),["description"]="默认 all；file 仅文件索引。"},["from"]=P("string","可选：YYYY-MM-DD，含当天。"),["to"]=P("string","可选：YYYY-MM-DD，含当天。"),["limit"]=new JsonObject{["type"]="integer",["minimum"]=1,["maximum"]=100,["description"]="返回排名前 K 条，默认 30；并非全部匹配。"}};
        var files=Page();files["conversationId"]=P("string","仅此好友/群；省略查询所有导入文件。");files["query"]=P("string","文件名、已提取文字或所属消息的字面包含匹配。");files["kind"]=P("string","精确附件类型，如 image/file/video/voice。");files["from"]=P("string","含起点，YYYY-MM-DD 或 ISO 时间。");files["to"]=P("string","YYYY-MM-DD 含当天；ISO 时间不含终点。");
        return new JsonArray
        {
            Api("wechat.observe","查看微信资料覆盖范围、最后同步时间、已导入数量、向量索引覆盖情况和明确设置的成员/项目关联。仅 APP 已导入资料可查；不读取未同步微信。",new()),
            Api("wechat.conversations","按微信最后更新顺序列出已导入消息的好友/群，支持名称过滤、总数和翻页。零消息会话不在结果中。",conv),
            Api("wechat.messages","按需查看或搜索消息原文及附件 ID，支持全库/单会话、日期、稳定游标翻页和命中前后文。正文不截断。",messages),
            Api("wechat.message","通过消息 ID 取完整正文、原始 rawContent/XML 和附件元数据。用于核对搜索线索；附件字节用 wechat.file。",new(){["id"]=P("string","消息 ID。")},"id"),
            Api("wechat.search","复用 APP 的本地向量 + 关键词混合搜索。返回原始消息、片段和文件 ID；相似度仅为线索，结论须核对原文/附件。",search,"query"),
            Api("wechat.files","列出已导入附件及可用状态，支持名称/文本、会话、日期过滤和翻页。",files),
            Api("wechat.file","按附件 ID 将 APP 保存的实际文件复制到当前隔离工作目录，返回相对路径，供原生工具查看图片或分析文档/多模态。不会去微信加载缺失文件。",new(){["id"]=P("string","附件 ID。"),["includeOriginal"]=P("boolean","默认 false；true 时额外复制微信加密/封装原件，与已解码可读图片分开标明。")},"id")
        };
    }

    public async Task<JsonObject> ReadAssistantDataAsync(string operation,JsonObject args,string workspace,CancellationToken cancellation)
    {
        ObjectDisposedException.ThrowIf(disposed,this);cancellation.ThrowIfCancellationRequested();
        var description=DescribeAssistantReads().OfType<JsonObject>().SingleOrDefault(x=>S(x,"name")==operation)??throw new InvalidOperationException("不支持的微信只读接口。");
        var allowed=description["parameters"]!["properties"]!.AsObject();foreach(var key in args.Select(x=>x.Key))if(!allowed.ContainsKey(key))throw new ArgumentException("微信只读接口参数不支持："+key);
        string Text(string key,int max=2000)
        {
            if(args[key] is null)return "";if(args[key] is not JsonValue value||!value.TryGetValue<string>(out var result))throw new ArgumentException(key+" 应为文本。");if(result.Length>max)throw new ArgumentException(key+$" 不能超过 {max} 字符。");return result.Trim();
        }
        int Number(string key,int fallback,int min,int max)
        {
            if(args[key] is null)return fallback;if(args[key] is not JsonValue value||!value.TryGetValue<int>(out var result)||result<min||result>max)throw new ArgumentException(key+$" 应为 {min} 到 {max} 的整数。");return result;
        }
        long? Cursor(string key){if(args[key] is null)return null;if(args[key] is not JsonValue value||!value.TryGetValue<long>(out var result)||result<DateTimeOffset.MinValue.ToUnixTimeMilliseconds()||result>DateTimeOffset.MaxValue.ToUnixTimeMilliseconds())throw new ArgumentException(key+" 应为有效 Unix 毫秒。");return result;}
        string Required(string key){var value=Text(key);return value.Length>0?value:throw new ArgumentException("缺少 "+key+"。");}
        string conversation=Text("conversationId");if(conversation.Length>0&&store.Conversation(conversation)==null)throw new InvalidOperationException("微信会话不存在。");
        JsonObject result;
        switch(operation)
        {
            case "wechat.observe":
                var cfg=Configuration;var imported=store.AssistantConversations("",0,1);var bindings=new JsonArray((cfg["bindings"] as JsonArray??new()).OfType<JsonObject>().Select(b=>(JsonNode?)new JsonObject{["conversationId"]=S(b,"conversationId"),["memberId"]=S(b,"memberId"),["projectId"]=S(b,"projectId")}).ToArray());
                result=new(){["counts"]=store.Counts(),["importedConversationCount"]=imported["total"]!.DeepClone(),["syncEnabled"]=B(cfg,"enabled"),["selectedConversationCount"]=(cfg["selectedConversationIds"] as JsonArray)?.Count??0,["bindings"]=bindings,["availability"]="Only messages and files already imported into this APP are accessible; missing or unsynced WeChat history is not evidence of absence."};break;
            case "wechat.conversations":result=store.AssistantConversations(Text("query"),Number("offset",0,0,int.MaxValue),Number("limit",40,1,100));break;
            case "wechat.messages":
                var around=Text("around");int limit=Number("limit",40,1,100);
                if(around.Length>0)
                {
                    if(args.Any(p=>p.Value!=null&&p.Key is not ("conversationId" or "around" or "limit")))throw new ArgumentException("around 仅能与 conversationId/limit 同时使用。");
                    if(limit<3)throw new ArgumentException("around 的 limit 最少为 3。");result=store.AssistantMessagesAround(conversation,around,limit);
                }
                else
                {
                    var before=Cursor("before");var after=Cursor("after");if(before.HasValue&&after.HasValue)throw new ArgumentException("before 和 after 不能同时使用。");
                    if(Text("beforeId").Length>0&&!before.HasValue||Text("afterId").Length>0&&!after.HasValue)throw new ArgumentException("消息 ID 游标需要对应时间戳。");
                    var order=Text("order");if(order.Length>0&&order is not ("oldest_first" or "newest_first"))throw new ArgumentException("order 无效。");bool ascending=after.HasValue||!before.HasValue&&order=="oldest_first";
                    if(after.HasValue&&order=="newest_first"||before.HasValue&&order=="oldest_first")throw new ArgumentException("翻页游标与 order 不一致。");
                    var range=AssistantDateRange(Text("from"),Text("to"));result=store.AssistantMessages(conversation,Text("query"),Text("kind"),Text("senderId"),range.From,range.Through,before??after,Text(ascending?"afterId":"beforeId"),ascending,limit);
                }
                SanitizeMessageRows(result,"messages");break;
            case "wechat.message":result=new(){["message"]=AssistantMessage(store.Message(Required("id"))??throw new InvalidOperationException("微信消息不存在。"))};break;
            case "wechat.search":
                var query=Required("query");int count=Number("limit",30,1,100);var kind=Text("kind");if(kind.Length>0&&kind is not ("all" or "file"))throw new ArgumentException("搜索 kind 只支持 all/file。");
                foreach(var key in new[]{"from","to"})if(Text(key).Length>0&&!DateOnly.TryParseExact(Text(key),"yyyy-MM-dd",CultureInfo.InvariantCulture,DateTimeStyles.None,out _))throw new ArgumentException("搜索日期请用 YYYY-MM-DD。");
                _=AssistantDateRange(Text("from"),Text("to"));var search=args.DeepClone().AsObject();search["query"]=query;search["limit"]=count;result=await SearchAsync(search,cancellation);SanitizeMessageRows(result,"results");
                if(S(result,"warning").Length>0)result["warning"]="向量检索暂不可用，本次仅返回关键词线索。";
                result["retrieval"]=new JsonObject{["mode"]=embedding.Available&&S(result,"warning").Length==0?"hybrid":"keyword_only",["resultLimit"]=count,["keywordCandidateLimit"]=120,["semanticCandidateLimit"]=320,["returned"]=result["results"]!.AsArray().Count,["exhaustive"]=false,["note"]="Ranked top-K results, not all matches. Narrow the query or use wechat.messages / wechat.files with literal filters and pagination for exhaustive lookup. Verify original message/attachment content before concluding."};break;
            case "wechat.files":var dates=AssistantDateRange(Text("from"),Text("to"));result=store.AssistantFiles(conversation,Text("query"),Text("kind"),dates.From,dates.Through,Number("offset",0,0,int.MaxValue),Number("limit",40,1,100));result["files"]=new JsonArray(result["files"]!.AsArray().OfType<JsonObject>().Select(f=>(JsonNode?)AssistantFileMetadata(f)).ToArray());break;
            case "wechat.file":
                bool include=false;if(args["includeOriginal"] is JsonNode includeNode&&(includeNode is not JsonValue v||!v.TryGetValue<bool>(out include)))throw new ArgumentException("includeOriginal 应为布尔值。");
                result=await CopyAssistantFileAsync(Required("id"),workspace,include,cancellation);break;
            default:throw new InvalidOperationException("不支持的微信只读接口。");
        }
        result["coverage"]=AssistantCoverage();return result;
    }

    JsonObject AssistantCoverage()
    {
        var counts=store.Counts();string phase;lock(gate)phase=S(status,"phase");return new(){["scope"]="imported_app_wechat_data",["lastSync"]=store.Meta("lastSync"),["phase"]=phase,["embeddingAvailable"]=embedding.Available,["indexedChunks"]=counts["indexed"]!.DeepClone(),["totalChunks"]=counts["chunks"]!.DeepClone(),["unindexedChunks"]=counts["chunks"]!.GetValue<long>()-counts["indexed"]!.GetValue<long>()};
    }
    static (long From,long Through) AssistantDateRange(string from,string to)
    {
        static long Bound(string value,bool end)
        {
            if(value.Length==0)return end?long.MaxValue:long.MinValue;
            if(DateOnly.TryParseExact(value,"yyyy-MM-dd",CultureInfo.InvariantCulture,DateTimeStyles.None,out var date))
            {
                if(end&&date==DateOnly.MaxValue)return long.MaxValue;var local=(end?date.AddDays(1):date).ToDateTime(TimeOnly.MinValue,DateTimeKind.Local);return new DateTimeOffset(local).ToUnixTimeMilliseconds();
            }
            if(!DateTimeOffset.TryParse(value,CultureInfo.InvariantCulture,DateTimeStyles.None,out var parsed))throw new ArgumentException("日期应为 YYYY-MM-DD 或 ISO 时间。");return parsed.ToUnixTimeMilliseconds();
        }
        var start=Bound(from,false);var through=Bound(to,true);if(start>=through)throw new ArgumentException("起始日期应早于结束时间。");return (start,through);
    }
    static JsonObject Pick(JsonObject source,params string[] fields)
    {var result=new JsonObject();foreach(var field in fields)if(source[field]!=null)result[field]=source[field]!.DeepClone();return result;}
    static JsonObject AssistantFileMetadata(JsonObject source)=>Pick(source,"id","messageId","name","size","type","status","mime","decodeStatus","isThumbnail","available","originalName","originalSize","conversationId","senderName","when");
    static JsonObject AssistantMessage(JsonObject source)
    {
        var result=Pick(source,"id","conversationId","senderId","senderName","isSelf","when","kind","text","rawContent","conversationName","score","excerpt","fileId");result["attachments"]=new JsonArray((source["attachments"] as JsonArray??new()).OfType<JsonObject>().Select(f=>(JsonNode?)AssistantFileMetadata(f)).ToArray());return result;
    }
    static void SanitizeMessageRows(JsonObject result,string key)=>result[key]=new JsonArray((result[key] as JsonArray??new()).OfType<JsonObject>().Select(m=>(JsonNode?)AssistantMessage(m)).ToArray());

    async Task<JsonObject> CopyAssistantFileAsync(string id,string workspace,bool includeOriginal,CancellationToken cancellation)
    {
        var file=store.File(id,true)??throw new InvalidOperationException("微信附件不存在。");var result=AssistantFileMetadata(file);var source=StoredPath(S(file,"storedName"));bool available=source.Length>0&&File.Exists(source);result["available"]=available;result["hasEncryptedOriginal"]=S(file,"originalStoredName").Length>0;
        if(!available){result["readStatus"]="not_available_locally";result["note"]="APP 尚未保存此文件；不能据此推断文件内容。";return result;}
        if(string.IsNullOrWhiteSpace(workspace))throw new ArgumentException("缺少隔离工作目录。");var workRoot=Path.GetFullPath(workspace);var target=Path.Combine(workRoot,"app-reads","wechat-files");Directory.CreateDirectory(target);
        async Task<string> Copy(string stored,string originalName,string variant)
        {
            string path=StoredPath(stored);if(path.Length==0||!File.Exists(path))return "";var name=Path.GetFileName(originalName.Replace('\\','/'));foreach(var invalid in Path.GetInvalidFileNameChars())name=name.Replace(invalid,'_');if(name.Length>100)name=name[..80]+Path.GetExtension(name)[..Math.Min(16,Path.GetExtension(name).Length)];if(string.IsNullOrWhiteSpace(name))name="attachment"+Path.GetExtension(stored);var relative="app-reads/wechat-files/"+WeChatStore.Stable(id,stored,variant)+"-"+name;string destination=Path.Combine(workRoot,relative.Replace('/',Path.DirectorySeparatorChar));
            await using(var src=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read,81920,true))await using(var dst=new FileStream(destination,FileMode.Create,FileAccess.Write,FileShare.None,81920,true))await src.CopyToAsync(dst,cancellation);return relative;
        }
        bool encrypted=S(file,"status")=="encrypted"||S(file,"decodeStatus")=="failed"&&S(file,"type")=="image";var copied=await Copy(S(file,"storedName"),S(file,"name"),"readable");result[encrypted?"originalFile":"file"]=copied;result["mime"]=encrypted?"application/octet-stream":MaterialMime(file);result["readStatus"]=encrypted?"encrypted_unreadable":"ready";
        result["note"]=encrypted?"这是加密或封装原件，不能当作已经看过图像内容。":"file 为实际附件相对路径，可用原生工具直接分析；图片请直接查看图像内容。";
        if(includeOriginal&&S(file,"originalStoredName").Length>0){var original=await Copy(S(file,"originalStoredName"),S(file,"originalName","original.dat"),"encrypted-original");if(original.Length>0){result["originalFile"]=original;result["originalFileStatus"]="encrypted_or_wrapped_original";}}
        if(S(file,"extractedText").Length>0){var textRelative=copied+".extracted.txt";await File.WriteAllTextAsync(Path.Combine(workRoot,textRelative.Replace('/',Path.DirectorySeparatorChar)),S(file,"extractedText"),new UTF8Encoding(false),cancellation);result["extractedTextFile"]=textRelative;}
        return result;
    }
}
