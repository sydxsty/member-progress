using Microsoft.Data.Sqlite;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using StudentProgress.WeChat;

namespace StudentProgress;

internal static class WeChatIndexRevisionChecks
{
    internal static async Task<JsonObject> RunAsync(string directory,string baseDirectory)
    {
        if(Directory.Exists(directory)&&Directory.EnumerateFileSystemEntries(directory).Any())throw new InvalidOperationException("索引修复检查需要空目录。");
        Directory.CreateDirectory(directory);var checks=new JsonArray();void Check(bool value,string name){if(!value)throw new InvalidOperationException(name);checks.Add(name);}
        string data=Path.Combine(directory,"data"),module=Path.Combine(data,"wechat"),workspace=Path.Combine(directory,"work");Directory.CreateDirectory(module);Directory.CreateDirectory(Path.Combine(module,"files"));
        File.WriteAllText(Path.Combine(module,"config.json"),"{\"enabled\":false,\"checkReminders\":false,\"syncMinutes\":5,\"reminderLookbackHours\":24,\"archiveFrom\":\"all\",\"selectedConversationIds\":[],\"bindings\":[]}");
        var now=DateTimeOffset.Now;JsonObject Message(string id,string text,string kind="text")=>new(){["id"]=id,["conversationId"]="c",["senderId"]="sender",["senderName"]="合成发送人",["isSelf"]=false,["when"]=now.ToString("O"),["kind"]=kind,["text"]=text,["rawContent"]="<msg/>"};
        var voice=Message("voice","[语音]","voice");long keptId;var vector=new float[WeChatEmbedding.Dimensions];vector[0]=1;
        string legacy=Path.Combine(directory,"legacy.sqlite");
        using(var store=new WeChatStore(module))
        {
            store.SaveConversation(new(){["id"]="c",["accountId"]="synthetic",["externalId"]="friend",["name"]="合成会话",["kind"]="friend"});store.UpsertMessage(Message("normal","保留的普通消息"));keptId=store.PendingIndexChunks().Single().Id;store.TrySetVector(keptId,vector);
            store.UpsertMessage(voice);File.WriteAllBytes(Path.Combine(module,"files","voice.silk"),[1,2,3]);store.UpsertFile(new(){["id"]="voice-file",["messageId"]="voice",["name"]="语音.silk",["type"]="voice",["size"]=3L},"synthetic","voice.silk","voice-hash","");
            // Recreate just the old placeholder records to exercise a real targeted upgrade.
            var bytes=new byte[vector.Length*4];Buffer.BlockCopy(vector,0,bytes,0,bytes.Length);
            foreach(var (id,legacyFileId) in new[]{(50001L,""),(50002L,"voice-file")})
            {store.Run("INSERT INTO chunks(id,messageId,fileId,ordinal,text,vector) VALUES($id,'voice',$file,0,'legacyvoice',$vector)",("$id",id),("$file",legacyFileId),("$vector",bytes));store.Run("INSERT INTO chunk_fts(rowid,tokens) VALUES($id,'legacyvoice ')",("$id",id));}
            store.SetMeta("voiceIndexPolicyVersion","");store.BackupTo(legacy);store.Reload();
            Check(store.IndexCounts().Total==1&&store.IndexCounts().Indexed==1&&store.IndexCounts().SkippedVoice==1&&store.Chunk(keptId)!=null,"legacy_voice_only_migration_keeps_other_vectors");
            Check(store.Chunk(50001)==null&&store.Chunk(50002)==null&&store.KeywordCandidates("legacyvoice").Count==0,"legacy_voice_chunks_and_fts_removed_together");
            Check(File.Exists(Path.Combine(module,"files","voice.silk"))&&store.Message("voice")!=null&&Directory.EnumerateFiles(Path.Combine(module,"recovery"),"records.sqlite",SearchOption.AllDirectories).Any(),"migration_preserves_audio_messages_and_database_recovery_copy");
            Check(store.Meta("vectorDirty")=="1","migration_marks_vector_cache_for_rebuild_from_saved_vectors_only");
            store.Reload();Check(store.IndexCounts().Total==1&&store.IndexCounts().Indexed==1,"voice_migration_is_idempotent");
            store.ReplaceFrom(legacy,Path.Combine(directory,"before-restore.sqlite"));Check(store.Chunk(50001)==null&&store.IndexCounts().Total==1,"bulk_restore_also_migrates_legacy_voice_chunks");
        }
        using var service=new WeChatService(data,Path.Combine(directory,"no-runtime"),()=>new JsonObject{["students"]=new JsonArray(),["projects"]=new JsonArray()},_=>throw new InvalidOperationException("no archive import"));var db=service.Store;
        voice["voiceTranscript"]="明天讨论一个合成事项";voice["voiceTranscriptSource"]="wechat_local";voice["voiceTranscriptStatus"]="completed";voice["text"]=voice["voiceTranscript"]!.DeepClone();db.UpsertMessage(voice);
        var transcript=db.PendingIndexChunks().Single();Check(transcript.ConversationName=="合成会话"&&transcript.FileName.Length==0&&db.IndexCounts().Total==2&&db.IndexCounts().SkippedVoice==0,"completed_transcript_adds_only_one_message_chunk");db.TrySetVector(transcript.Id,vector);
        var read=await service.ReadAssistantDataAsync("wechat.message",new(){["id"]="voice"},workspace,CancellationToken.None);Check(read["message"]!["voiceTranscript"]!.GetValue<string>()=="明天讨论一个合成事项"&&read["message"]!["voiceTranscriptSource"]!.GetValue<string>()=="wechat_local","native_message_read_preserves_transcript_and_source");
        db.UpsertMessage(Message("unknown","[语音]","voice"));Check(db.IndexCounts().SkippedVoice==1&&db.IndexCounts().Total==2,"voice_without_transcript_is_kept_but_not_indexed");
        string document=Path.Combine(directory,"unchanged.pdf");File.WriteAllBytes(document,[23,42,17,9]);string hash=Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(document))).ToLowerInvariant();string stored=hash+".pdf";File.Copy(document,Path.Combine(module,"files",stored));db.UpsertMessage(Message("document","合成文件","file"));
        var file=new JsonObject{["id"]="document-file",["messageId"]="document",["readerId"]="reader-file",["name"]="unchanged.pdf",["size"]=4L,["type"]="file",["status"]="stored",["sourceWriteTicks"]=0L,["sourceSize"]=4L};db.UpsertFile(file,document,stored,hash,"CACHED_EVIDENCE");
        foreach(var p in db.PendingIndexChunks(100))db.TrySetVector(p.Id,vector);var before=db.AllVectors().Select(x=>x.Id).ToArray();string revision=db.Meta("vectorRevision");
        await service.ImportFilesAsync(new(){["attachments"]=new JsonArray(new JsonObject{["id"]="reader-file",["name"]="unchanged.pdf",["path"]=document,["size"]=4L,["type"]="file",["status"]="stored"})},db.Message("document")!,CancellationToken.None);
        Check(db.AllVectors().Select(x=>x.Id).SequenceEqual(before)&&db.Meta("vectorRevision")==revision&&WeChatService.S(db.File("document-file",true),"extractedText")=="CACHED_EVIDENCE","same_hash_reuses_extraction_and_metadata_refresh_preserves_vectors");
        db.UpsertMessage(Message("failure","需要失败隔离的合成文本"));var failed=db.PendingIndexChunks().Single();db.FailIndexChunk(failed.Id,"synthetic item failure");Check(db.IndexCounts().Failed==1&&db.PendingIndexChunks().Count==0,"failed_item_is_excluded_from_automatic_queue");
        db.Reload();Check(db.IndexCounts().Failed==1&&db.PendingIndexChunks().Count==0,"item_failure_survives_restart");
        var same=Message("failure","需要失败隔离的合成文本");same["source"]=new JsonObject{["cacheTimestamp"]="metadata-only"};db.UpsertMessage(same);Check(db.IndexCounts().Failed==1&&db.PendingIndexChunks().Count==0,"metadata_only_changes_do_not_retry_failed_text");
        db.UpsertMessage(Message("failure","改变后的合成文本"));Check(db.IndexCounts().Failed==0&&db.PendingIndexChunks().Count==1,"changed_text_requeues_only_changed_source");var newFailed=db.PendingIndexChunks().Single();db.FailIndexChunk(newFailed.Id,"synthetic retry failure");db.SetMeta("indexGlobalFailure","synthetic model failure");
        var progress=service.ProgressSnapshot();Check(progress["phase"]!.GetValue<string>()=="error"&&progress["failedChunks"]!.GetValue<long>()==1&&progress["skippedVoiceMessages"]!.GetValue<long>()==1&&progress["eligibleChunks"]!.GetValue<long>()==db.IndexCounts().Total,"progress_reports_eligible_failed_skipped_and_global_pause");
        db.RetryIndexFailures();Check(db.IndexCounts().Failed==0&&db.PendingIndexChunks().Count==1&&db.Meta("indexGlobalFailure")=="","manual_retry_requeues_failures_and_clears_global_pause");
        var corrected=Message("corrected","时间校正");corrected["when"]=now.AddDays(-3).ToString("O");db.UpsertMessage(corrected);corrected["when"]=now.ToString("O");corrected["senderId"]="corrected-sender";db.UpsertMessage(corrected);
        using(var verify=new SqliteConnection(new SqliteConnectionStringBuilder{DataSource=Path.Combine(module,"records.sqlite"),Mode=SqliteOpenMode.ReadOnly,Pooling=false}.ToString()))
        {verify.Open();using var command=verify.CreateCommand();command.CommandText="SELECT whenMs,senderId FROM messages WHERE id='corrected'";using var row=command.ExecuteReader();row.Read();Check(row.GetInt64(0)==now.ToUnixTimeMilliseconds()&&row.GetString(1)=="corrected-sender","source_time_and_sender_columns_follow_payload_corrections");}
        return new(){["ok"]=true,["count"]=checks.Count,["checks"]=checks,["modelCalls"]=0,["embeddingCalls"]=0,["liveWechatReads"]=0};
    }
}
