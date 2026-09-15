using System.Text.Json.Nodes;

namespace StudentProgress;

public sealed partial class AppService
{
    // C# host boundary only. This method is intentionally not routed by HandleAsync.
    public JsonObject GetAssistantConnection()
    {
        lock(gate)
        {
            ObjectDisposedException.ThrowIf(disposed,this);
            var settings=(JsonObject)state["settings"]!;
            return new JsonObject { ["baseUrl"]=S(settings,"baseUrl"),["model"]=S(settings,"model"),["key"]=ReadKey(),["apiMode"]=S(settings,"apiMode","responses") };
        }
    }

    public JsonObject BuildAssistantContext(JsonObject selection)
    {
        lock(gate)
        {
            ObjectDisposedException.ThrowIf(disposed,this);
            var recent=new JsonArray();var referenced=new JsonArray();
            if(B(selection,"includeRecent",true))
            {
                foreach(var type in new[]{"student","project"})
                    foreach(var scope in Rows(type=="student"?"students":"projects"))
                        recent.Add(AssistantArchive(type,scope,false,false));
            }
            if(selection["references"]!=null&&selection["references"] is not JsonArray)throw new InvalidOperationException("归档引用格式无效。");
            var seen=new HashSet<string>(StringComparer.Ordinal);
            foreach(var item in selection["references"] as JsonArray ?? new())
            {
                if(item is not JsonObject reference)throw new InvalidOperationException("归档引用格式无效。");
                var type=Required(reference,"scopeType");var id=Required(reference,"scopeId");
                if(type is not ("student" or "project"))throw new InvalidOperationException("归档引用对象无效。");
                if(!seen.Add(type+":"+id))continue;
                var scope=Rows(type=="student"?"students":"projects").FirstOrDefault(x=>S(x,"id")==id);
                var archived=false;
                if(scope==null&&type=="student")
                {
                    scope=Rows("archivedStudents").FirstOrDefault(x=>S(x,"id")==id);archived=scope!=null;
                }
                if(scope==null)throw new InvalidOperationException("引用的归档已不存在，请重新选择。");
                referenced.Add(AssistantArchive(type,scope,true,archived));
            }
            return new JsonObject
            {
                ["generatedAt"]=Now(),
                ["currentUser"]=CurrentUser(),
                ["students"]=new JsonArray(Rows("students").Select(s=>(JsonNode)AssistantFields(s,"id","name","grade","tags","bio","currentSummary")).ToArray()),
                ["projects"]=new JsonArray(Rows("projects").Select(p=>(JsonNode)AssistantFields(p,"id","name","studentIds","currentSummary")).ToArray()),
                ["tasks"]=new JsonArray(Rows("tasks").Select(t=>(JsonNode)AssistantFields(t,"id","title","studentIds","projectId","start","due","done","tags","createdAt")).ToArray()),
                ["reminders"]=B(selection,"includeReminders",true)?new JsonArray(Rows("alarms").Select(a=>(JsonNode)AssistantFields(a,"id","scopeType","studentId","projectId","title","when","repeat","intervalDays","enabled","monthDay","lastTriggeredWhen")).ToArray()):new JsonArray(),
                ["recentArchives"]=recent,["referencedArchives"]=referenced
            };
        }
    }

    JsonObject? CurrentUser()
    {
        var id=S((JsonObject)state["settings"]!,"currentStudentId");
        var member=Rows("students").FirstOrDefault(s=>S(s,"id")==id);
        return member==null?null:AssistantFields(member,"id","name");
    }

    JsonObject AssistantArchive(string type,JsonObject scope,bool whole,bool archived)
    {
        var messages=Rows("messages").Where(m=>S(m,"scopeType")==type&&S(m,"scopeId")==S(scope,"id"));
        if(!whole)messages=messages.TakeLast(3);
        var output=new JsonArray();
        foreach(var message in messages)
        {
            var text=S(message,"text");var truncated=!whole&&text.Length>1200;
            var attachments=new JsonArray();
            foreach(var attachment in (message["attachments"] as JsonArray ?? new()).OfType<JsonObject>())
            {
                var file=AssistantFields(attachment,"name","textStatus");
                if(whole)file["extractedText"]=S(attachment,"extractedText");
                attachments.Add(file);
            }
            var record=AssistantFields(message,"id","role","createdAt");
            record["text"]=truncated?text[..1200]+"\n[最近归档内容已截取；引用整个归档可读取全文]":text;
            record["truncated"]=truncated;record["attachments"]=attachments;
            if(whole)record["suggestions"]=new JsonArray((message["suggestions"] as JsonArray ?? new()).OfType<JsonObject>().Select(s=>(JsonNode)AssistantFields(s,"title")).ToArray());
            output.Add(record);
        }
        return new JsonObject { ["scopeType"]=type,["scopeId"]=S(scope,"id"),["title"]=S(scope,"name"),["archived"]=archived,["messages"]=output };
    }

    static JsonObject AssistantFields(JsonObject source,params string[] names)
    {
        var target=new JsonObject();
        foreach(var name in names)if(source.TryGetPropertyValue(name,out var value))target[name]=value?.DeepClone();
        return target;
    }
}
