using System.Text.Json;
using System.Text.Json.Nodes;
using StudentProgress.Assistant;

namespace StudentProgress;

internal static class AssistantReminderLiveCheck
{
    public static int Run(string directory)
    {
        try{return RunAsync(directory).GetAwaiter().GetResult();}
        catch(Exception e){Directory.CreateDirectory(directory);File.WriteAllText(Path.Combine(directory,"reminder-live-result.json"),JsonSerializer.Serialize(new{ok=false,error=e.Message}));return 1;}
    }
    static async Task<int> RunAsync(string directory)
    {
        using var core=new AppService(directory);
        if(core.Snapshot()["students"]!.AsArray().Count!=0||core.Snapshot()["alarms"]!.AsArray().Count!=0)throw new InvalidOperationException("Live check requires isolated empty records.");
        await core.HandleAsync("student.save",new JsonObject{["name"]="联调成员"});
        var memberId=core.Snapshot()["students"]![0]!["id"]!.GetValue<string>();
        using var assistant=new AssistantService(directory,AppContext.BaseDirectory,core.BuildAssistantContext,core.GetAssistantConnection,core.CreateAssistantReminders);
        await assistant.HandleAsync("assistant.send",new JsonObject{["text"]="请为联调成员创建一条明天上午9点的提醒，内容为核对实验进展。",["includeReminders"]=true,["includeRecent"]=false});
        var limit=DateTime.UtcNow.AddMinutes(4);
        while(assistant.Snapshot()["busy"]!.GetValue<bool>()&&DateTime.UtcNow<limit)await Task.Delay(250);
        if(assistant.Snapshot()["busy"]!.GetValue<bool>())await assistant.HandleAsync("assistant.cancel",new JsonObject());
        var state=assistant.Snapshot();var alarms=core.Snapshot()["alarms"]!.AsArray();var alarm=alarms.FirstOrDefault()?.AsObject();
        var parsed=DateTimeOffset.TryParse(alarm?["when"]?.GetValue<string>(),out var when);
        var created=state["messages"]!.AsArray().LastOrDefault(x=>x?["role"]?.GetValue<string>()=="assistant");
        var ok=alarms.Count==1&&alarm?["scopeType"]?.GetValue<string>()=="student"&&alarm?["studentId"]?.GetValue<string>()==memberId&&parsed&&when.LocalDateTime.Date==DateTime.Today.AddDays(1)&&when.LocalDateTime.Hour==9&&created?["reminderResults"]?.AsArray().Count==1&&string.IsNullOrEmpty(state["error"]?.GetValue<string>());
        File.WriteAllText(Path.Combine(directory,"reminder-live-result.json"),JsonSerializer.Serialize(new{ok,cliInvocations=1,createdReminders=alarms.Count,answer=created?["text"]?.GetValue<string>(),error=state["error"]?.GetValue<string>()},new JsonSerializerOptions{WriteIndented=true}));
        return ok?0:1;
    }
}
