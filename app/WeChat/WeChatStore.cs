using Microsoft.Data.Sqlite;
using System.Text;
using System.Text.Json.Nodes;
using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace StudentProgress.WeChat;

// This database contains only WeChat material; no host business state or credentials.
internal sealed class WeChatStore : IDisposable
{
    readonly object gate = new();
    readonly string databasePath;
    SqliteConnection db;
    internal WeChatStore(string root)
    {
        Directory.CreateDirectory(root); databasePath=Path.Combine(root,"records.sqlite");
        db=Open(databasePath);
        Run("""
        CREATE TABLE IF NOT EXISTS conversations(id TEXT PRIMARY KEY,accountId TEXT NOT NULL,externalId TEXT NOT NULL,name TEXT NOT NULL,kind TEXT NOT NULL,payload TEXT NOT NULL);
        CREATE TABLE IF NOT EXISTS messages(id TEXT PRIMARY KEY,conversationId TEXT NOT NULL,senderId TEXT NOT NULL,whenMs INTEGER NOT NULL,kind TEXT NOT NULL,text TEXT NOT NULL,payload TEXT NOT NULL,revision INTEGER NOT NULL DEFAULT 1,importedAt INTEGER NOT NULL);
        CREATE INDEX IF NOT EXISTS ix_messages_conversation ON messages(conversationId,whenMs,id);
        CREATE INDEX IF NOT EXISTS ix_messages_time ON messages(whenMs);
        CREATE TABLE IF NOT EXISTS files(id TEXT PRIMARY KEY,messageId TEXT NOT NULL,name TEXT NOT NULL,size INTEGER NOT NULL,hash TEXT NOT NULL DEFAULT '',storedName TEXT NOT NULL DEFAULT '',sourcePath TEXT NOT NULL DEFAULT '',extractedText TEXT NOT NULL DEFAULT '',payload TEXT NOT NULL);
        CREATE INDEX IF NOT EXISTS ix_files_message ON files(messageId);
        CREATE TABLE IF NOT EXISTS chunks(id INTEGER PRIMARY KEY AUTOINCREMENT,messageId TEXT NOT NULL,fileId TEXT NOT NULL DEFAULT '',ordinal INTEGER NOT NULL,text TEXT NOT NULL,vector BLOB,UNIQUE(messageId,fileId,ordinal));
        CREATE INDEX IF NOT EXISTS ix_chunks_message ON chunks(messageId);
        CREATE VIRTUAL TABLE IF NOT EXISTS chunk_fts USING fts5(tokens,content='',tokenize='unicode61');
        CREATE TABLE IF NOT EXISTS source_refs(id TEXT PRIMARY KEY,payload TEXT NOT NULL);
        CREATE TABLE IF NOT EXISTS receipts(id TEXT PRIMARY KEY,payload TEXT NOT NULL);
        CREATE TABLE IF NOT EXISTS message_aliases(alias TEXT PRIMARY KEY,messageId TEXT NOT NULL);
        CREATE TABLE IF NOT EXISTS meta(key TEXT PRIMARY KEY,value TEXT NOT NULL);
        """);
    }
    static SqliteConnection Open(string path) {var c=new SqliteConnection(new SqliteConnectionStringBuilder {DataSource=path,Mode=SqliteOpenMode.ReadWriteCreate,Pooling=false}.ToString());c.Open();using var cmd=c.CreateCommand();cmd.CommandText="PRAGMA journal_mode=WAL; PRAGMA synchronous=FULL; PRAGMA busy_timeout=15000;";cmd.ExecuteNonQuery();return c;}
    internal static string S(JsonNode? n,string key,string fallback="")=>n?[key] is JsonValue v&&v.TryGetValue<string>(out var s)?s:fallback;
    internal static string Stable(params string[] parts)=>Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\0',parts)))).ToLowerInvariant()[..32];
    static JsonObject Parse(string s)=>JsonNode.Parse(s)!.AsObject();
    SqliteCommand Command(string sql,params (string,object?)[] args){var c=db.CreateCommand();c.CommandText=sql;foreach(var (k,v) in args)c.Parameters.AddWithValue(k,v??DBNull.Value);return c;}
    internal void Run(string sql,params (string,object?)[] args){lock(gate){using var c=Command(sql,args);c.ExecuteNonQuery();}}
    internal string Meta(string key,string fallback=""){lock(gate){using var c=Command("SELECT value FROM meta WHERE key=$k",("$k",key));return c.ExecuteScalar()?.ToString()??fallback;}}
    internal void SetMeta(string key,string value)=>Run("INSERT INTO meta(key,value) VALUES($k,$v) ON CONFLICT(key) DO UPDATE SET value=excluded.value",("$k",key),("$v",value));
    internal bool HasReceipt(string id){lock(gate){using var c=Command("SELECT 1 FROM receipts WHERE id=$id",("$id",id));return c.ExecuteScalar()!=null;}}
    internal void Receipt(string id,JsonObject value)=>Run("INSERT OR REPLACE INTO receipts(id,payload) VALUES($id,$p)",("$id",id),("$p",value.ToJsonString()));
    internal void SaveConversation(JsonObject c)=>Run("INSERT INTO conversations(id,accountId,externalId,name,kind,payload) VALUES($id,$a,$e,$n,$k,$p) ON CONFLICT(id) DO UPDATE SET name=excluded.name,kind=excluded.kind,payload=excluded.payload",("$id",S(c,"id")),("$a",S(c,"accountId")),("$e",S(c,"externalId")),("$n",S(c,"name")),("$k",S(c,"kind")),("$p",c.ToJsonString()));
    internal JsonArray Conversations()
    {
        lock(gate)
        {
            var rows=new List<JsonObject>();using var c=Command("SELECT c.payload,COUNT(m.id),MAX(m.whenMs) FROM conversations c LEFT JOIN messages m ON m.conversationId=c.id GROUP BY c.id");using var r=c.ExecuteReader();
            while(r.Read()){var x=Parse(r.GetString(0));x["messageCount"]=r.GetInt64(1);x["lastMessageAt"]=r.IsDBNull(2)?"":DateTimeOffset.FromUnixTimeMilliseconds(r.GetInt64(2)).ToString("O");rows.Add(x);}
            static long LastMessage(JsonObject row)=>DateTimeOffset.TryParse(S(row,"sourceLastMessageAt"),out var source)?source.ToUnixTimeMilliseconds():DateTimeOffset.TryParse(S(row,"lastMessageAt"),out var imported)?imported.ToUnixTimeMilliseconds():long.MinValue;
            return new JsonArray(rows.OrderByDescending(LastMessage).ThenBy(x=>S(x,"name"),StringComparer.Ordinal).ThenBy(x=>S(x,"id"),StringComparer.Ordinal).Cast<JsonNode?>().ToArray());
        }
    }
    internal JsonObject? Conversation(string id){lock(gate){using var c=Command("SELECT payload FROM conversations WHERE id=$id",("$id",id));return c.ExecuteScalar() is string s?Parse(s):null;}}
    internal string CanonicalMessageId(string accountId,string conversationId,string localId,string serverId)
    {
        lock(gate)
        {
            var local=Stable("local",accountId,localId);var server=serverId.Length>0&&serverId!="0"?Stable("server",accountId,conversationId,serverId):"";
            string? Lookup(string alias){using var c=Command("SELECT messageId FROM message_aliases WHERE alias=$a",("$a",alias));return c.ExecuteScalar() as string;}
            var id=Lookup(local)??(server.Length>0?Lookup(server):null)??Stable(accountId,localId);
            Run("INSERT OR IGNORE INTO message_aliases(alias,messageId) VALUES($a,$m)",("$a",local),("$m",id));if(server.Length>0)Run("INSERT OR IGNORE INTO message_aliases(alias,messageId) VALUES($a,$m)",("$a",server),("$m",id));return id;
        }
    }
    internal bool UpsertMessage(JsonObject message)
    {
        lock(gate)
        {
            var id=S(message,"id");var text=S(message,"text");var payload=message.DeepClone().AsObject();payload.Remove("attachments");
            var serialized=payload.ToJsonString();using var old=Command("SELECT payload FROM messages WHERE id=$id",("$id",id));if(old.ExecuteScalar() is string prior&&prior==serialized)return false;
            Run("INSERT INTO messages(id,conversationId,senderId,whenMs,kind,text,payload,importedAt) VALUES($id,$c,$s,$w,$k,$t,$p,$ia) ON CONFLICT(id) DO UPDATE SET text=excluded.text,payload=excluded.payload,revision=revision+1,importedAt=excluded.importedAt",("$id",id),("$c",S(message,"conversationId")),("$s",S(message,"senderId")),("$w",DateTimeOffset.Parse(S(message,"when")).ToUnixTimeMilliseconds()),("$k",S(message,"kind")),("$t",text),("$p",serialized),("$ia",DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));
            var conv=Conversation(S(message,"conversationId"));SetChunks(id,"",S(conv,"name")+" · "+S(message,"senderName")+"\n"+text);return true;
        }
    }
    internal void UpsertFile(JsonObject file,string sourcePath,string storedName,string hash,string extracted)
    {
        lock(gate)
        {
            var id=S(file,"id");using var old=Command("SELECT hash,payload FROM files WHERE id=$id",("$id",id));using var r=old.ExecuteReader();string? oldHash=null,oldPayload=null;if(r.Read()){oldHash=r.GetString(0);oldPayload=r.GetString(1);}r.Close();
            // Never lose a previously downloaded original when WeChat later clears its cache.
            if(storedName.Length==0&&oldHash?.Length>0)return;
            var payload=file.ToJsonString();if(oldHash==hash&&oldPayload==payload)return;
            Run("INSERT INTO files(id,messageId,name,size,hash,storedName,sourcePath,extractedText,payload) VALUES($id,$m,$n,$s,$h,$sn,$src,$t,$p) ON CONFLICT(id) DO UPDATE SET name=excluded.name,size=excluded.size,hash=excluded.hash,storedName=excluded.storedName,sourcePath=excluded.sourcePath,extractedText=excluded.extractedText,payload=excluded.payload",("$id",id),("$m",S(file,"messageId")),("$n",S(file,"name")),("$s",file["size"]?.GetValue<long>()??0),("$h",hash),("$sn",storedName),("$src",sourcePath),("$t",extracted),("$p",payload));
            Run("UPDATE messages SET importedAt=$now WHERE id=$id",("$now",DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()),("$id",S(file,"messageId")));
            var msg=Message(S(file,"messageId"));SetChunks(S(file,"messageId"),id,S(file,"name")+"\n"+S(msg,"text")+"\n"+extracted);
        }
    }
    static IEnumerable<string> Split(string text)
    {
        if(string.IsNullOrWhiteSpace(text))yield break;
        // Overlapping source windows bound CPU work without dropping long-message/file tails.
        const int size=700,overlap=100;for(int i=0;i<text.Length;i+=size-overlap){yield return text.Substring(i,Math.Min(size,text.Length-i));if(i+size>=text.Length)break;}
    }
    static string Tokens(string text)
    {
        var b=new StringBuilder(text.ToLowerInvariant());b.Append(' ');for(int i=0;i<text.Length;i++)if(IsCjk(text[i])){b.Append(text[i]).Append(' ');if(i+1<text.Length&&IsCjk(text[i+1]))b.Append(text[i]).Append(text[i+1]).Append(' ');}return b.ToString();
    }
    static bool IsCjk(char c)=>c>='\u3400'&&c<='\u9fff';
    void SetChunks(string messageId,string fileId,string text)
    {
        using var transaction=db.BeginTransaction();
        using(var old=Command("SELECT id,text FROM chunks WHERE messageId=$m AND fileId=$f",("$m",messageId),("$f",fileId))){using var reader=old.ExecuteReader();var rows=new List<(long,string)>();while(reader.Read())rows.Add((reader.GetInt64(0),reader.GetString(1)));reader.Close();foreach(var (id,t) in rows)Run("INSERT INTO chunk_fts(chunk_fts,rowid,tokens) VALUES('delete',$id,$t)",("$id",id),("$t",Tokens(t)));}
        Run("DELETE FROM chunks WHERE messageId=$m AND fileId=$f",("$m",messageId),("$f",fileId));
        int n=0;foreach(var part in Split(text)){Run("INSERT INTO chunks(messageId,fileId,ordinal,text) VALUES($m,$f,$n,$t)",("$m",messageId),("$f",fileId),("$n",n++),("$t",part));Run("INSERT INTO chunk_fts(rowid,tokens) VALUES(last_insert_rowid(),$t)",("$t",Tokens(part)));}
        transaction.Commit();TouchVectorRevision();
    }
    internal JsonObject? Message(string id,bool includeHostPaths=false)
    {
        lock(gate){using var c=Command("SELECT payload FROM messages WHERE id=$id",("$id",id));var value=c.ExecuteScalar();if(value is not string s)return null;var m=Parse(s);m["attachments"]=FilesForMessage(id,includeHostPaths);return m;}
    }
    JsonArray FilesForMessage(string id,bool host)
    {
        var a=new JsonArray();using var c=Command("SELECT payload,storedName,sourcePath,hash,extractedText FROM files WHERE messageId=$id ORDER BY name",("$id",id));using var r=c.ExecuteReader();while(r.Read()){var f=Parse(r.GetString(0));f["available"]=r.GetString(1).Length>0;if(r.GetString(1).Length>0&&S(f,"mime").StartsWith("image/")&&S(f,"status")!="encrypted")f["previewUrl"]="https://student-progress-media.local/wechat-image/"+Uri.EscapeDataString(S(f,"id"))+"?v="+r.GetString(3);if(!host){f.Remove("originalStoredName");f.Remove("sourceWriteTicks");f.Remove("sourceSize");}if(host){f["storedName"]=r.GetString(1);f["sourcePath"]=r.GetString(2);f["hash"]=r.GetString(3);f["extractedText"]=r.GetString(4);}a.Add(f);}return a;
    }
    internal JsonObject Messages(string conversationId,long before=long.MaxValue,int limit=50,string beforeId="")
    {
        lock(gate){limit=Math.Clamp(limit,1,200);using var c=Command("SELECT id FROM messages WHERE conversationId=$c AND (whenMs<$b OR (whenMs=$b AND id<$bi)) ORDER BY whenMs DESC,id DESC LIMIT $l",("$c",conversationId),("$b",before),("$bi",beforeId.Length>0?beforeId:"\uffff"),("$l",limit+1));using var r=c.ExecuteReader();var ids=new List<string>();while(r.Read())ids.Add(r.GetString(0));r.Close();bool more=ids.Count>limit;var rows=ids.Take(limit).Select(id=>Message(id)!).Reverse().ToArray();return new JsonObject{["messages"]=new JsonArray(rows),["hasMore"]=more,["before"]=rows.Length>0?DateTimeOffset.Parse(S(rows[0],"when")).ToUnixTimeMilliseconds():0,["beforeId"]=rows.Length>0?S(rows[0],"id"):""};}
    }
    internal JsonObject MessagesAround(string conversationId,string around,int limit)
    {
        lock(gate)
        {
            var center=Message(around);if(center==null||S(center,"conversationId")!=conversationId)return Messages(conversationId,limit:limit);
            long when=DateTimeOffset.Parse(S(center,"when")).ToUnixTimeMilliseconds();limit=Math.Clamp(limit,1,200);
            using var c=Command("SELECT id FROM messages WHERE conversationId=$c ORDER BY ABS(whenMs-$w),whenMs,id LIMIT $l",("$c",conversationId),("$w",when),("$l",limit));using var r=c.ExecuteReader();var ids=new List<string>();while(r.Read())ids.Add(r.GetString(0));r.Close();var rows=ids.Select(id=>Message(id)!).OrderBy(x=>S(x,"when")).ThenBy(x=>S(x,"id")).ToArray();
            return new JsonObject{["messages"]=new JsonArray(rows),["hasMore"]=true,["before"]=rows.Length>0?DateTimeOffset.Parse(S(rows[0],"when")).ToUnixTimeMilliseconds():0,["beforeId"]=rows.Length>0?S(rows[0],"id"):""};
        }
    }
    internal JsonObject? File(string id,bool host=false)
    {
        lock(gate){using var c=Command("SELECT messageId FROM files WHERE id=$id",("$id",id));return c.ExecuteScalar() is string m?FilesForMessage(m,host).OfType<JsonObject>().FirstOrDefault(f=>S(f,"id")==id)?.DeepClone().AsObject():null;}
    }
    internal JsonArray Files(string conversationId="",int limit=200,int offset=0)
    {
        lock(gate){using var c=Command("SELECT f.id,m.payload FROM files f JOIN messages m ON m.id=f.messageId WHERE ($c='' OR m.conversationId=$c) ORDER BY m.whenMs DESC,f.id DESC LIMIT $l OFFSET $o",("$c",conversationId),("$l",Math.Clamp(limit,1,1001)),("$o",Math.Max(0,offset)));using var r=c.ExecuteReader();var rows=new List<(string,string)>();while(r.Read())rows.Add((r.GetString(0),r.GetString(1)));r.Close();var a=new JsonArray();foreach(var (id,p) in rows){var f=File(id)!;var m=Parse(p);f["conversationId"]=S(m,"conversationId");f["senderName"]=S(m,"senderName");f["when"]=S(m,"when");a.Add(f);}return a;}
    }
    internal JsonArray ArchiveCandidates()
    {
        lock(gate){using var c=Command("SELECT f.id FROM files f JOIN messages m ON m.id=f.messageId WHERE f.storedName<>'' ORDER BY m.whenMs");using var r=c.ExecuteReader();var ids=new List<string>();while(r.Read())ids.Add(r.GetString(0));r.Close();return new JsonArray(ids.Select(id=>(JsonNode?)File(id,true)).ToArray());}
    }
    internal List<(long Id,string Text)> PendingChunks(int limit=64)
    {lock(gate){using var c=Command("SELECT id,text FROM chunks WHERE vector IS NULL ORDER BY id LIMIT $l",("$l",limit));using var r=c.ExecuteReader();var a=new List<(long,string)>();while(r.Read())a.Add((r.GetInt64(0),r.GetString(1)));return a;}}
    internal void SetVector(long id,float[] v){lock(gate){var bytes=new byte[v.Length*4];Buffer.BlockCopy(v,0,bytes,0,bytes.Length);Run("UPDATE chunks SET vector=$v WHERE id=$id",("$v",bytes),("$id",id));Run("INSERT INTO meta(key,value) VALUES('vectorRevision','1') ON CONFLICT(key) DO UPDATE SET value=CAST(value AS INTEGER)+1");}}
    void TouchVectorRevision(){Run("INSERT INTO meta(key,value) VALUES('vectorRevision','1') ON CONFLICT(key) DO UPDATE SET value=CAST(value AS INTEGER)+1");SetMeta("vectorDirty","1");}
    internal IEnumerable<(long Id,float[] Vector)> AllVectors()
    {var a=new List<(long,float[])>();lock(gate){using var c=Command("SELECT id,vector FROM chunks WHERE vector IS NOT NULL");using var r=c.ExecuteReader();while(r.Read()){var b=(byte[])r[1];var v=new float[b.Length/4];Buffer.BlockCopy(b,0,v,0,b.Length);a.Add((r.GetInt64(0),v));}}return a;}
    internal JsonObject Counts(){lock(gate){using var c=Command("SELECT (SELECT COUNT(*) FROM messages),(SELECT COUNT(*) FROM files),(SELECT COUNT(*) FROM chunks),(SELECT COUNT(*) FROM chunks WHERE vector IS NOT NULL)");using var r=c.ExecuteReader();r.Read();return new JsonObject{["messages"]=r.GetInt64(0),["files"]=r.GetInt64(1),["chunks"]=r.GetInt64(2),["indexed"]=r.GetInt64(3)};}}
    internal List<long> KeywordCandidates(string query,int limit=120,string conversationId="",string kind="",long from=long.MinValue,long through=long.MaxValue)
    {
        var terms=Regex.Matches(query.ToLowerInvariant(),@"[\p{L}\p{N}_]+").Select(m=>m.Value).Where(x=>x.Length>0).Take(12).ToArray();if(terms.Length==0)return [];
        var expanded=terms.Concat(terms.Where(t=>t.Any(IsCjk)).SelectMany(t=>Enumerable.Range(0,Math.Max(0,t.Length-1)).Where(i=>IsCjk(t[i])&&IsCjk(t[i+1])).Select(i=>t.Substring(i,2)))).Distinct().Take(30);
        var match=string.Join(" OR ",expanded.Select(x=>'"'+x.Replace("\"","\"\"")+'"'));lock(gate){using var c=Command("SELECT chunk_fts.rowid FROM chunk_fts JOIN chunks c ON c.id=chunk_fts.rowid JOIN messages m ON m.id=c.messageId WHERE chunk_fts MATCH $q AND ($c='' OR m.conversationId=$c) AND ($k<>'file' OR c.fileId<>'') AND m.whenMs>=$from AND m.whenMs<$through ORDER BY rank LIMIT $l",("$q",match),("$l",limit),("$c",conversationId),("$k",kind),("$from",from),("$through",through));using var r=c.ExecuteReader();var rows=new List<long>();while(r.Read())rows.Add(r.GetInt64(0));return rows;}
    }
    internal List<long> FilteredSimilarity(float[] query,string conversationId,string kind,long from,long through,int limit=320)
    {
        lock(gate)
        {
            using var c=Command("SELECT c.id,c.vector FROM chunks c JOIN messages m ON m.id=c.messageId WHERE c.vector IS NOT NULL AND ($conv='' OR m.conversationId=$conv) AND ($kind<>'file' OR c.fileId<>'') AND m.whenMs>=$from AND m.whenMs<$through",("$conv",conversationId),("$kind",kind),("$from",from),("$through",through));using var r=c.ExecuteReader();var best=new PriorityQueue<long,double>();
            while(r.Read()){var bytes=(byte[])r[1];if(bytes.Length!=query.Length*4)continue;var values=System.Runtime.InteropServices.MemoryMarshal.Cast<byte,float>(bytes.AsSpan());double score=0;for(int i=0;i<query.Length;i++)score+=(double)query[i]*values[i];best.Enqueue(r.GetInt64(0),score);if(best.Count>limit)best.Dequeue();}
            return best.UnorderedItems.OrderByDescending(x=>x.Priority).Select(x=>x.Element).ToList();
        }
    }
    internal JsonObject? Chunk(long id)
    {lock(gate){using var c=Command("SELECT messageId,fileId,text FROM chunks WHERE id=$id",("$id",id));using var r=c.ExecuteReader();if(!r.Read())return null;return new JsonObject{["id"]=id,["messageId"]=r.GetString(0),["fileId"]=r.GetString(1),["text"]=r.GetString(2)};}}
    internal void SaveReference(JsonObject r)=>Run("INSERT OR REPLACE INTO source_refs(id,payload) VALUES($id,$p)",("$id",S(r,"id")),("$p",r.ToJsonString()));
    internal JsonObject? Reference(string id){lock(gate){using var c=Command("SELECT payload FROM source_refs WHERE id=$id",("$id",id));return c.ExecuteScalar() is string p?Parse(p):null;}}
    internal IEnumerable<JsonObject> ReadMessages(string conversationId="",long from=0,long through=long.MaxValue,ISet<string>? ids=null,bool host=false)
    {
        // Page through the database so full-history exports do not need a huge JSON array.
        long after=from-1;string afterId="";
        while(true){List<(string Id,long When)> page=new();lock(gate){using var c=Command("SELECT id,whenMs FROM messages WHERE ($c='' OR conversationId=$c) AND whenMs<=$end AND (whenMs>$a OR (whenMs=$a AND id>$ai)) ORDER BY whenMs,id LIMIT 200",("$c",conversationId),("$end",through),("$a",after),("$ai",afterId));using var r=c.ExecuteReader();while(r.Read())page.Add((r.GetString(0),r.GetInt64(1)));}if(page.Count==0)yield break;foreach(var (id,when) in page){after=when;afterId=id;if(ids!=null&&!ids.Contains(id))continue;var m=Message(id,host);if(m!=null)yield return m;}}
    }
    internal IEnumerable<JsonObject> AnalysisMessages(long importedSince,long messageFrom)
    {
        List<string> ids=new();lock(gate){using var c=Command("SELECT id FROM messages WHERE importedAt>=$i AND whenMs>=$m ORDER BY whenMs,id",("$i",importedSince),("$m",messageFrom));using var r=c.ExecuteReader();while(r.Read())ids.Add(r.GetString(0));}foreach(var id in ids){var m=Message(id);if(m!=null)yield return m;}
    }
    internal void ReplaceFrom(string source,string recovery)
    {
        lock(gate)
        {
            using(var test=Open(source)){using var check=test.CreateCommand();check.CommandText="PRAGMA integrity_check";if(check.ExecuteScalar()?.ToString()!="ok")throw new InvalidDataException("微信资料库不完整。");}
            BackupTo(recovery);db.Dispose();
            try{System.IO.File.Copy(source,databasePath+".restoring",true);if(System.IO.File.Exists(databasePath+"-wal"))System.IO.File.Delete(databasePath+"-wal");if(System.IO.File.Exists(databasePath+"-shm"))System.IO.File.Delete(databasePath+"-shm");System.IO.File.Move(databasePath+".restoring",databasePath,true);}
            catch{System.IO.File.Copy(recovery,databasePath,true);throw;}
            finally{db=Open(databasePath);}
        }
    }
    internal void BackupTo(string destination){lock(gate){using var target=Open(destination);db.BackupDatabase(target);using var c=target.CreateCommand();c.CommandText="PRAGMA wal_checkpoint(TRUNCATE)";c.ExecuteNonQuery();}}
    internal void Reload(){lock(gate){db.Dispose();db=Open(databasePath);}}
    public void Dispose(){lock(gate)db.Dispose();}
}
