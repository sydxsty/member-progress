using System.Text.Json;
using System.Text.Json.Nodes;
using StudentProgress.Assistant;

namespace StudentProgress;

internal static class AssistantLiveCheck
{
    public static int Run(string directory)
    {
        try { return RunAsync(directory).GetAwaiter().GetResult(); }
        catch(Exception error)
        {
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory,"assistant-live-result.json"),JsonSerializer.Serialize(new{ok=false,error=error.Message}));
            return 1;
        }
    }
    static async Task<int> RunAsync(string directory)
    {
        // The caller supplies only a copy of connection settings, never production records.
        using(var setup=new AppService(directory))
        {
            if(setup.Snapshot()["students"]!.AsArray().Count>0)throw new InvalidOperationException("Live check requires an empty student list.");
            await setup.HandleAsync("student.save",new JsonObject{["name"]="内核联调学生"});
            var id=setup.Snapshot()["students"]![0]!["id"]!.GetValue<string>();
            await setup.HandleAsync("task.add",new JsonObject{["scopeType"]="student",["scopeId"]=id,["title"]="提交论文初稿",["due"]=DateTime.Now.AddDays(2).ToString("yyyy-MM-dd")});
            var nextMonth=DateTime.Now.AddMonths(1);
            await setup.HandleAsync("alarm.save",new JsonObject{["scopeType"]="global",["title"]="发放劳务费",["repeat"]="monthly",["when"]=new DateTimeOffset(new DateTime(nextMonth.Year,nextMonth.Month,9,9,0,0)).ToString("O")});
        }
        var statePath=Path.Combine(directory,"state.json");
        var state=JsonNode.Parse(File.ReadAllText(statePath))!.AsObject();
        var studentId=state["students"]![0]!["id"]!.GetValue<string>();
        state["messages"]!.AsArray().Add(new JsonObject{["id"]="fixture-archive",["scopeType"]="student",["scopeId"]=studentId,["role"]="user",["text"]="实验已经完成，论文初稿还未写完，需要在两天内提交。",["createdAt"]=DateTimeOffset.Now.ToString("O"),["attachments"]=new JsonArray(),["suggestions"]=new JsonArray()});
        File.WriteAllText(statePath,state.ToJsonString());
        using var core=new AppService(directory);
        using var assistant=new AssistantService(directory,AppContext.BaseDirectory,core.BuildAssistantContext,core.GetAssistantConnection);
        await assistant.HandleAsync("assistant.send",new JsonObject{["text"]="根据提醒和引用归档，只用一句中文给出现在最应优先做的事。",["includeReminders"]=true,["includeRecent"]=true,["references"]=new JsonArray(new JsonObject{["scopeType"]="student",["scopeId"]=studentId,["title"]="内核联调学生的完整归档"})});
        var limit=DateTime.UtcNow.AddMinutes(3);
        while(assistant.Snapshot()["busy"]!.GetValue<bool>()&&DateTime.UtcNow<limit)await Task.Delay(250);
        var result=assistant.Snapshot();
        if(result["busy"]!.GetValue<bool>())await assistant.HandleAsync("assistant.cancel",new JsonObject());
        var answer=result["messages"]!.AsArray().LastOrDefault(x=>x?["role"]?.GetValue<string>()=="assistant")?["text"]?.GetValue<string>();
        var ok=!string.IsNullOrWhiteSpace(answer)&&string.IsNullOrWhiteSpace(result["error"]?.GetValue<string>());
        File.WriteAllText(Path.Combine(directory,"assistant-live-result.json"),JsonSerializer.Serialize(new{ok,cliInvocations=1,answer,error=result["error"]?.GetValue<string>(),runtimeVersion=result["runtime"]?["version"]?.GetValue<string>()},new JsonSerializerOptions{WriteIndented=true}));
        return ok?0:1;
    }
}
