using System.Text.Json.Nodes;

namespace StudentProgress.WeChat;

internal sealed partial class WeChatStore
{
    internal readonly record struct IndexStatistics(long Total,long Indexed,long Pending,long Failed,long SkippedVoice);
    internal IndexStatistics IndexCounts()
    {
        lock(gate)
        {
            using var command=Command("SELECT (SELECT COUNT(*) FROM chunks),(SELECT COUNT(*) FROM chunks WHERE vector IS NULL),(SELECT COUNT(*) FROM chunks c JOIN chunk_failures f ON f.chunkId=c.id WHERE c.vector IS NULL),(SELECT COUNT(*) FROM messages WHERE kind='voice' AND (COALESCE(json_extract(payload,'$.voiceTranscriptStatus'),'')<>'completed' OR length(trim(COALESCE(json_extract(payload,'$.voiceTranscript'),'')))=0))");
            using var reader=command.ExecuteReader();reader.Read();long total=reader.GetInt64(0),unindexed=reader.GetInt64(1),failed=reader.GetInt64(2);return new(total,total-unindexed,unindexed-failed,failed,reader.GetInt64(3));
        }
    }
    internal sealed record IndexChunk(long Id,string Text,string ConversationId,string ConversationName,string FileName);
    internal List<IndexChunk> PendingIndexChunks(int limit=24)
    {
        lock(gate)
        {
            using var command=Command("SELECT c.id,c.text,m.conversationId,COALESCE(v.name,''),COALESCE(f.name,'') FROM chunks c JOIN messages m ON m.id=c.messageId LEFT JOIN conversations v ON v.id=m.conversationId LEFT JOIN files f ON f.id=c.fileId WHERE c.vector IS NULL AND NOT EXISTS(SELECT 1 FROM chunk_failures failure WHERE failure.chunkId=c.id) ORDER BY c.id LIMIT $limit",("$limit",limit));
            using var reader=command.ExecuteReader();var result=new List<IndexChunk>();while(reader.Read())result.Add(new(reader.GetInt64(0),reader.GetString(1),reader.GetString(2),reader.GetString(3),reader.GetString(4)));return result;
        }
    }
    internal bool TrySetVector(long id,float[] vector)
    {
        lock(gate)
        {
            var bytes=new byte[vector.Length*4];Buffer.BlockCopy(vector,0,bytes,0,bytes.Length);using var update=Command("UPDATE chunks SET vector=$vector WHERE id=$id AND vector IS NULL",("$vector",bytes),("$id",id));if(update.ExecuteNonQuery()!=1)return false;
            Run("DELETE FROM chunk_failures WHERE chunkId=$id",("$id",id));Run("INSERT INTO meta(key,value) VALUES('vectorRevision','1') ON CONFLICT(key) DO UPDATE SET value=CAST(value AS INTEGER)+1");return true;
        }
    }
    internal void FailIndexChunk(long id,string error)
    {
        lock(gate)
        {
            // A replaced source gets a new chunk ID and must never inherit the old failure.
            Run("INSERT INTO chunk_failures(chunkId,error,failedAt,attempts) SELECT id,$error,$at,1 FROM chunks WHERE id=$id AND vector IS NULL ON CONFLICT(chunkId) DO UPDATE SET error=excluded.error,failedAt=excluded.failedAt,attempts=attempts+1",("$id",id),("$error",error[..Math.Min(error.Length,800)]),("$at",DateTimeOffset.Now.ToString("O")));
        }
    }
    internal void RetryIndexFailures()
    {lock(gate){Run("DELETE FROM chunk_failures");SetMeta("indexGlobalFailure","");}}
    internal (string Revision,IEnumerable<(long Id,float[] Vector)> Vectors) VectorSnapshot()
    {lock(gate)return (Meta("vectorRevision","0"),AllVectors());}
    internal bool MarkIndexSaved(string revision,bool clearDirty)
    {
        lock(gate)
        {
            if(Meta("vectorRevision","0")!=revision||!clearDirty&&Meta("vectorDirty")=="1")return false;
            SetMeta("indexRevision",revision);if(clearDirty)SetMeta("vectorDirty","0");return true;
        }
    }
    static string CompletedVoiceText(JsonObject? message)=>S(message,"voiceTranscriptStatus")=="completed"?S(message,"voiceTranscript").Trim():"";
    string MessageIndexText(JsonObject message)
    {
        string text=S(message,"kind")=="voice"?CompletedVoiceText(message):S(message,"text");
        if(string.IsNullOrWhiteSpace(text))return "";return S(Conversation(S(message,"conversationId")),"name")+" · "+S(message,"senderName")+"\n"+text;
    }
    void EnsureIndexPolicy()
    {
        lock(gate)
        {
            Run("CREATE TABLE IF NOT EXISTS chunk_failures(chunkId INTEGER PRIMARY KEY,error TEXT NOT NULL,failedAt TEXT NOT NULL,attempts INTEGER NOT NULL DEFAULT 1)");
            Run("CREATE INDEX IF NOT EXISTS ix_chunks_pending ON chunks(id) WHERE vector IS NULL");
            if(Meta("voiceIndexPolicyVersion")=="1")return;
            var old=new List<(long Id,string Text)>();using(var read=Command("SELECT c.id,c.text FROM chunks c JOIN messages m ON m.id=c.messageId WHERE m.kind='voice'")){using var reader=read.ExecuteReader();while(reader.Read())old.Add((reader.GetInt64(0),reader.GetString(1)));}
            if(old.Count>0)
            {
                var recovery=Path.Combine(Path.GetDirectoryName(databasePath)!,"recovery","index-policy-v16-"+DateTime.UtcNow.ToString("yyyyMMdd-HHmmss")+"-"+Guid.NewGuid().ToString("N")[..6]);Directory.CreateDirectory(recovery);BackupTo(Path.Combine(recovery,"records.sqlite"));
            }
            var voices=new List<JsonObject>();using(var command=Command("SELECT payload FROM messages WHERE kind='voice'")){using var reader=command.ExecuteReader();while(reader.Read())voices.Add(Parse(reader.GetString(0)));}
            using var transaction=db.BeginTransaction();
            foreach(var (id,text) in old)DeleteIndexChunk(id,text);
            foreach(var message in voices)InsertChunks(S(message,"id"),"",MessageIndexText(message));
            if(old.Count>0||voices.Any(v=>CompletedVoiceText(v).Length>0))TouchVectorRevision();
            SetMeta("voiceIndexPolicyVersion","1");transaction.Commit();
        }
    }
    void DeleteIndexChunk(long id,string text)
    {
        Run("INSERT INTO chunk_fts(chunk_fts,rowid,tokens) VALUES('delete',$id,$text)",("$id",id),("$text",Tokens(text)));
        Run("DELETE FROM chunk_failures WHERE chunkId=$id",("$id",id));Run("DELETE FROM chunks WHERE id=$id",("$id",id));
    }
    void InsertChunks(string messageId,string fileId,string text)
    {
        int ordinal=0;foreach(var part in Split(text)){Run("INSERT INTO chunks(messageId,fileId,ordinal,text) VALUES($m,$f,$n,$text)",("$m",messageId),("$f",fileId),("$n",ordinal++),("$text",part));Run("INSERT INTO chunk_fts(rowid,tokens) VALUES(last_insert_rowid(),$text)",("$text",Tokens(part)));}
    }
}
