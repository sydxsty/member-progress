using System.Text.Json.Nodes;

namespace StudentProgress.WeChat;

internal sealed partial class WeChatStore
{
    internal (long Total,long Indexed) IndexCounts()
    {
        lock(gate)
        {
            using var command=Command("SELECT (SELECT COUNT(*) FROM chunks),(SELECT COUNT(*) FROM chunks WHERE vector IS NULL)");using var reader=command.ExecuteReader();reader.Read();long total=reader.GetInt64(0);return (total,total-reader.GetInt64(1));
        }
    }
    internal sealed record IndexChunk(long Id,string Text,string ConversationId,string ConversationName,string FileName);
    internal List<IndexChunk> PendingIndexChunks(int limit=24)
    {
        lock(gate)
        {
            using var command=Command("SELECT c.id,c.text,m.conversationId,COALESCE(v.name,''),COALESCE(f.name,'') FROM chunks c JOIN messages m ON m.id=c.messageId LEFT JOIN conversations v ON v.id=m.conversationId LEFT JOIN files f ON f.id=c.fileId WHERE c.vector IS NULL ORDER BY c.id LIMIT $limit",("$limit",limit));
            using var reader=command.ExecuteReader();var result=new List<IndexChunk>();while(reader.Read())result.Add(new(reader.GetInt64(0),reader.GetString(1),reader.GetString(2),reader.GetString(3),reader.GetString(4)));return result;
        }
    }
    internal bool TrySetVector(long id,float[] vector)
    {
        lock(gate)
        {
            var bytes=new byte[vector.Length*4];Buffer.BlockCopy(vector,0,bytes,0,bytes.Length);using var update=Command("UPDATE chunks SET vector=$vector WHERE id=$id AND vector IS NULL",("$vector",bytes),("$id",id));if(update.ExecuteNonQuery()!=1)return false;
            Run("INSERT INTO meta(key,value) VALUES('vectorRevision','1') ON CONFLICT(key) DO UPDATE SET value=CAST(value AS INTEGER)+1");return true;
        }
    }
    internal (string Revision,IEnumerable<(long Id,float[] Vector)> Vectors) VectorSnapshot()
    {lock(gate)return (Meta("vectorRevision","0"),AllVectors());}
    // A sync can replace chunks while the vector cache is being saved. Never mark that newer
    // database revision indexed by an older snapshot or hide a pending rebuild.
    internal bool MarkIndexSaved(string revision,bool clearDirty)
    {
        lock(gate)
        {
            if(Meta("vectorRevision","0")!=revision||!clearDirty&&Meta("vectorDirty")=="1")return false;
            SetMeta("indexRevision",revision);if(clearDirty)SetMeta("vectorDirty","0");return true;
        }
    }
}
