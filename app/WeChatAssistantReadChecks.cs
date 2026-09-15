using System.Drawing.Imaging;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using StudentProgress.WeChat;

namespace StudentProgress;

internal static class WeChatAssistantReadChecks
{
    internal static async Task<JsonObject> RunAsync(string directory,string appRoot)
    {
        if(Directory.Exists(directory)&&Directory.EnumerateFileSystemEntries(directory).Any())throw new InvalidOperationException("微信只读检查需要空的独立目录。");
        Directory.CreateDirectory(directory);string data=Path.Combine(directory,"data"),module=Path.Combine(data,"wechat"),work=Path.Combine(directory,"workspace");Directory.CreateDirectory(module);Directory.CreateDirectory(work);
        string cfg=Path.Combine(module,"config.json");File.WriteAllText(cfg,"{\"enabled\":false,\"checkReminders\":false,\"archiveFrom\":\"all\",\"selectedConversationIds\":[],\"bindings\":[{\"conversationId\":\"b\",\"memberId\":\"member\",\"projectId\":\"\"}]}");var configBefore=File.ReadAllText(cfg);var business=new JsonObject{["students"]=new JsonArray(new JsonObject{["id"]="member",["name"]="成员"}),["projects"]=new JsonArray()};string businessBefore=business.ToJsonString();int imports=0;
        using var wx=new WeChatService(data,Path.Combine(directory,"no-model-runtime"),()=>business.DeepClone().AsObject(),_=>{imports++;throw new InvalidOperationException("Read API must not archive.");});
        var checks=new JsonArray();void Check(bool value,string name){if(!value)throw new InvalidOperationException("WeChat assistant read check failed: "+name);checks.Add(name);}
        Task<JsonObject> Read(string operation,JsonObject? args=null)=>wx.ReadAssistantDataAsync(operation,args??new(),work,CancellationToken.None);
        wx.Store.SaveConversation(new(){["id"]="a",["accountId"]="private-account",["externalId"]="friend-a",["name"]="较早联系人",["kind"]="friend",["sourceLastMessageAt"]="2026-09-10T10:00:00+08:00"});
        wx.Store.SaveConversation(new(){["id"]="b",["accountId"]="private-account",["externalId"]="friend-b",["name"]="研究项目群",["kind"]="group",["sourceLastMessageAt"]="2026-09-15T10:00:00+08:00"});
        wx.Store.SaveConversation(new(){["id"]="empty",["accountId"]="private-account",["externalId"]="friend-empty",["name"]="零消息",["kind"]="friend",["sourceLastMessageAt"]="2026-09-16T10:00:00+08:00"});
        var when=new DateTimeOffset(2026,9,14,10,0,0,TimeSpan.FromHours(8));string longText=new('中',18000);longText+="末尾证据 marker-tail";
        JsonObject Message(string id,string conv,string text,DateTimeOffset time,string kind="text")=>new(){["id"]=id,["conversationId"]=conv,["accountId"]="private-account",["externalId"]="private-source-id",["senderId"]="friend-b",["senderName"]="群内发送人",["when"]=time.ToString("O"),["kind"]=kind,["text"]=text,["rawContent"]="<msg>"+text+"</msg>",["source"]=new JsonObject{["database"]="C:\\private-source\\chat.db",["key"]="not-source-evidence"}};
        for(int i=0;i<125;i++)wx.Store.UpsertMessage(Message("same-"+i.ToString("D3"),"b",i==61?longText:"同秒消息 "+i,when));
        wx.Store.UpsertMessage(Message("other-conversation","a","百分号 100% 不是通配符",when.AddDays(1)));
        wx.Store.UpsertMessage(Message("tomorrow","b","明日证据",when.AddDays(1)));
        wx.Store.UpsertMessage(Message("image-msg","b","图片原文",when.AddMinutes(-1),"image"));
        wx.Store.UpsertMessage(Message("document-msg","b","成员提交文档",when.AddMinutes(-2),"file"));
        string files=Path.Combine(module,"files"),png=Path.Combine(files,"fixture.png"),dat=Path.Combine(files,"fixture.dat"),doc=Path.Combine(files,"fixture.txt");
        using(var bitmap=new Bitmap(21,13)){using var graphics=Graphics.FromImage(bitmap);graphics.Clear(Color.RoyalBlue);bitmap.Save(png,ImageFormat.Png);}File.WriteAllBytes(dat,[1,3,5,7]);File.WriteAllText(doc,"文档原始内容\n末尾内容不能截断",new UTF8Encoding(false));
        static string Hash(string path)=>Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
        wx.Store.UpsertFile(new(){["id"]="image",["messageId"]="image-msg",["name"]="图片.png",["size"]=new FileInfo(png).Length,["type"]="image",["status"]="ready",["mime"]="image/png",["decodeStatus"]="decoded",["originalStoredName"]="fixture.dat",["originalName"]="fixture.dat",["sourceWriteTicks"]=123L},"C:\\private-source\\image.dat","fixture.png",Hash(png),"");
        wx.Store.UpsertFile(new(){["id"]="encrypted",["messageId"]="image-msg",["name"]="unknown.dat",["size"]=4L,["type"]="image",["status"]="encrypted"},"C:\\private-source\\unknown.dat","fixture.dat",Hash(dat),"");
        wx.Store.UpsertFile(new(){["id"]="document",["messageId"]="document-msg",["name"]="进度文档.txt",["size"]=new FileInfo(doc).Length,["type"]="file",["status"]="ready"},"C:\\private-source\\doc.txt","fixture.txt",Hash(doc),"文档全文检索内容");
        wx.Store.UpsertFile(new(){["id"]="missing",["messageId"]="document-msg",["name"]="missing.txt",["size"]=0L,["type"]="file",["status"]="missing"},"C:\\private-source\\missing.txt","","","");
        wx.Store.SetMeta("lastSync","2026-09-15T12:34:00+08:00");

        var observe=await Read("wechat.observe");Check(observe["importedConversationCount"]!.GetValue<long>()==2&&observe["coverage"]!["lastSync"]!.GetValue<string>().Length>0&&!observe["coverage"]!["embeddingAvailable"]!.GetValue<bool>()&&observe["bindings"]!.AsArray().Count==1,"import_scope_counts_sync_coverage_and_explicit_bindings");
        var first=await Read("wechat.conversations",new(){["limit"]=1});var next=await Read("wechat.conversations",new(){["limit"]=1,["offset"]=first["nextOffset"]!.DeepClone()});Check(first["conversations"]![0]!["id"]!.GetValue<string>()=="b"&&next["conversations"]![0]!["id"]!.GetValue<string>()=="a"&&!next["hasMore"]!.GetValue<bool>(),"conversation_source_update_order_zero_hidden_pagination");
        var convSearch=await Read("wechat.conversations",new(){["query"]="项目"});Check(convSearch["total"]!.GetValue<long>()==1,"conversation_name_lookup");
        var ids=new List<string>();var args=new JsonObject{["conversationId"]="b",["query"]="同秒消息",["limit"]=17};
        do{var page=await Read("wechat.messages",args);ids.AddRange(page["messages"]!.AsArray().Select(x=>x!["id"]!.GetValue<string>()));if(!page["hasMore"]!.GetValue<bool>())break;foreach(var pair in page["nextCursor"]!.AsObject())args[pair.Key]=pair.Value!.DeepClone();}while(true);
        Check(ids.Count==124&&ids.Distinct().Count()==124&&ids.First()=="same-124"&&ids.Last()=="same-000","all_same_timestamp_pages_without_skips_or_duplicates");
        var dates=await Read("wechat.messages",new(){["conversationId"]="b",["from"]="2026-09-15",["to"]="2026-09-15"});Check(dates["messages"]!.AsArray().Count==1&&dates["messages"]![0]!["id"]!.GetValue<string>()=="tomorrow","date_filter_is_inclusive_calendar_day");
        var full=await Read("wechat.message",new(){["id"]="same-061"});Check(full["message"]!["text"]!.GetValue<string>()==longText&&full["message"]!["rawContent"]!.GetValue<string>().EndsWith("marker-tail</msg>"),"full_message_and_raw_xml_preserve_long_tail");
        var context=await Read("wechat.messages",new(){["conversationId"]="b",["around"]="same-061",["limit"]=7});Check(context["messages"]!.AsArray().Count==7&&context["messages"]![0]!["id"]!.GetValue<string>()=="same-058"&&context["messages"]![6]!["id"]!.GetValue<string>()=="same-064"&&context["hasOlder"]!.GetValue<bool>()&&context["hasNewer"]!.GetValue<bool>(),"around_message_uses_stable_neighbor_context");
        var newer=await Read("wechat.messages",new(){["conversationId"]="b",["after"]=when.ToUnixTimeMilliseconds(),["afterId"]="same-120",["limit"]=10});Check(newer["messages"]![0]!["id"]!.GetValue<string>()=="same-121"&&newer["messages"]!.AsArray().Count==5,"after_cursor_reads_forward_without_same_timestamp_loss");
        var literal=await Read("wechat.messages",new(){["query"]="100%"});Check(literal["messages"]!.AsArray().Count==1,"literal_search_does_not_treat_percent_as_sql_wildcard");
        var search=await Read("wechat.search",new(){["query"]="文档",["conversationId"]="b",["kind"]="file"});Check(search["results"]!.AsArray().Any(x=>x!["fileId"]!.GetValue<string>()=="document")&&search["retrieval"]!["mode"]!.GetValue<string>()=="keyword_only"&&!search["retrieval"]!["exhaustive"]!.GetValue<bool>(),"reuse_keyword_search_reports_top_k_and_missing_vectors");
        var fileList=await Read("wechat.files",new(){["query"]="全文检索"});Check(fileList["total"]!.GetValue<long>()==1&&fileList["files"]![0]!["id"]!.GetValue<string>()=="document","file_search_matches_extracted_content");
        var filePage=await Read("wechat.files",new(){["conversationId"]="b",["limit"]=1});Check(filePage["hasMore"]!.GetValue<bool>()&&filePage["total"]!.GetValue<long>()==4,"file_listing_has_total_and_pagination");
        var image=await Read("wechat.file",new(){["id"]="image",["includeOriginal"]=true});string imagePath=Path.Combine(work,image["file"]!.GetValue<string>().Replace('/',Path.DirectorySeparatorChar)),originalPath=Path.Combine(work,image["originalFile"]!.GetValue<string>().Replace('/',Path.DirectorySeparatorChar));using(var bitmap=Image.FromFile(imagePath))Check(bitmap.Width==21&&bitmap.Height==13&&Hash(imagePath)==Hash(png)&&Hash(originalPath)==Hash(dat),"copies_actual_decoded_image_and_distinct_encrypted_original");
        var document=await Read("wechat.file",new(){["id"]="document"});Check(File.ReadAllText(Path.Combine(work,document["file"]!.GetValue<string>().Replace('/',Path.DirectorySeparatorChar)))==File.ReadAllText(doc),"file_api_preserves_original_document_bytes");
        var encrypted=await Read("wechat.file",new(){["id"]="encrypted"});var missing=await Read("wechat.file",new(){["id"]="missing"});Check(encrypted["file"]==null&&encrypted["originalFile"]!=null&&encrypted["readStatus"]!.GetValue<string>()=="encrypted_unreadable"&&!missing["available"]!.GetValue<bool>(),"encrypted_and_missing_files_are_not_claimed_readable");
        Check(new[]{observe,first,next,convSearch,full,context,newer,literal,search,fileList,filePage,image,document,encrypted,missing}.All(output=>
        {string json=output.ToJsonString();return !json.Contains("private-source")&&!json.Contains("private-account")&&!json.Contains("not-source-evidence")&&!json.Contains("sourcePath")&&!json.Contains("storedName")&&!json.Contains("previewUrl")&&!json.Contains("sourceWriteTicks");}),"all_outputs_exclude_host_secret_and_source_path_metadata");
        foreach(var (operation,payload) in new[]{("wechat.sync",new JsonObject()),("wechat.settings.save",new JsonObject()),("wechat.message",new JsonObject{["id"]="same-061",["path"]="C:\\private-source"}),("wechat.messages",new JsonObject{["conversationId"]="a",["around"]="same-061"})})
        {bool rejected=false;try{await Read(operation,payload);}catch(ArgumentException){rejected=true;}catch(InvalidOperationException){rejected=true;}Check(rejected,"forbidden_or_invalid_read_rejected_"+checks.Count);}
        Check(imports==0&&File.ReadAllText(cfg)==configBefore&&business.ToJsonString()==businessBefore,"does_not_mutate_business_configuration_or_archive");
        return new(){["ok"]=true,["checks"]=checks,["count"]=checks.Count,["modelCalls"]=0,["embeddingCalls"]=0,["liveWechatReads"]=0};
    }
}
