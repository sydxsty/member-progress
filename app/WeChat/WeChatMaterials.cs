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
        Directory.CreateDirectory(workspace);var targetRoot=Path.Combine(workspace,"wechat");Directory.CreateDirectory(targetRoot);var originals=Path.Combine(targetRoot,"originals");Directory.CreateDirectory(originals);var sources=new JsonArray();var copied=new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);var imageFiles=new HashSet<string>();
        async Task<string> CopyOriginalAsync(string stored,string id,string name)
        {
            var source=StoredPath(stored);if(source.Length==0||!File.Exists(source))return "";
            if(copied.TryGetValue(stored,out var existing))return existing;
            var safeName=Path.GetFileName(name.Replace('\\','/'));foreach(char invalid in Path.GetInvalidFileNameChars())safeName=safeName.Replace(invalid,'_');if(safeName.Length>150)safeName=safeName[..130]+Path.GetExtension(safeName);if(safeName.Length==0)safeName="文件"+Path.GetExtension(stored);
            var relative="wechat/originals/"+id+"-"+safeName;
            await using var src=new FileStream(source,FileMode.Open,FileAccess.Read,FileShare.Read,81920,true);await using var dst=new FileStream(Path.Combine(workspace,relative.Replace('/',Path.DirectorySeparatorChar)),FileMode.Create,FileAccess.Write,FileShare.None,81920,true);await src.CopyToAsync(dst,ct);copied[stored]=relative;return relative;
        }
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
                        if(filterFile.Length>0&&S(f,"id")!=filterFile)continue;var stored=S(f,"storedName");var sanitized=f.DeepClone().AsObject();
                        foreach(var field in new[]{"sourcePath","storedName","originalPath","originalStoredName","previewUrl","sourceWriteTicks","sourceSize"})sanitized.Remove(field);
                        var relative=await CopyOriginalAsync(stored,S(f,"id"),S(f,"name","文件"));
                        if(relative.Length>0)
                        {
                            if(S(f,"status")=="encrypted"||S(f,"decodeStatus")=="failed"&&S(f,"type")=="image")
                            {
                                sanitized["originalFile"]=relative;sanitized["readStatus"]="图片原件已保存，当前副本尚未成功解码，不能当作已读取图像内容。";
                            }
                            else
                            {
                                sanitized["file"]=relative;sanitized["mime"]=MaterialMime(f);
                                if(MaterialMime(f).StartsWith("image/",StringComparison.Ordinal)){imageFiles.Add(relative);sanitized["readStatus"]="可读取的图片文件已提供；图像内容需要直接查看，文字提取结果不代表图像内容。";}
                            }
                            var original=await CopyOriginalAsync(S(f,"originalStoredName"),S(f,"id")+"-original",S(f,"originalName","原始图片.dat"));
                            if(original.Length>0&&original!=relative){sanitized["originalFile"]=original;sanitized["originalFileStatus"]="微信加密或封装原件，已解码图像见 file。";}
                            var extracted=S(f,"extractedText");if(extracted.Length>0){var textRelative=relative+".extracted.txt";await File.WriteAllTextAsync(Path.Combine(workspace,textRelative.Replace('/',Path.DirectorySeparatorChar)),extracted,new UTF8Encoding(false),ct);sanitized["extractedTextFile"]=textRelative;}
                        }
                        sanitized.Remove("extractedText");files.Add(sanitized);
                    }
                    message["attachments"]=files;await writer.WriteLineAsync(message.ToJsonString().AsMemory(),ct);count++;
                }
            }
            sources.Add(new JsonObject{["scopeType"]="wechat",["scopeId"]=id,["title"]=S(reference,"title"),["messagesFile"]="wechat/"+filename,["messageCount"]=count});
        }
        var manifest=new JsonObject{["sources"]=sources,["originalFileCount"]=copied.Count,["imageFileCount"]=imageFiles.Count,["note"]="聊天 JSONL 保留原始正文、发送时间和来源；附件 file 指向可直接读取的文件，图片为已识别格式。originalFile 若存在是另行保留的微信加密或封装原件。图片需直接查看内容，未解码图片不能当作已经读取；相似度索引不作为事实依据。"};await File.WriteAllTextAsync(Path.Combine(targetRoot,"manifest.json"),manifest.ToJsonString(),new UTF8Encoding(false),ct);return manifest;
    }
    static string MaterialMime(JsonObject file)
    {
        var mime=S(file,"mime");if(mime.Length>0&&mime!="application/octet-stream")return mime;
        return Path.GetExtension(S(file,"name")).ToLowerInvariant() switch{".jpg" or ".jpeg"=>"image/jpeg",".png"=>"image/png",".gif"=>"image/gif",".webp"=>"image/webp",".bmp"=>"image/bmp",".avif"=>"image/avif",".txt"=>"text/plain",_=>"application/octet-stream"};
    }
    JsonArray ArchiveMaterialFiles(JsonObject file)
    {
        var files=new JsonArray();var path=StoredPath(S(file,"storedName"));if(path.Length>0&&File.Exists(path))files.Add(new JsonObject{["id"]=S(file,"id"),["name"]=S(file,"name"),["path"]=path,["mime"]=MaterialMime(file)});
        var original=StoredPath(S(file,"originalStoredName"));if(original.Length>0&&original!=path&&File.Exists(original))files.Add(new JsonObject{["id"]=S(file,"id")+"-original",["name"]=S(file,"originalName","原始图片.dat"),["path"]=original,["mime"]="application/octet-stream"});
        return files;
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
                foreach(var f in (m["attachments"] as JsonArray??new()).OfType<JsonObject>()){if(S(reference,"fileId").Length>0&&S(f,"id")!=S(reference,"fileId"))continue;if(fileIds.Add(S(f,"id")))foreach(var file in ArchiveMaterialFiles(f))files.Add(file!.DeepClone());}
            }
        }
        files.Insert(0,new JsonObject{["id"]=refId,["name"]=S(reference,"title")+".txt",["path"]=materialPath});
        var request=new JsonObject{["requestId"]="wechat-ref-"+WeChatStore.Stable(refId,scopeType,scopeId),["scopeType"]=scopeType,["scopeId"]=scopeId,["refreshFiles"]=true,["text"]=S(reference,"title")+$"\n{messageCount} 条微信记录\n"+text,["createdAt"]=first.Length>0?first:DateTimeOffset.Now.ToString("O"),["sourceReferences"]=new JsonArray(new JsonObject{["scopeType"]="wechat",["scopeId"]=refId,["title"]=S(reference,"title")}),["files"]=files};
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
                    if(target.Length==0||(type=="student"?!existingMembers.Contains(target):!existingProjects.Contains(target)))continue;var receipt="file-"+WeChatStore.Stable(S(f,"id"),type,target);var imageRevision=MaterialMime(f).StartsWith("image/",StringComparison.Ordinal)&&S(f,"status")!="encrypted"?receipt+"-image-"+S(f,"hash"):"";if(store.HasReceipt(receipt)&&(imageRevision.Length==0||store.HasReceipt(imageRevision)))continue;
                    var reference=CreateReference(S(m,"conversationId"),[S(m,"id")],S(f,"id"));
                    var result=archiveImporter(new(){["requestId"]="wechat-"+receipt,["scopeType"]=type,["scopeId"]=target,["refreshFiles"]=true,["text"]="微信文件 · "+S(conv,"name")+" · "+S(m,"senderName")+(S(m,"text").Length>0?"\n"+S(m,"text"):""),["createdAt"]=S(m,"when"),["sourceReferences"]=new JsonArray(reference),["files"]=ArchiveMaterialFiles(f)});store.Receipt(receipt,result);if(imageRevision.Length>0)store.Receipt(imageRevision,result);
                }
            }
        }
        await Task.CompletedTask;
    }

    internal Task BackfillReferencedImagesAsync(CancellationToken ct)
    {
        var business=businessSnapshot();
        var activeScopes=(business["students"] as JsonArray??new()).Select(s=>"student:"+S(s,"id")).Concat((business["projects"] as JsonArray??new()).Select(p=>"project:"+S(p,"id"))).ToHashSet();
        var archives=(business["messages"] as JsonArray??new()).OfType<JsonObject>().Where(m=>S(m,"sourceType")=="wechat"&&activeScopes.Contains(S(m,"scopeType")+":"+S(m,"scopeId"))).ToArray();
        if(archives.Length==0)return Task.CompletedTask;
        // Read only attachment metadata here. Full conversation text and agent workspaces are
        // never regenerated by this backfill; unchanged image revisions stop at their receipt.
        var images=store.ArchiveCandidates().OfType<JsonObject>().Where(f=>MaterialMime(f).StartsWith("image/",StringComparison.Ordinal)&&S(f,"status")!="encrypted")
            .Select(f=>(File:f,Message:store.Message(S(f,"messageId")))).Where(x=>x.Message!=null).ToArray();
        foreach(var archive in archives)
        foreach(var source in (archive["sourceReferences"] as JsonArray??new()).OfType<JsonObject>().Where(r=>S(r,"scopeType")=="wechat"))
        {
            ct.ThrowIfCancellationRequested();var refId=S(source,"scopeId");var reference=store.Reference(refId);if(reference==null)continue;
            var requestId="wechat-ref-"+WeChatStore.Stable(refId,S(archive,"scopeType"),S(archive,"scopeId"));
            var expectedId=Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes("wechat:"+requestId))).ToLowerInvariant()[..32];
            // Automatic file archives have a different stable request and are handled above.
            if(S(archive,"id")!=expectedId)continue;
            var ids=reference["messageIds"] is JsonArray selected?selected.Select(x=>x!.GetValue<string>()).ToHashSet():null;
            var files=new JsonArray();var revisions=new List<string>();
            foreach(var item in images)
            {
                ct.ThrowIfCancellationRequested();var f=item.File;var m=item.Message!;
                if(S(m,"conversationId")!=S(reference,"conversationId")||ids!=null&&!ids.Contains(S(m,"id"))||DateTimeOffset.Parse(S(m,"when")).ToUnixTimeMilliseconds()>Long(reference,"through",long.MaxValue)||S(reference,"fileId").Length>0&&S(reference,"fileId")!=S(f,"id"))continue;
                var revision="archive-image-"+WeChatStore.Stable(expectedId,S(f,"id"),S(f,"hash"));if(store.HasReceipt(revision))continue;
                var material=ArchiveMaterialFiles(f);if(material.Count==0)continue;
                foreach(var file in material)files.Add(file!.DeepClone());revisions.Add(revision);
            }
            if(files.Count==0)continue;
            var result=archiveImporter(new JsonObject{["requestId"]=requestId,["scopeType"]=S(archive,"scopeType"),["scopeId"]=S(archive,"scopeId"),["refreshFiles"]=true,["sourceReferences"]=new JsonArray(source.DeepClone()),["files"]=files});
            foreach(var revision in revisions)store.Receipt(revision,result);
        }
        return Task.CompletedTask;
    }
}
