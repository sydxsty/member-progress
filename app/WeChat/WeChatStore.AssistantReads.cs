using System.Text.Json.Nodes;

namespace StudentProgress.WeChat;

internal sealed partial class WeChatStore
{
    internal JsonObject AssistantConversations(string query,int offset,int limit)
    {
        lock(gate)
        {
            const string table="FROM conversations c JOIN (SELECT conversationId,COUNT(*) AS messageCount,MAX(whenMs) AS lastWhen FROM messages GROUP BY conversationId) m ON m.conversationId=c.id WHERE ($q='' OR instr(lower(c.name),lower($q))>0 OR instr(lower(c.externalId),lower($q))>0)";
            using var count=Command("SELECT COUNT(*) "+table,("$q",query));long total=Convert.ToInt64(count.ExecuteScalar());
            using var command=Command("SELECT c.payload,m.messageCount,m.lastWhen "+table+" ORDER BY COALESCE(CAST(strftime('%s',json_extract(c.payload,'$.sourceLastMessageAt')) AS INTEGER)*1000,m.lastWhen) DESC,c.name,c.id LIMIT $l OFFSET $o",("$q",query),("$l",limit),("$o",offset));
            using var reader=command.ExecuteReader();var rows=new JsonArray();
            while(reader.Read())
            {
                var raw=Parse(reader.GetString(0));rows.Add(new JsonObject{["id"]=S(raw,"id"),["name"]=S(raw,"name"),["kind"]=S(raw,"kind"),["messageCount"]=reader.GetInt64(1),["lastMessageAt"]=DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(2)).ToString("O"),["sourceLastMessageAt"]=S(raw,"sourceLastMessageAt")});
            }
            return new(){["conversations"]=rows,["total"]=total,["offset"]=offset,["limit"]=limit,["hasMore"]=offset+rows.Count<total,["nextOffset"]=offset+rows.Count<total?JsonValue.Create(offset+rows.Count):null,["order"]="last_updated_desc,name,id"};
        }
    }

    internal JsonObject AssistantMessages(string conversation,string query,string kind,string sender,long from,long through,long? cursor,string cursorId,bool ascending,int limit)
    {
        lock(gate)
        {
            var filters=new List<string>{"m.whenMs>=$from","m.whenMs<$through"};var parameters=new List<(string,object?)>{("$from",from),("$through",through),("$limit",limit+1)};
            if(conversation.Length>0){filters.Add("m.conversationId=$conversation");parameters.Add(("$conversation",conversation));}
            if(kind.Length>0){filters.Add("m.kind=$kind");parameters.Add(("$kind",kind));}
            if(sender.Length>0){filters.Add("m.senderId=$sender");parameters.Add(("$sender",sender));}
            // Literal matching is intentionally complete, unlike ranked top-K retrieval. Time/conversation indexes bound scans when supplied.
            if(query.Length>0){filters.Add("(instr(lower(m.text),lower($query))>0 OR instr(lower(COALESCE(json_extract(m.payload,'$.rawContent'),'')),lower($query))>0 OR instr(lower(COALESCE(json_extract(m.payload,'$.senderName'),'')),lower($query))>0)");parameters.Add(("$query",query));}
            if(cursor.HasValue)
            {
                string sign=ascending?">":"<";filters.Add($"(m.whenMs{sign}$cursor OR (m.whenMs=$cursor AND m.id{sign}$cursorId))");parameters.Add(("$cursor",cursor.Value));parameters.Add(("$cursorId",cursorId.Length>0?cursorId:ascending?"":"\uffff"));
            }
            var order=ascending?"ASC":"DESC";using var command=Command("SELECT m.id,m.whenMs FROM messages m WHERE "+string.Join(" AND ",filters)+$" ORDER BY m.whenMs {order},m.id {order} LIMIT $limit",parameters.ToArray());
            using var reader=command.ExecuteReader();var ids=new List<(string Id,long When)>();while(reader.Read())ids.Add((reader.GetString(0),reader.GetInt64(1)));reader.Close();bool more=ids.Count>limit;var selected=ids.Take(limit).ToArray();var messages=new JsonArray(selected.Select(x=>(JsonNode?)Message(x.Id)).ToArray());
            return new(){["messages"]=messages,["limit"]=limit,["hasMore"]=more,["order"]=ascending?"oldest_first":"newest_first",["nextCursor"]=more?new JsonObject{[ascending?"after":"before"]=selected[^1].When,[ascending?"afterId":"beforeId"]=selected[^1].Id}:null,["matchMode"]=query.Length>0?"literal_substring":"all"};
        }
    }

    internal JsonObject AssistantMessagesAround(string conversation,string id,int limit)
    {
        lock(gate)
        {
            var center=Message(id)??throw new InvalidOperationException("微信消息不存在。");var actual=S(center,"conversationId");if(conversation.Length>0&&actual!=conversation)throw new InvalidOperationException("消息不属于指定微信会话。");
            long when=DateTimeOffset.Parse(S(center,"when")).ToUnixTimeMilliseconds();int half=Math.Max(1,(limit-1)/2);var older=AssistantMessages(actual,"","","",long.MinValue,long.MaxValue,when,id,false,half);var newer=AssistantMessages(actual,"","","",long.MinValue,long.MaxValue,when,id,true,Math.Max(1,limit-half-1));
            var rows=new JsonArray();foreach(var m in older["messages"]!.AsArray().Reverse())rows.Add(m!.DeepClone());rows.Add(center);foreach(var m in newer["messages"]!.AsArray())rows.Add(m!.DeepClone());
            return new(){["messages"]=rows,["conversationId"]=actual,["centerMessageId"]=id,["order"]="oldest_first",["hasOlder"]=older["hasMore"]!.DeepClone(),["hasNewer"]=newer["hasMore"]!.DeepClone(),["olderCursor"]=older["nextCursor"]?.DeepClone(),["newerCursor"]=newer["nextCursor"]?.DeepClone()};
        }
    }

    internal JsonObject AssistantFiles(string conversation,string query,string kind,long from,long through,int offset,int limit)
    {
        lock(gate)
        {
            var filters=new List<string>{"m.whenMs>=$from","m.whenMs<$through"};var parameters=new List<(string,object?)>{("$from",from),("$through",through)};
            if(conversation.Length>0){filters.Add("m.conversationId=$conversation");parameters.Add(("$conversation",conversation));}
            if(query.Length>0){filters.Add("(instr(lower(f.name),lower($query))>0 OR instr(lower(f.extractedText),lower($query))>0 OR instr(lower(m.text),lower($query))>0)");parameters.Add(("$query",query));}
            if(kind.Length>0){filters.Add("COALESCE(json_extract(f.payload,'$.type'),'')=$kind");parameters.Add(("$kind",kind));}
            var fromSql=" FROM files f JOIN messages m ON m.id=f.messageId WHERE "+string.Join(" AND ",filters);using var count=Command("SELECT COUNT(*)"+fromSql,parameters.ToArray());long total=Convert.ToInt64(count.ExecuteScalar());parameters.Add(("$limit",limit));parameters.Add(("$offset",offset));
            using var command=Command("SELECT f.id,m.conversationId,m.payload"+fromSql+" ORDER BY m.whenMs DESC,f.id DESC LIMIT $limit OFFSET $offset",parameters.ToArray());using var reader=command.ExecuteReader();var ids=new List<(string Id,string Conversation,string Payload)>();while(reader.Read())ids.Add((reader.GetString(0),reader.GetString(1),reader.GetString(2)));reader.Close();var files=new JsonArray();
            foreach(var (id,conv,payload) in ids){var file=File(id)!;var msg=Parse(payload);file["conversationId"]=conv;file["senderName"]=S(msg,"senderName");file["when"]=S(msg,"when");files.Add(file);}
            return new(){["files"]=files,["total"]=total,["offset"]=offset,["limit"]=limit,["hasMore"]=offset+files.Count<total,["nextOffset"]=offset+files.Count<total?JsonValue.Create(offset+files.Count):null,["order"]="newest_first"};
        }
    }
}
