using System.Drawing.Imaging;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using StudentProgress.WeChat;

namespace StudentProgress;

// One isolated material/import pass. No live WeChat, embedding, OCR or GPT call.
internal static class WeChatImageMaterialsChecks
{
    public static int Run(string directory,bool backfillOnly=false)
    {
        var checks=new List<string>();bool owns=false;
        try
        {
            if(Directory.Exists(directory)&&Directory.EnumerateFileSystemEntries(directory).Any())return 1;
            Directory.CreateDirectory(directory);owns=true;(backfillOnly?RunBackfillAsync(directory,checks):RunAsync(directory,checks)).GetAwaiter().GetResult();Write(true,null);return 0;
        }
        catch(Exception e){if(owns)Write(false,e.ToString());return 1;}
        void Write(bool ok,string? error)=>File.WriteAllText(Path.Combine(directory,"wechat-image-materials-result.json"),JsonSerializer.Serialize(new{ok,count=checks.Count,checks,error,modelCalls=0,embeddingCalls=0,liveWechatReads=0},new JsonSerializerOptions{WriteIndented=true}),new UTF8Encoding(false));
    }
    static JsonObject Obj(object o)=>JsonSerializer.SerializeToNode(o)!.AsObject();
    static string S(JsonNode? n,string key)=>n?[key]?.GetValue<string>()??"";
    static string Hash(string path){using var source=File.OpenRead(path);return Convert.ToHexString(SHA256.HashData(source)).ToLowerInvariant();}

    static async Task RunBackfillAsync(string directory,List<string> checks)
    {
        string actual=Path.Combine(directory,"data"),module=Path.Combine(actual,"wechat");Directory.CreateDirectory(module);File.WriteAllText(Path.Combine(actual,"initialized"),"isolated-manual-image-backfill");
        File.WriteAllText(Path.Combine(actual,"state.json"),new JsonObject
        {
            ["schemaVersion"]=1,["students"]=new JsonArray(Obj(new{id="member",name="成员",grade="",tags=Array.Empty<string>(),bio="",currentSummary="保留进展",summaryStatus="idle",summaryError=""})),
            ["projects"]=new JsonArray(),["tasks"]=new JsonArray(),["messages"]=new JsonArray(),["alarms"]=new JsonArray(),["settings"]=Obj(new{currentStudentId="member",baseUrl="http://127.0.0.1:1/v1",model="",apiMode="responses"}),["status"]=new JsonObject()
        }.ToJsonString());
        File.WriteAllText(Path.Combine(module,"config.json"),Obj(new{enabled=false,checkReminders=false,checkMinutes=60,archiveFrom="all",selectedConversationIds=Array.Empty<string>(),bindings=Array.Empty<object>()}).ToJsonString());
        using var app=new AppService(actual);int imports=0;
        using var wx=new WeChatService(actual,Path.Combine(directory,"absent-runtime"),app.Snapshot,p=>{imports++;return app.ImportWeChatArchive(p);});
        const string conversation="manual-conversation",messageId="manual-image",fileId="manual-file";
        wx.Store.SaveConversation(Obj(new{id=conversation,accountId="fixture",externalId="friend",name="独立微信备注",kind="friend"}));
        wx.Store.UpsertMessage(Obj(new{id=messageId,accountId="fixture",conversationId=conversation,senderId="friend",senderName="发送人",isSelf=false,kind="image",text="归档后不改动的原始正文",when=DateTimeOffset.Now.AddMinutes(-1).ToString("O")}));
        var dat=Path.Combine(module,"files","manual.dat");File.WriteAllBytes(dat,new byte[]{12,43,84,31});
        wx.Store.UpsertFile(Obj(new{id=fileId,messageId,name="manual.dat",type="image",status="encrypted",size=4L,available=true}),"source-private","manual.dat",Hash(dat),"");
        var reference=wx.CreateReference(conversation);var result=await wx.HandleAsync("wechat.archive.create",new JsonObject{["reference"]=reference.DeepClone(),["scopeType"]="student",["scopeId"]="member"});
        string archivedId=S(result,"messageId");var before=app.Snapshot()["messages"]![0]!.DeepClone();string text=S(before,"text");
        var textAttachment=before["attachments"]!.AsArray().OfType<JsonObject>().Single(a=>S(a,"name").EndsWith(".txt"));string textPath=app.ResolveAttachment(S(textAttachment,"id"))!,textHash=Hash(textPath);
        string materialPath=Directory.EnumerateFiles(Path.Combine(module,"archive-material"),"*.txt").Single();File.SetLastWriteTimeUtc(materialPath,DateTime.UtcNow.AddDays(-1));DateTime materialStamp=File.GetLastWriteTimeUtc(materialPath);
        string png=Path.Combine(module,"files","manual.png");using(var bitmap=new Bitmap(16,12)){using var graphics=Graphics.FromImage(bitmap);graphics.Clear(Color.ForestGreen);bitmap.Save(png,ImageFormat.Png);}
        wx.Store.UpsertFile(Obj(new{id=fileId,messageId,name="manual.png",type="image",status="ready",mime="image/png",decodeStatus="decoded",size=new FileInfo(png).Length,originalStoredName="manual.dat",originalName="manual.dat",originalHash=Hash(dat),available=true}),"source-private","manual.png",Hash(png),"");
        await wx.BackfillReferencedImagesAsync(CancellationToken.None);var after=app.Snapshot()["messages"]![0]!;string snapshot=after.ToJsonString();int firstImports=imports;
        await wx.BackfillReferencedImagesAsync(CancellationToken.None);
        bool kept=app.Snapshot()["messages"]!.AsArray().Count==1&&S(after,"id")==archivedId&&S(after,"text")==text&&after["attachments"]!.AsArray().Count==3&&after["attachments"]!.AsArray().Any(a=>S(a,"mime")=="image/png")&&Hash(textPath)==textHash&&File.GetLastWriteTimeUtc(materialPath)==materialStamp&&imports==firstImports&&app.Snapshot()["messages"]![0]!.ToJsonString()==snapshot&&S(app.Snapshot()["students"]![0],"currentSummary")=="保留进展";
        await app.HandleAsync("chat.delete",Obj(new{id=archivedId}));await wx.BackfillReferencedImagesAsync(CancellationToken.None);
        if(!kept||imports!=2||app.Snapshot()["messages"]!.AsArray().Count!=0)throw new InvalidOperationException("manual_archive_image_backfill_without_binding_is_idempotent_and_preserves_text_and_deletion");
        checks.Add("manual_archive_image_backfill_without_binding_is_idempotent_and_preserves_text_and_deletion");
    }

    static async Task RunAsync(string directory,List<string> checks)
    {
        void Check(bool value,string label){if(!value)throw new InvalidOperationException(label);checks.Add(label);}
        string actual=Path.Combine(directory,"data"),module=Path.Combine(actual,"wechat"),source=Path.Combine(directory,"private-reader-source");Directory.CreateDirectory(module);Directory.CreateDirectory(source);
        File.WriteAllText(Path.Combine(actual,"initialized"),"isolated-image-check");
        File.WriteAllText(Path.Combine(actual,"state.json"),new JsonObject
        {
            ["schemaVersion"]=1,["students"]=new JsonArray(Obj(new{id="member",name="当前成员",grade="",tags=Array.Empty<string>(),bio="",currentSummary="保留手工进展",summaryStatus="idle",summaryError=""})),
            ["projects"]=new JsonArray(),["tasks"]=new JsonArray(),["messages"]=new JsonArray(),["alarms"]=new JsonArray(),["settings"]=Obj(new{currentStudentId="member",baseUrl="http://127.0.0.1:1/v1",model="",apiMode="responses"}),["status"]=new JsonObject()
        }.ToJsonString());
        File.WriteAllText(Path.Combine(module,"config.json"),Obj(new{enabled=false,checkReminders=false,checkMinutes=60,archiveFrom="all",selectedConversationIds=Array.Empty<string>(),bindings=Array.Empty<object>()}).ToJsonString());
        using var app=new AppService(actual);
        using var wx=new WeChatService(actual,Path.Combine(directory,"absent-runtime"),app.Snapshot,app.ImportWeChatArchive);
        string account="fixture",conv=WeChatStore.Stable(account,"friend");wx.Store.SaveConversation(Obj(new{id=conv,accountId=account,externalId="friend",name="独立微信备注",kind="friend"}));
        wx.SaveConfiguration(Obj(new{enabled=false,checkReminders=false,archiveFrom="all",selectedConversationIds=new[]{conv},bindings=new[]{new{conversationId=conv,memberId="member",projectId=""}}}));
        JsonObject Message(string id)=>Obj(new{id,accountId=account,conversationId=conv,senderId="friend",senderName="发送人",isSelf=false,kind="image",text="[图片]",rawContent="<image source='original' />",when=DateTimeOffset.Now.AddMinutes(-1).ToString("O")});
        var message=Message("image-message");wx.Store.UpsertMessage(message);
        string originalPath=Path.Combine(source,"photo.dat");File.WriteAllBytes(originalPath,Enumerable.Range(0,512).Select(i=>(byte)(i*31)).ToArray());
        string storedOriginal="legacy-photo.dat",fileId=WeChatStore.Stable("image-message","photo.dat","0");File.Copy(originalPath,Path.Combine(module,"files",storedOriginal));
        wx.Store.UpsertFile(Obj(new{id=fileId,messageId="image-message",name="photo.dat",size=512L,type="image",status="encrypted",available=true}),originalPath,storedOriginal,Hash(originalPath),"");
        async Task Archive()=>await (Task)typeof(WeChatService).GetMethod("ArchiveFilesAsync",BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(wx,new object[]{CancellationToken.None})!;
        JsonArray Archived()=>app.Snapshot()["messages"]!.AsArray();
        await Archive();string archiveId=S(Archived().Single(),"id");
        Check(Archived().Count==1&&Archived()[0]!["attachments"]!.AsArray().Count==1,"legacy_encrypted_image_has_one_archive_entry");
        Check(wx.ResolvePreviewImage(fileId)==null,"encrypted_bytes_are_not_exposed_as_image_preview");
        string png=Path.Combine(source,"photo.png");using(var bitmap=new Bitmap(48,32)){using var graphics=Graphics.FromImage(bitmap);graphics.Clear(Color.CornflowerBlue);bitmap.Save(png,ImageFormat.Png);}
        var raw=Obj(new{attachments=new[]{new{id="reader-image-id",name="photo.png",path=png,size=new FileInfo(png).Length,type="image",status="ready",decodeStatus="decoded",mime="image/png",originalPath,originalName="photo.dat",originalSize=512L}}});
        await wx.ImportFilesAsync(raw,message,CancellationToken.None);
        var imported=wx.Store.Message("image-message",true)!["attachments"]!.AsArray();var ready=imported.Single()!.AsObject();
        Check(imported.Count==1&&S(ready,"id")==fileId&&S(ready,"status")=="ready"&&S(ready,"mime")=="image/png","legacy_dat_upgrades_in_place_with_stable_attachment_id_and_mime");
        Check(S(ready,"originalStoredName")==storedOriginal&&Hash(wx.StoredPath(S(ready,"originalStoredName")))==Hash(originalPath),"upgrade_preserves_encrypted_original_bytes");
        var preview=wx.ResolvePreviewImage(fileId);using(var image=Image.FromFile(preview!.Value.Path))Check(preview.Value.Mime=="image/png"&&image.Width==48&&image.Height==32,"preview_resolves_a_real_decodable_image");
        string readySnapshot=ready.ToJsonString();await wx.ImportFilesAsync(raw,message,CancellationToken.None);
        Check(wx.Store.Message("image-message",true)!["attachments"]![0]!.ToJsonString()==readySnapshot,"repeated_ready_import_is_idempotent");
        await wx.ImportFilesAsync(Obj(new{attachments=new[]{new{id="reader-image-id",name="photo.dat",path=originalPath,size=512L,type="image",status="encrypted",decodeStatus="failed"}}}),message,CancellationToken.None);
        Check(wx.Store.Message("image-message",true)!["attachments"]![0]!.ToJsonString()==readySnapshot,"temporary_image_key_failure_does_not_downgrade_decoded_copy");
        await Archive();var business=Archived().Single()!.AsObject();var attachments=business["attachments"]!.AsArray();var imageAttachment=attachments.OfType<JsonObject>().Single(a=>S(a,"mime")=="image/png");
        Check(S(business,"id")==archiveId&&attachments.Count==2&&Hash(app.ResolveAttachment(S(imageAttachment,"id"))!)==Hash(png),"late_decode_adds_png_to_same_archive_and_retains_dat");
        Check(S(app.Snapshot()["students"]![0],"currentSummary")=="保留手工进展","image_backfill_preserves_manually_maintained_progress");
        string archiveSnapshot=Archived().ToJsonString();await Archive();
        Check(Archived().ToJsonString()==archiveSnapshot,"revision_receipt_prevents_repeat_image_archive_updates");

        var unavailable=Message("pending-image-message");wx.Store.UpsertMessage(unavailable);
        string pendingPath=Path.Combine(module,"files","pending.dat");File.WriteAllBytes(pendingPath,new byte[]{1,4,2,8,3,12});
        wx.Store.UpsertFile(Obj(new{id="pending-image",messageId="pending-image-message",name="pending.dat",size=6L,type="image",status="encrypted",decodeStatus="failed",available=true}),"private-source-location", "pending.dat",Hash(pendingPath),"");
        var reference=wx.CreateReference(conv);string workspace=Path.Combine(directory,"native-materials");
        var manifest=await wx.WriteReferenceWorkspaceAsync(new JsonArray(reference.DeepClone()),workspace,CancellationToken.None);
        string index=Directory.EnumerateFiles(Path.Combine(workspace,"wechat"),"*.jsonl").Single();string jsonl=File.ReadAllText(index);var rows=File.ReadAllLines(index).Select(line=>JsonNode.Parse(line)!.AsObject()).ToArray();
        var imageFile=rows.Single(row=>S(row,"id")=="image-message")["attachments"]![0]!;var pendingFile=rows.Single(row=>S(row,"id")=="pending-image-message")["attachments"]![0]!;
        string MaterialPath(JsonNode f,string key)=>Path.Combine(workspace,S(f,key).Replace('/',Path.DirectorySeparatorChar));
        Check(manifest["imageFileCount"]!.GetValue<int>()==1&&S(imageFile,"mime")=="image/png"&&Hash(MaterialPath(imageFile,"file"))==Hash(png),"native_gpt_workspace_contains_full_readable_image");
        Check(File.Exists(MaterialPath(imageFile,"originalFile"))&&Hash(MaterialPath(imageFile,"originalFile"))==Hash(originalPath),"native_materials_keep_encrypted_source_separate_from_readable_image");
        Check(pendingFile["file"]==null&&File.Exists(MaterialPath(pendingFile,"originalFile"))&&S(pendingFile,"readStatus").Contains("尚未成功解码"),"undecoded_image_is_explicit_and_not_misrepresented_as_readable");
        Check(!jsonl.Contains("private-reader-source")&&!jsonl.Contains("sourcePath")&&!jsonl.Contains("storedName")&&!jsonl.Contains("originalStoredName")&&jsonl.Contains("rawContent"),"native_materials_keep_source_content_without_host_file_paths");
        Check(wx.AnalysisMessages(DateTimeOffset.Now.AddMinutes(-2),false).OfType<JsonObject>().Any(m=>S(m,"id")=="image-message"),"updated_image_message_is_available_to_reminder_analysis_batch");
        var selection=Obj(new{references=new[]{new{scopeType="student",scopeId="member"}}});string businessWorkspace=Path.Combine(directory,"business-materials");
        await app.WriteBusinessReferenceMaterialsAsync(selection,businessWorkspace,CancellationToken.None);
        var businessIndex=JsonNode.Parse(File.ReadAllText(Path.Combine(businessWorkspace,"archives","0001","archive.json")))!;
        var businessImage=businessIndex["messages"]![0]!["attachments"]!.AsArray().OfType<JsonObject>().Single(f=>S(f,"mime")=="image/png");
        Check(File.Exists(Path.Combine(businessWorkspace,"archives","0001",S(businessImage,"file")))&&S(businessImage,"textStatus").Contains("可直接查看"),"archive_summary_workspace_exposes_image_for_native_vision");
        await app.HandleAsync("chat.delete",Obj(new{id=archiveId}));
        var deletedResult=app.ImportWeChatArchive(Obj(new{requestId="wechat-file-"+WeChatStore.Stable(fileId,"student","member"),scopeType="student",scopeId="member",refreshFiles=true,text="自动补图",files=new[]{new{id=fileId,path=wx.StoredPath(S(ready,"storedName")),name="photo.png",mime="image/png"}}}));
        Check(deletedResult["replayed"]!.GetValue<bool>()&&Archived().Count==0,"image_refresh_never_resurrects_deleted_archive");
    }
}
