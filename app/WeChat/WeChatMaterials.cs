using System.Text;
using System.Text.Json.Nodes;

namespace StudentProgress.WeChat;

public sealed partial class WeChatService
{
    internal JsonObject CreateReference(string conversationId,IEnumerable<string>? messageIds=null,string fileId="")
    {
        if(fileId.Length>0){var f=store.File(fileId)??throw new InvalidOperationException("微信文件不存在。");var m=store.Message(S(f,"messageId"))!;conversationId=S(m,"conversationId");messageIds=[S(m,"id")];}
        var conv=store.Conversation(conversationId)??throw new InvalidOperationException("微信会话不存在。");var ids=messageIds?.Where(x=>x.Length>0).Distinct().Order().ToArray();if(ids is{Length:0})ids=null;
        if(ids!=null)foreach(var id in ids)if(store.Message(id) is not JsonObject m||S(m,"conversationId")!=conversationId)throw new InvalidOperationException("引用消息不属于此会话。");
        var through=DateTimeOffset.Now.ToUnixTimeMilliseconds();var title=S(conv,"name")+(fileId.Length>0?" · "+S(store.File(fileId),"name"):ids==null?" · 全部聊天":$" · {ids.Length} 条聊天");
        var key=WeChatStore.Stable(conversationId,string.Join(',',ids??[]),fileId,ids==null?store.Meta("lastSync"):"");
        var reference=new JsonObject{["id"]=key,["conversationId"]=conversationId,["messageIds"]=ids==null?null:new JsonArray(ids.Select(x=>(JsonNode?)JsonValue.Create(x)).ToArray()),["fileId"]=fileId,["through"]=through,["title"]=title,["createdAt"]=DateTimeOffset.Now.ToString("O")};
        // Existing references keep their original cutoff so retrying a click is idempotent.
        if(store.Reference(key)==null)store.SaveReference(reference);
        return new(){["scopeType"]="wechat",["scopeId"]=key,["title"]=title};
    }
    public JsonObject GetReference(string referenceId)
    {
        var r=store.Reference(referenceId)??throw new InvalidOperationException("微信引用资料不可用；请先恢复微信资料备份。");return new(){["reference"]=r.DeepClone(),["conversationId"]=S(r,"conversationId"),["messageIds"]=r["messageIds"]?.DeepClone(),["messages"]=new JsonArray(ReferenceMessages(r).Take(100).Cast<JsonNode?>().ToArray())};
    }
    IEnumerable<JsonObject> ReferenceMessages(JsonObject reference,bool host=false)
    {
        HashSet<string>? ids=reference["messageIds"] is JsonArray a?new(a.Select(x=>x!.GetValue<string>())):null;
        return store.ReadMessages(S(reference,"conversationId"),through:Long(reference,"through",long.MaxValue),ids:ids,host:host);
    }
    internal JsonArray AnalysisMessages(DateTimeOffset importedSince,bool firstRun)
    {
        var selected=Selected();var from=firstRun?DateTimeOffset.Now.AddDays(-7).ToUnixTimeMilliseconds():0;
        return new JsonArray(store.AnalysisMessages(importedSince.ToUnixTimeMilliseconds(),from).Where(m=>selected.Contains(S(m,"conversationId"))).Select(m=>{m["conversationName"]=S(store.Conversation(S(m,"conversationId")),"name");return (JsonNode?)m;}).ToArray());
    }
    // Raw source evidence, including original XML and local files, is supplied to native Codex.
    public async Task<JsonObject> WriteReferenceWorkspaceAsync(JsonArray references,string workspace,CancellationToken ct)
    {
        Directory.CreateDirectory(workspace);var targetRoot=Path.Combine(workspace,"wechat");Directory.CreateDirectory(targetRoot);var originals=Path.Combine(targetRoot,"originals");Directory.CreateDirectory(originals);var sources=new JsonArray();var copied=new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
        foreach(var item in references.OfType<JsonObject>())
        {
            ct.ThrowIfCancellationRequested();var id=S(item,"scopeId",S(item,"id"));var reference=store.Reference(id)??throw new InvalidOperationException("引用的微信资料不在本机，请恢复微信资料备份。");
            var filename=id+".jsonl";var jsonl=Path.Combine(targetRoot,filename);long count=0;
            await using(var output=new FileStream(jsonl,FileMode.Create,FileAccess.Write,FileShare.None,81920,true))await using(var writer=new StreamWriter(output,new UTF8Encoding(false)))
            {
                foreach(var message in ReferenceMessages(reference,true))
                {
                    ct.ThrowIfCancellationRequested();var filterFile=S(reference,"fileId");var files=new JsonArray();
                    foreach(var f in (message["attachments"] as JsonArray??new()).OfType<JsonObject>())
                    {
                        if(filterFile.Length>0&&S(f,"id")!=filterFile)continue;var stored=S(f,"storedName");var source=StoredPath(stored);var sanitized=f.DeepClone().AsObject();sanitized.Remove("sourcePath");sanitized.Remove("storedName");
                        if(source.Length>0&&File.Exists(source))
                        {
                            if(!copied.TryGetValue(stored,out var relative))
                            {
                                var safeName=Path.GetFileName(S(f,"name","文件"));foreach(char invalid in Path.GetInvalidFileNameChars())safeName=safeName.Replace(invalid,'_');if(safeName.Length>150)safeName=safeName[..130]+Path.GetExtension(safeName);relative="wechat/originals/"+S(f,"id")+"-"+safeName;
                                await using var src=new FileStream(source,FileMode.Open,FileAccess.Read,FileShare.Read,81920,true);await using var dst=new FileStream(Path.Combine(workspace,relative.Replace('/',Path.DirectorySeparatorChar)),FileMode.Create,FileAccess.Write,FileShare.None,81920,true);await src.CopyToAsync(dst,ct);copied[stored]=relative;
                            }
                            sanitized["file"]=relative;
                            var extracted=S(f,"extractedText");if(extracted.Length>0){var textRelative=relative+".extracted.txt";await File.WriteAllTextAsync(Path.Combine(workspace,textRelative.Replace('/',Path.DirectorySeparatorChar)),extracted,new UTF8Encoding(false),ct);sanitized["extractedTextFile"]=textRelative;}
                        }
                        sanitized.Remove("extractedText");files.Add(sanitized);
                    }
                    message["attachments"]=files;await writer.WriteLineAsync(message.ToJsonString().AsMemory(),ct);count++;
                }
            }
            sources.Add(new JsonObject{["scopeType"]="wechat",["scopeId"]=id,["title"]=S(reference,"title"),["messagesFile"]="wechat/"+filename,["messageCount"]=count});
        }
        var manifest=new JsonObject{["sources"]=sources,["originalFileCount"]=copied.Count,["note"]="聊天 JSONL 保留原始正文、发送时间和来源，附件 file 指向原件；相似度索引不作为事实依据。"};await File.WriteAllTextAsync(Path.Combine(targetRoot,"manifest.json"),manifest.ToJsonString(),new UTF8Encoding(false),ct);return manifest;
    }
    async Task<JsonObject> ArchiveReferenceAsync(JsonObject payload,CancellationToken ct)
    {
        var refId=S(payload["reference"] as JsonObject,"scopeId",S(payload,"referenceId"));var reference=store.Reference(refId)??throw new InvalidOperationException("微信引用不存在。");string scopeType=S(payload,"scopeType"),scopeId=S(payload,"scopeId");
        var text=new StringBuilder();var files=new JsonArray();var fileIds=new HashSet<string>();var first="";long messageCount=0;
        // The complete source is materialized as a text attachment; the archive bubble remains compact.
        var materialName=refId+".txt";var materialDir=Path.Combine(root,"archive-material");Directory.CreateDirectory(materialDir);var materialPath=Path.Combine(materialDir,materialName);
        await using(var writer=new StreamWriter(new FileStream(materialPath,FileMode.Create,FileAccess.Write,FileShare.Read,81920,true),new UTF8Encoding(false)))
        {
            foreach(var m in ReferenceMessages(reference,true))
            {
                ct.ThrowIfCancellationRequested();if(first.Length==0)first=S(m,"when");var line=S(m,"when")+" "+S(m,"senderName")+"\n"+S(m,"text")+"\n";await writer.WriteLineAsync(line.AsMemory(),ct);if(text.Length<3000)text.Append(line.AsSpan(0,Math.Min(line.Length,3000-text.Length)));messageCount++;
                foreach(var f in (m["attachments"] as JsonArray??new()).OfType<JsonObject>()){if(S(reference,"fileId").Length>0&&S(f,"id")!=S(reference,"fileId"))continue;var path=StoredPath(S(f,"storedName"));if(path.Length>0&&File.Exists(path)&&fileIds.Add(S(f,"id")))files.Add(new JsonObject{["id"]=S(f,"id"),["name"]=S(f,"name"),["path"]=path});}
            }
        }
        files.Insert(0,new JsonObject{["id"]=refId,["name"]=S(reference,"title")+".txt",["path"]=materialPath});
        var request=new JsonObject{["requestId"]="wechat-ref-"+WeChatStore.Stable(refId,scopeType,scopeId),["scopeType"]=scopeType,["scopeId"]=scopeId,["text"]=S(reference,"title")+$"\n{messageCount} 条微信记录\n"+text,["createdAt"]=first.Length>0?first:DateTimeOffset.Now.ToString("O"),["sourceReferences"]=new JsonArray(new JsonObject{["scopeType"]="wechat",["scopeId"]=refId,["title"]=S(reference,"title")}),["files"]=files};
        return archiveImporter(request);
    }
    async Task ArchiveFilesAsync(CancellationToken ct)
    {
        var settings=Configuration;var from=S(settings,"archiveFrom",DateTime.Today.ToString("yyyy-MM-dd"));var bindings=(settings["bindings"] as JsonArray??new()).OfType<JsonObject>().ToArray();if(bindings.Length==0)return;var selected=Selected();var business=businessSnapshot();var existingMembers=new HashSet<string>((business["students"] as JsonArray??new()).Select(x=>S(x,"id")));var existingProjects=new HashSet<string>((business["projects"] as JsonArray??new()).Select(x=>S(x,"id")));
        foreach(var f in store.ArchiveCandidates().OfType<JsonObject>())
        {
            ct.ThrowIfCancellationRequested();var m=store.Message(S(f,"messageId"));if(m==null||B(m,"isSelf")||!selected.Contains(S(m,"conversationId")))continue;
            var conv=store.Conversation(S(m,"conversationId"));var senderConv=WeChatStore.Stable(S(m,"accountId"),S(m,"senderId"));var applicable=bindings.Where(b=>S(b,"conversationId")==S(m,"conversationId")||S(b,"conversationId")==senderConv);
            foreach(var binding in applicable)
            {
                if(from!="all"&&DateTimeOffset.Parse(S(m,"when")).LocalDateTime.Date<DateTime.Parse(from).Date)continue;
                foreach(var (type,target) in new[]{("student",S(binding,"memberId")),("project",S(binding,"projectId"))})
                {
                    if(target.Length==0||(type=="student"?!existingMembers.Contains(target):!existingProjects.Contains(target)))continue;var receipt="file-"+WeChatStore.Stable(S(f,"id"),type,target);if(store.HasReceipt(receipt))continue;
                    var reference=CreateReference(S(m,"conversationId"),[S(m,"id")],S(f,"id"));
                    var result=archiveImporter(new(){["requestId"]="wechat-"+receipt,["scopeType"]=type,["scopeId"]=target,["text"]="微信文件 · "+S(conv,"name")+" · "+S(m,"senderName")+(S(m,"text").Length>0?"\n"+S(m,"text"):""),["createdAt"]=S(m,"when"),["sourceReferences"]=new JsonArray(reference),["files"]=new JsonArray(new JsonObject{["id"]=S(f,"id"),["name"]=S(f,"name"),["path"]=StoredPath(S(f,"storedName"))})});store.Receipt(receipt,result);
                }
            }
        }
        await Task.CompletedTask;
    }
}
