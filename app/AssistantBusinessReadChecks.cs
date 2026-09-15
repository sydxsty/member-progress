using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace StudentProgress;

// Isolated contract checks; no live Data, model, embedding or external connection.
internal static class AssistantBusinessReadChecks
{
    internal static async Task<JsonObject> RunAsync(string directory)
    {
        if(Directory.Exists(directory)&&Directory.EnumerateFileSystemEntries(directory).Any())throw new InvalidOperationException("业务只读检查需要空目录。");
        Directory.CreateDirectory(directory);string data=Path.Combine(directory,"fixture"),work=Path.Combine(directory,"materials");Directory.CreateDirectory(Path.Combine(data,"attachments"));Directory.CreateDirectory(work);
        File.WriteAllText(Path.Combine(data,"initialized"),"synthetic-business-read-check");
        var checks=new JsonArray();void Check(bool value,string name){if(!value)throw new InvalidOperationException(name);checks.Add(name);}
        static JsonObject Obj(object value)=>JsonSerializer.SerializeToNode(value)!.AsObject();
        static string S(JsonNode? node,string name)=>node?[name]?.GetValue<string>()??"";
        static string Hash(string path)=>Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
        int year=DateTime.Now.Month>=9?DateTime.Now.Year:DateTime.Now.Year-1;
        JsonObject Member(string id,string name)=>Obj(new{id,name,grade="",gradeAcademicYear=year,tags=Array.Empty<string>(),bio="研究主题",currentSummary="当前进展"});
        JsonObject FileRecord(string id,string name,string extracted)=>Obj(new{id,name,mime="text/plain",size=27,storedName=id+".txt",textStatus="已提取文字",extractedText=extracted});
        JsonObject Message(string id,string scope,string text,string date,JsonArray? files=null)=>new(){["id"]=id,["scopeType"]="student",["scopeId"]=scope,["role"]="user",["text"]=text,["createdAt"]=date,["attachments"]=files??new JsonArray(),["suggestions"]=new JsonArray()};
        string privateMarker="NEVER_EXPOSE_CONNECTION_TOKEN",hostMarker="C:/private/source/not-for-agent";
        var state=new JsonObject
        {
            ["schemaVersion"]=1,["students"]=new JsonArray(Member("self","当前成员")),["archivedStudents"]=new JsonArray(Member("former","已归档成员")),
            ["projects"]=new JsonArray(Obj(new{id="project",name="研究项目",studentIds=new[]{"self"},currentSummary="项目进展"})),
            ["tasks"]=new JsonArray(Obj(new{id="task",title="南湖出差",studentIds=new[]{"self"},projectId="project",start="2030-10-22",due="2030-10-24",done=false,tags=Array.Empty<string>(),createdAt="2030-10-01T09:00:00+08:00"})),
            ["alarms"]=new JsonArray(Obj(new{id="monthly",title="发劳务费",scopeType="student",studentId="self",when="2030-10-09T09:00:00+08:00",repeat="monthly",monthDay=9,monthTime="09:00:00",enabled=true,intervalDays=1}),Obj(new{id="disabled",title="已关闭",scopeType="global",when="2030-10-09T09:00:00+08:00",repeat="once",enabled=false})),
            ["messages"]=new JsonArray(Message("old-a","former","旧会话中的关键决定","2020-01-01T10:00:00+08:00",new JsonArray(FileRecord("evidence","原始材料.txt","只有文件提取中包含的检索词"))),Message("old-b","former","较晚的关键决定","2020-01-02T10:00:00+08:00"),Message("current","self","最新文字","2030-01-01T10:00:00+08:00")),
            ["deletedMessages"]=new JsonArray(new JsonObject{["id"]="deleted",["message"]=Message("deleted","self","删除后的秘密正文","2020-01-01T10:00:00+08:00",new JsonArray(FileRecord("deleted-file","删除文件.txt","已删除提取文字")))}),
            ["settings"]=Obj(new{currentStudentId="self",baseUrl="http://127.0.0.1:1/v1",model="",key=privateMarker,dataRoot=hostMarker}),["status"]=new JsonObject()
        };
        File.WriteAllText(Path.Combine(data,"attachments","evidence.txt"),"完整原始材料：原件末尾验证码 ORIGINAL-73",new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(data,"attachments","deleted-file.txt"),"删除后原件不得通过接口获取");
        string statePath=Path.Combine(data,"state.json");File.WriteAllText(statePath,state.ToJsonString());
        using var app=new AppService(data);string before=Hash(statePath);
        Task<JsonObject> Read(string op,JsonObject? args=null)=>app.ReadAssistantDataAsync(op,args??new(),work,CancellationToken.None);
        var observe=await Read("app.observe");var members=await Read("app.members");
        Check(S(observe["currentUser"],"id")=="self"&&members["total"]!.GetValue<int>()==2&&members["items"]!.AsArray().Any(m=>S(m,"id")=="former"&&m!["archived"]!.GetValue<bool>()),"observe_current_user_and_archived_member_readable");
        var first=await Read("app.archive",Obj(new{scopeType="student",scopeId="former",limit=1}));
        var second=await Read("app.archive",Obj(new{scopeType="student",scopeId="former",limit=1,offset=first["nextOffset"]!.GetValue<int>(),snapshotVersion=S(first,"snapshotVersion")}));
        Check(first["total"]!.GetValue<int>()==2&&S(first["items"]![0],"id")=="old-a"&&S(second["items"]![0],"id")=="old-b"&&!second["hasMore"]!.GetValue<bool>()&&second["nextOffset"]==null,"older_archives_paginate_without_recent_context_limit");
        var search=await Read("app.search",Obj(new{query="文件提取中",kind="archive",memberId="former"}));
        Check(search["total"]!.GetValue<int>()==1&&S(search["items"]![0],"attachmentId")=="evidence"&&S(search["items"]![0],"scopeId")=="former","global_search_finds_attachment_text_and_returns_original_locator");
        var tasks=await Read("app.tasks",Obj(new{from="2030-10-23",to="2030-10-23",memberId="self"}));
        var reminders=await Read("app.reminders",Obj(new{mode="occurrences",from="2030-11-01",to="2031-01-31",memberId="self"}));
        Check(tasks["total"]!.GetValue<int>()==1&&reminders["total"]!.GetValue<int>()==3&&reminders["items"]!.AsArray().All(r=>S(r,"reminderId")=="monthly"&&DateTimeOffset.Parse(S(r,"when")).Day==9),"task_date_overlap_and_monthly_occurrences_use_original_schedule");
        var material=await Read("app.file",Obj(new{attachmentId="evidence"}));string relative=S(material,"file");
        Check(S(material,"readStatus")=="ready"&&!Path.IsPathRooted(relative)&&File.ReadAllText(Path.Combine(work,relative)).Contains("ORIGINAL-73")&&S(material,"scopeId")=="former"&&S(material,"extractedTextFile").Length>0,"requested_original_is_materialized_with_source_context");
        bool missing=false,mutation=false,stale=false,traversal=false;
        try{await Read("app.file",Obj(new{attachmentId="deleted-file"}));}catch(InvalidOperationException){missing=true;}
        try{await Read("task.delete",Obj(new{id="task"}));}catch(InvalidOperationException){mutation=true;}
        try{await Read("app.members",Obj(new{offset=1,snapshotVersion="outdated"}));}catch(InvalidOperationException){stale=true;}
        try{await Read("app.file",Obj(new{attachmentId="../../state.json"}));}catch(InvalidOperationException){traversal=true;}
        var deletedSearch=await Read("app.search",Obj(new{query="删除"}));
        Check(missing&&mutation&&stale&&traversal&&deletedSearch["total"]!.GetValue<int>()==0,"deleted_records_mutations_stale_pages_and_path_requests_are_rejected");
        string output=string.Join("\n",new[]{observe,members,first,second,search,tasks,reminders,material,deletedSearch}.Select(x=>x.ToJsonString()));
        Check(!output.Contains(privateMarker)&&!output.Contains(hostMarker)&&!output.Contains("storedName")&&!output.Contains("deletedMessages")&&!output.Contains(data)&&Hash(statePath)==before,"read_interface_excludes_credentials_host_paths_and_preserves_business_state");
        return new JsonObject{["ok"]=true,["count"]=checks.Count,["checks"]=checks,["modelCalls"]=0,["liveDataReads"]=0};
    }
}
