using System.IO.Compression;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace StudentProgress.Assistant;

internal static class AssistantAttachmentChecks
{
    internal static async Task<JsonObject> RunAsync(string directory)
    {
        if(Directory.Exists(directory)&&Directory.EnumerateFileSystemEntries(directory).Any())throw new InvalidOperationException("附件检查需要空目录。");
        Directory.CreateDirectory(directory);string data=Path.Combine(directory,"data"),work=Path.Combine(directory,"material");Directory.CreateDirectory(data);Directory.CreateDirectory(work);
        File.WriteAllText(Path.Combine(data,"initialized"),"synthetic-attachments");
        var checks=new JsonArray();void Check(bool value,string name){if(!value)throw new InvalidOperationException(name);checks.Add(name);}
        using var service=new AssistantService(data,Path.Combine(directory,"unused-runtime"),_=>new JsonObject(),()=>new JsonObject());
        var save=typeof(AssistantService).GetMethod("SaveAttachments",BindingFlags.Instance|BindingFlags.NonPublic)!;
        byte[] bytes=Encoding.UTF8.GetBytes("原始附件全文：文件本身的末尾 VERIFY-FILE-847");
        var payload=new JsonObject{["files"]=new JsonArray(new JsonObject{["name"]="会议资料.txt",["mime"]="text/plain",["dataBase64"]=Convert.ToBase64String(bytes)})};
        var files=(List<AssistantAttachment>)save.Invoke(service,new object[]{payload,"user-file"})!;
        var saved=files.Single();string original=Path.Combine(data,"assistant","files",saved.StoredName);
        Check(File.ReadAllBytes(original).SequenceEqual(bytes)&&saved.Size==bytes.Length&&saved.Sha256.Length==64,"uploaded_original_persisted_with_stable_metadata");
        var conversation=new AssistantState{Messages=[new AssistantMessage{Id="user-file",Text="",Attachments=files}]};
        string history=Path.Combine(data,"assistant","history.json");AssistantJson.WriteAtomic(history,conversation);service.Reload();
        var snapshot=service.Snapshot();
        Check(snapshot["messages"]![0]!["attachments"]!.AsArray().Count==1&&!snapshot.ToJsonString().Contains("storedName")&&snapshot["messages"]![0]!["text"]!.GetValue<string>()=="","file_only_message_persists_without_exposing_stored_path");
        var material=await service.ReadAttachmentAsync(new JsonObject{["attachmentId"]=saved.Id},work,CancellationToken.None);
        Check(material["readStatus"]!.GetValue<string>()=="ready"&&!Path.IsPathRooted(material["file"]!.GetValue<string>())&&File.ReadAllBytes(Path.Combine(work,material["file"]!.GetValue<string>())).SequenceEqual(bytes),"agent_reads_requested_original_in_isolated_workspace");
        await service.HandleAsync("assistant.clear",new JsonObject());bool hidden=false;
        try{await service.ReadAttachmentAsync(new JsonObject{["attachmentId"]=saved.Id},work,CancellationToken.None);}catch(InvalidOperationException){hidden=true;}
        Check(hidden&&File.Exists(original),"clearing_hides_attachments_without_destroying_undo_originals");
        await service.HandleAsync("assistant.undo-clear",new JsonObject());
        Check(service.ResolveChatAttachment(saved.Id)==original&&service.PrepareArchiveAttachmentFiles(new[]{saved.Id}).Count==1,"undo_restores_attachment_access_and_explicit_archive_descriptor");
        bool tooMany=false;
        try{save.Invoke(service,new object[]{new JsonObject{["files"]=new JsonArray(Enumerable.Range(0,21).Select(_=>(JsonNode)new JsonObject()).ToArray())},"over-limit"});}
        catch(TargetInvocationException e)when(e.InnerException is InvalidOperationException){tooMany=true;}
        Check(tooMany&&Directory.EnumerateFiles(Path.GetDirectoryName(original)!).Count()==1,"invalid_upload_rejected_without_partial_files");

        // The actual shared backup path must retain files referenced only by the undo buffer too.
        await service.HandleAsync("assistant.clear",new JsonObject());
        using var app=new AppService(data);app.RegisterBackupFiles(new[]{"assistant/history.json","assistant/settings.json"});
        string zip=Path.Combine(directory,"backup.zip");await app.HandleAsync("backup.export",new JsonObject{["path"]=zip});
        string stage=Path.Combine(directory,"inspect-backup");ZipFile.ExtractToDirectory(zip,stage);AssistantAttachmentBackup.Validate(stage);
        Check(File.ReadAllBytes(Path.Combine(stage,"assistant","files",saved.StoredName)).SequenceEqual(bytes)&&JsonNode.Parse(File.ReadAllText(Path.Combine(stage,"assistant","history.json")))!["clearedMessages"]!.AsArray().Count==1,"general_backup_preserves_undoable_chat_originals");
        string target=Path.Combine(directory,"restored");Directory.CreateDirectory(target);File.WriteAllText(Path.Combine(target,"initialized"),"synthetic-restore");
        using(var restore=new AppService(target))
        {
            restore.RegisterBackupFiles(new[]{"assistant/history.json","assistant/settings.json"});
            await restore.HandleAsync("backup.import",new JsonObject{["path"]=zip});
        }
        using(var restoredService=new AssistantService(target,Path.Combine(directory,"unused-runtime"),_=>new JsonObject(),()=>new JsonObject()))
        {
            await restoredService.HandleAsync("assistant.undo-clear",new JsonObject());
            string? restored=restoredService.ResolveChatAttachment(saved.Id);
            Check(restored!=null&&File.ReadAllBytes(restored).SequenceEqual(bytes),"general_restore_and_undo_recover_attachment_bytes");
        }
        File.WriteAllText(Path.Combine(stage,"assistant","files",saved.StoredName),"corrupted");bool invalid=false;
        try{AssistantAttachmentBackup.Validate(stage);}catch(InvalidDataException){invalid=true;}
        Check(invalid,"restore_checks_original_size_and_hash_before_mutating_live_data");
        return new JsonObject{["ok"]=true,["count"]=checks.Count,["checks"]=checks,["modelCalls"]=0,["liveDataReads"]=0};
    }
}
