using System.Text.Json.Nodes;
using System.Security.Cryptography;

namespace StudentProgress.WeChat;

internal static class WeChatChecks
{
    public static async Task<JsonObject> Run(string isolatedRoot,string baseDir,bool embeddingTest=false)
    {
        if(Directory.Exists(isolatedRoot)&&Directory.EnumerateFileSystemEntries(isolatedRoot).Any())throw new InvalidOperationException("微信检查必须使用空的隔离目录。");Directory.CreateDirectory(isolatedRoot);var checks=new JsonArray();void Check(bool pass,string label){if(!pass)throw new InvalidOperationException(label);checks.Add(label);}
        var business=new JsonObject{["students"]=new JsonArray(new JsonObject{["id"]="member",["name"]="测试成员"}),["projects"]=new JsonArray(new JsonObject{["id"]="project",["name"]="测试项目"})};var imports=new List<JsonObject>();
        using var service=new WeChatService(isolatedRoot,baseDir,()=>business.DeepClone().AsObject(),p=>{imports.Add(p.DeepClone().AsObject());return new(){["messageId"]="imported",["replayed"]=false};});
        var store=service.Store;var account="test-account";var external="friend-1";var conversation=WeChatStore.Stable(account,external);
        store.SaveConversation(new(){["id"]=conversation,["accountId"]=account,["externalId"]=external,["name"]="与成员姓名不同的备注",["kind"]="friend"});
        Check((service.Configuration["selectedConversationIds"] as JsonArray)?.Count==0,"默认没有选中或导入任何会话");
        service.SaveConfiguration(new(){["enabled"]=false,["checkReminders"]=false,["selectedConversationIds"]=new JsonArray(conversation)});
        var oldWhen=DateTimeOffset.Now.AddDays(-20);var when=DateTimeOffset.Now.AddMinutes(-1);var m1=new JsonObject{["id"]="m1",["accountId"]=account,["conversationId"]=conversation,["senderId"]=external,["senderName"]="小王",["isSelf"]=false,["kind"]="text",["text"]="明天下午两点到南湖开会，记得带项目报告。",["when"]=when.ToString("O"),["rawContent"]="完整来源正文"};
        Check(store.UpsertMessage(m1),"首次读取插入聊天");Check(!store.UpsertMessage(m1),"再次读取同一记录不会重复或反复重建索引");
        var m2=m1.DeepClone().AsObject();m2["id"]="m2";m2["text"]="历史回填的聊天";m2["when"]=oldWhen.ToString("O");store.UpsertMessage(m2);
        Check(service.AnalysisMessages(DateTimeOffset.Now.AddMinutes(-1),false).Count==2,"增量分析按导入时间接收历史回填");Check(service.AnalysisMessages(DateTimeOffset.MinValue,true).Count==1,"首次提醒分析仅自动处理最近七天");
        var body=System.Text.Encoding.UTF8.GetBytes("这是项目报告的完整原件。\n南湖会议实验结果。");var hash=Convert.ToHexString(SHA256.HashData(body)).ToLowerInvariant();var stored=hash+".txt";await File.WriteAllBytesAsync(Path.Combine(service.DataDirectory,"files",stored),body);
        var f=new JsonObject{["id"]="f1",["messageId"]="m1",["name"]="南湖项目报告.txt",["size"]=(long)body.Length,["type"]="file",["available"]=true};store.UpsertFile(f,"C:\\private-source.txt",stored,hash,System.Text.Encoding.UTF8.GetString(body));
        Check(store.KeywordCandidates("南湖").Count>0,"两字中文能检索聊天和文件正文");
        var snapshot=(await service.HandleAsync("wechat.messages",new(){["conversationId"]=conversation})).ToJsonString();Check(!snapshot.Contains("private-source")&&!snapshot.Contains("storedName"),"界面快照不泄露本机路径");
        store.UpsertFile(f,"","","","");Check(WeChatStore.S(store.File("f1",true),"storedName")==stored,"微信清理缓存后仍保留已保存原件");
        await service.HandleAsync("wechat.binding.save",new(){["conversationId"]=conversation,["memberId"]="member",["archiveFrom"]=DateTime.Today.ToString("yyyy-MM-dd")});Check(imports.Count==1&&WeChatStore.S(imports[0],"scopeId")=="member","关联按起始日期归档到正确成员");
        await service.HandleAsync("wechat.binding.save",new(){["conversationId"]=conversation,["memberId"]="member",["archiveFrom"]=""});Check(imports.Count==1,"扩大归档日期不会重复加入同一文件");
        var reference=(await service.HandleAsync("wechat.reference.create",new(){["fileId"]="f1"}))["reference"]!.AsObject();var refId=WeChatStore.S(reference,"scopeId");Check(WeChatStore.S(service.GetReference(refId),"conversationId")==conversation,"文件引用可定位原会话");
        var workspace=Path.Combine(isolatedRoot,"evidence");var manifest=await service.WriteReferenceWorkspaceAsync(new JsonArray(reference.DeepClone()),workspace,CancellationToken.None);Check(manifest["originalFileCount"]!.GetValue<int>()==1,"Codex资料包含原文件");var original=Directory.GetFiles(Path.Combine(workspace,"wechat","originals")).Single(x=>!x.EndsWith(".extracted.txt"));Check((await File.ReadAllBytesAsync(original)).SequenceEqual(body),"核对使用与微信保存文件相同的完整原件");
        var backup=Path.Combine(isolatedRoot,"wechat-materials.zip");await service.HandleAsync("wechat.export",new(){["path"]=backup});using(var zip=System.IO.Compression.ZipFile.OpenRead(backup)){Check(zip.GetEntry("records.sqlite")!=null&&zip.Entries.All(e=>e.FullName is not("config.json" or "suggestions.json")),"独立微信导出只包含资料，设置交由通用导出");}
        store.SaveConversation(new(){["id"]=conversation,["accountId"]=account,["externalId"]=external,["name"]="改过的备注",["kind"]="friend"});Check(WeChatStore.S(service.Configuration["bindings"]?.AsArray().First(),"conversationId")==conversation,"修改微信备注不破坏成员关联");
        await service.HandleAsync("wechat.import",new(){["path"]=backup});Check(store.Counts()["messages"]!.GetValue<long>()==2&&store.File("f1",true)!=null,"独立资料恢复保留消息和文件");
        var around=await service.HandleAsync("wechat.messages",new(){["conversationId"]=conversation,["around"]="m1"});Check((around["messages"] as JsonArray)!.Any(x=>WeChatStore.S(x,"id")=="m1"),"搜索结果可以跳到原聊天附近");
        JsonObject? modelResult=null;
        if(embeddingTest)
        {
            using var model=new WeChatEmbedding(baseDir,service.DataDirectory);var watch=System.Diagnostics.Stopwatch.StartNew();
            var texts=new[]{"明天下午两点到南湖校区开会，请带上项目报告。","今天晚上想吃火锅，周末一起出去逛街。","数据库查询计划的连接顺序优化实验已经完成。"};var vectors=texts.Select(t=>model.Encode(t)).ToArray();var query=model.Encode("南湖会议什么时候开始，要准备什么资料？");model.EnsureIndex(store,true);for(int i=0;i<vectors.Length;i++)model.Add(i+100,vectors[i]);var found=model.Search(query,3);Check(found[0]==100,"真实本地 BGE-M3 相似度搜索把相关聊天排第一");model.SaveIndex();
            Check(query.Length==1024&&Math.Abs(query.Sum(v=>(double)v*v)-1)<0.001,"FP32高精度向量维度和归一化正确");
            Check(model.Tokenize("明天下午两点到南湖开会").SequenceEqual(new long[]{0,6,72938,33253,6442,2391,789,4617,19664,4185,1147,2}),"分词 ID 与官方 Hugging Face tokenizer 独立结果一致");
            modelResult=new(){["model"]="BAAI/bge-m3",["precision"]="float32",["dimensions"]=query.Length,["milliseconds"]=watch.ElapsedMilliseconds,["tokens"]=new JsonArray(model.Tokenize("明天下午两点到南湖开会").Select(x=>(JsonNode?)JsonValue.Create(x)).ToArray()),["cosineSimilarities"]=new JsonArray(vectors.Select(v=>(JsonNode?)JsonValue.Create(v.Zip(query).Sum(p=>(double)p.First*p.Second))).ToArray())};
        }
        return new(){["ok"]=true,["checks"]=checks,["count"]=checks.Count,["embedding"]=modelResult,["modelCalls"]=0};
    }
}
