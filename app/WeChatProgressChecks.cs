using System.Reflection;
using System.Text.Json.Nodes;
using StudentProgress.WeChat;

namespace StudentProgress;

// Isolated settings, index accounting and notification checks. No source WeChat, GPT or ONNX calls.
internal static class WeChatProgressChecks
{
    internal static async Task<JsonObject> RunAsync(string directory,string appRoot)
    {
        if(Directory.Exists(directory)&&Directory.EnumerateFileSystemEntries(directory).Any())throw new InvalidOperationException("索引进度检查需要空目录。");
        Directory.CreateDirectory(directory);var checks=new JsonArray();void Check(bool ok,string label){if(!ok)throw new InvalidOperationException(label);checks.Add(label);}
        var legacy=new JsonObject{["enabled"]=false,["checkReminders"]=false,["checkMinutes"]=180,["archiveFrom"]="all",["selectedConversationIds"]=new JsonArray(),["bindings"]=new JsonArray()};string old=legacy.ToJsonString();var upgraded=WeChatService.UpgradeConfiguration(legacy);
        Check(upgraded["syncMinutes"]!.GetValue<int>()==1&&upgraded["checkMinutes"]!.GetValue<int>()==180&&legacy.ToJsonString()==old,"legacy_sync_interval_defaults_to_one_without_changing_gpt_check_frequency");
        string data=Path.Combine(directory,"data"),module=Path.Combine(data,"wechat");Directory.CreateDirectory(module);string settings=Path.Combine(module,"config.json");File.WriteAllText(settings,old);
        using var service=new WeChatService(data,Path.Combine(directory,"absent-model-and-reader"),()=>new JsonObject{["students"]=new JsonArray(),["projects"]=new JsonArray()},_=>throw new InvalidOperationException("no archive write"));
        Check(service.Configuration["syncMinutes"]!.GetValue<int>()==1&&File.ReadAllText(Path.Combine(module,"config.pre-v15.json"))==old,"migration_persists_interval_and_preserves_previous_configuration");
        service.SaveConfiguration(new(){["syncMinutes"]=17});Check(service.Configuration["syncMinutes"]!.GetValue<int>()==17&&service.Configuration["checkMinutes"]!.GetValue<int>()==180,"custom_sync_interval_saves_independently");
        foreach(JsonNode value in new JsonNode[]{JsonValue.Create(0)!,JsonValue.Create(1441)!,JsonValue.Create(1.5)!,JsonValue.Create("5")!})
        {
            var before=service.Configuration.ToJsonString();bool rejected=false;try{service.SaveConfiguration(new(){["syncMinutes"]=value.DeepClone()});}catch(InvalidDataException){rejected=true;}Check(rejected&&before==service.Configuration.ToJsonString(),"invalid_interval_does_not_partially_save_"+checks.Count);
        }
        var empty=service.ProgressSnapshot();Check(empty["totalChunks"]!.GetValue<long>()==0&&empty["percent"]==null,"empty_library_has_no_fake_percentage");
        service.Store.SaveConversation(new(){["id"]="conversation",["accountId"]="test",["externalId"]="friend",["name"]="合成项目群",["kind"]="group"});
        JsonObject Message(string text)=>new(){["id"]="message",["conversationId"]="conversation",["senderId"]="sender",["senderName"]="成员",["when"]=DateTimeOffset.Now.ToString("O"),["kind"]="text",["text"]=text};
        service.Store.UpsertMessage(Message(new string('段',1500)));service.Store.UpsertFile(new(){["id"]="file",["messageId"]="message",["name"]="合成附件.txt",["size"]=0L,["type"]="file"},"","","","附件可提取文字");
        var pending=service.Store.PendingIndexChunks(20);Check(pending.Count>=4&&pending.All(p=>p.ConversationName=="合成项目群")&&pending.Any(p=>p.FileName=="合成附件.txt"),"pending_chunks_include_message_and_file_sources");
        var vector=new float[WeChatEmbedding.Dimensions];vector[0]=1;
        Check(service.Store.TrySetVector(pending[0].Id,vector)&&!service.Store.TrySetVector(pending[0].Id,vector),"vector_completion_is_counted_once");
        var progress=service.ProgressSnapshot();long total=progress["totalChunks"]!.GetValue<long>();Check(progress["indexedChunks"]!.GetValue<long>()==1&&progress["pendingChunks"]!.GetValue<long>()==total-1&&Math.Abs(progress["percent"]!.GetValue<double>()-Math.Floor(1000.0/total)/10.0)<.001,"percentage_uses_all_imported_chunks_as_denominator");
        var beforeSnapshot=service.Store.VectorSnapshot();long oldPending=pending.First(p=>p.Id!=pending[0].Id&&p.FileName.Length==0).Id;service.Store.UpsertMessage(Message("同步更新后的正文"));
        Check(!service.Store.TrySetVector(oldPending,vector)&&!service.Store.MarkIndexSaved(beforeSnapshot.Revision,true)&&service.Store.Meta("vectorDirty")=="1","concurrent_replacement_cannot_save_stale_vectors_or_acknowledge_stale_index");
        var afterSnapshot=service.Store.VectorSnapshot();Check(service.Store.MarkIndexSaved(afterSnapshot.Revision,true)&&service.Store.Meta("vectorDirty")=="0","current_vector_snapshot_can_acknowledge_saved_revision");
        int events=0;service.ProgressChanged+=()=>events++;
        var update=typeof(WeChatService).GetMethod("UpdateIndexProgress",BindingFlags.Instance|BindingFlags.NonPublic)!;
        void Publish(string phase,bool running,string error="",bool force=false)=>update.Invoke(service,new object[]{phase,running,"conversation","合成项目群","合成附件.txt",error,force});
        Publish("indexing",true,force:true);int first=events;Publish("indexing",true);Publish("indexing",true);Check(events==first,"quantity_updates_are_throttled_within_one_second");
        Publish("saving",true);Check(events==first+1&&service.ProgressSnapshot()["phase"]!.GetValue<string>()=="saving","phase_changes_publish_immediately");
        Publish("error",false,"合成失败",true);Check(service.ProgressSnapshot()["error"]!.GetValue<string>()=="合成失败","index_error_is_visible");Publish("preparing_index",true);Check(service.ProgressSnapshot()["error"]!.GetValue<string>()=="","new_attempt_clears_stale_index_error");
        Publish("unavailable",false);Check(service.ProgressSnapshot()["phase"]!.GetValue<string>()=="unavailable"&&!service.ProgressSnapshot()["modelAvailable"]!.GetValue<bool>(),"missing_model_reports_keyword_fallback_without_invoking_model");
        var reply=await service.HandleAsync("wechat.progress",new());Check(reply["progress"] is JsonObject&&service.Snapshot()["indexProgress"] is JsonObject,"lightweight_and_full_snapshot_share_progress_contract");
        await service.PauseForRestoreAsync();try{Check(service.ProgressSnapshot()["phase"]!.GetValue<string>()=="paused","restore_pause_is_reported");}finally{service.ResumeAfterRestore();}
        Check(service.Configuration["syncMinutes"]!.GetValue<int>()==17,"restore_resume_reloads_custom_interval");
        return new(){["ok"]=true,["checks"]=checks,["count"]=checks.Count,["modelCalls"]=0,["embeddingCalls"]=0,["liveWechatReads"]=0};
    }
}
