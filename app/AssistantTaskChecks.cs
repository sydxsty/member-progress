using System.Text.Json;
using System.Text.Json.Nodes;
using StudentProgress.Assistant;

namespace StudentProgress;

internal static class AssistantTaskChecks
{
    public static int RunLive(string directory)
    {
        if(Directory.Exists(directory)&&Directory.EnumerateFileSystemEntries(directory).Any())return 1;
        Directory.CreateDirectory(directory);
        try
        {
            using var core=new AppService(directory);
            core.HandleAsync("student.save",new(){["name"]="彭泽顺"}).GetAwaiter().GetResult();
            var member=core.Snapshot()["students"]![0]!["id"]!.GetValue<string>();
            core.HandleAsync("settings.save",new(){["currentStudentId"]=member}).GetAwaiter().GetResult();
            using var assistant=new AssistantService(directory,AppContext.BaseDirectory,core.BuildAssistantContext,core.GetAssistantConnection,core.CreateAssistantReminders);
            var now=DateTime.Now;var alarmDay=now.Hour<14?now.Date:now.Date.AddDays(1);
            var question=(now.Hour<14?"下午2点叫我":"明天下午2点叫我")+"；明天提醒我去南湖；10月22日-24日提醒我去CNCC";
            var finished=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            assistant.StateChanged+=()=>{if(assistant.Snapshot()["busy"]?.GetValue<bool>()==false)finished.TrySetResult();};
            assistant.HandleAsync("assistant.send",new(){["text"]=question,["includeRecent"]=false}).GetAwaiter().GetResult();
            finished.Task.WaitAsync(TimeSpan.FromMinutes(4)).GetAwaiter().GetResult();
            var state=core.Snapshot();var chat=assistant.Snapshot();
            var alarms=state["alarms"]!.AsArray();var tasks=state["tasks"]!.AsArray();
            var expectedYear=now.Month>10||now.Month==10&&now.Day>24?now.Year+1:now.Year;
            var time=alarms.Count==1?DateTimeOffset.Parse(alarms[0]!["when"]!.GetValue<string>()).LocalDateTime:DateTime.MinValue;
            bool ok=alarms.Count==1&&alarms[0]!["studentId"]?.GetValue<string>()==member&&time.Date==alarmDay&&time.Hour==14&&time.Minute==0&&tasks.Count==2&&
                tasks.All(t=>t!["studentIds"]!.AsArray().Count==1&&t["studentIds"]![0]!.GetValue<string>()==member)&&
                tasks.Any(t=>t!["title"]!.GetValue<string>().Contains("南湖")&&t["start"]!.GetValue<string>()==now.AddDays(1).ToString("yyyy-MM-dd")&&t["due"]!.GetValue<string>()==now.AddDays(1).ToString("yyyy-MM-dd"))&&
                tasks.Any(t=>t!["title"]!.GetValue<string>().Contains("CNCC")&&t["start"]!.GetValue<string>()==$"{expectedYear}-10-22"&&t["due"]!.GetValue<string>()==$"{expectedYear}-10-24")&&
                string.IsNullOrEmpty(chat["error"]?.GetValue<string>());
            File.WriteAllText(Path.Combine(directory,"task-live-result.json"),JsonSerializer.Serialize(new{ok,realModelCalls=1,question,reminders=alarms,tasks,error=chat["error"],answer=chat["messages"]!.AsArray().LastOrDefault()?["text"]}));return ok?0:1;
        }
        catch(Exception e){File.WriteAllText(Path.Combine(directory,"task-live-result.json"),JsonSerializer.Serialize(new{ok=false,error=e.Message}));return 1;}
    }

    public static int Run(string directory)
    {
        var checks=new List<string>();
        if(Directory.Exists(directory)&&Directory.EnumerateFileSystemEntries(directory).Any())return 1;
        Directory.CreateDirectory(directory);
        try
        {
            var data=Path.Combine(directory,"data");Directory.CreateDirectory(data);File.WriteAllText(Path.Combine(data,"initialized"),"offline");
            void Check(bool ok,string name){if(!ok)throw new InvalidOperationException(name);checks.Add(name);}
            void Reject(AppService core,JsonObject request,string name)
            {var before=core.Snapshot().ToJsonString();var rejected=false;try{core.CreateAssistantReminders(request);}catch(InvalidOperationException){rejected=true;}Check(rejected&&core.Snapshot().ToJsonString()==before,name);}
            string member;JsonObject batch;
            using(var core=new AppService(data))
            {
                Check(core.BuildAssistantContext(new())["currentUser"]==null,"no_implicit_global_or_guessed_current_user");
                core.HandleAsync("student.save",new(){["name"]="当前用户联调"}).GetAwaiter().GetResult();
                member=core.Snapshot()["students"]![0]!["id"]!.GetValue<string>();
                core.HandleAsync("settings.save",new(){["currentStudentId"]=member}).GetAwaiter().GetResult();
                Check(core.BuildAssistantContext(new())["currentUser"]?["id"]?.GetValue<string>()==member&&core.MobileSnapshot()["currentUser"]?["id"]?.GetValue<string>()==member,"configured_current_user_shared_with_gpt_and_mobile");
                string Date(int days)=>DateTime.Today.AddDays(days).ToString("yyyy-MM-dd");
                JsonObject Task(string title,int start,int end)=>new(){["title"]=title,["start"]=Date(start),["due"]=Date(end),["studentIds"]=new JsonArray(member)};
                batch=new(){["requestId"]="three-examples",["expectedCurrentStudentId"]=member,
                    ["reminders"]=new JsonArray(new JsonObject{["scopeType"]="student",["studentId"]=member,["title"]="叫我",["when"]=DateTimeOffset.Now.AddHours(1).ToString("O"),["repeat"]="once"}),
                    ["tasks"]=new JsonArray(Task("去南湖",1,1),Task("去CNCC",3,5))};
                var result=core.CreateAssistantReminders(batch);var snapshot=core.Snapshot();
                Check(result["reminders"]!.AsArray().Count==1&&result["tasks"]!.AsArray().Count==2&&snapshot["tasks"]!.AsArray().Count==2,"single_batch_creates_personal_alarm_and_two_tasks");
                Check(snapshot["tasks"]![0]!["start"]!.GetValue<string>()==Date(1)&&snapshot["tasks"]![1]!["due"]!.GetValue<string>()==Date(5)&&snapshot["tasks"]![0]!["done"]!.GetValue<bool>()==false,"date_only_and_range_tasks_reuse_existing_task_schema");
                Check(result["tasks"]![0]!["scopeLabel"]!.GetValue<string>()=="当前用户联调","task_receipt_uses_validated_member_name");
                Check(core.CreateAssistantReminders(batch)["replayed"]!.GetValue<bool>()&&core.Snapshot()["tasks"]!.AsArray().Count==2,"batch_retry_creates_no_duplicates");
                var invalid=batch.DeepClone().AsObject();invalid["requestId"]="invalid";invalid["tasks"]![1]!["due"]=Date(2);
                Reject(core,invalid,"invalid_task_rolls_back_whole_batch_including_alarm");
                var changed=batch.DeepClone().AsObject();changed["requestId"]="changed-user";changed["expectedCurrentStudentId"]="old-user";
                Reject(core,changed,"current_user_change_during_model_call_rejects_whole_batch");
            }
            using(var core=new AppService(data))
            {
                Check(core.BuildAssistantContext(new())["currentUser"]?["id"]?.GetValue<string>()==member&&core.CreateAssistantReminders(batch)["replayed"]!.GetValue<bool>(),"current_user_and_action_receipts_survive_restart");
                var taskId=core.Snapshot()["tasks"]![0]!["id"]!.GetValue<string>();
                core.HandleAsync("task.delete",new(){["id"]=taskId}).GetAwaiter().GetResult();
                Reject(core,batch,"deleted_task_cannot_be_recreated_by_old_request");
                core.HandleAsync("student.delete",new(){["id"]=member}).GetAwaiter().GetResult();
                Check(core.BuildAssistantContext(new())["currentUser"]==null,"deleted_current_user_is_cleared");
            }
            File.WriteAllText(Path.Combine(directory,"task-result.json"),JsonSerializer.Serialize(new{ok=true,checks,realModelCalls=0}));return 0;
        }
        catch(Exception e){File.WriteAllText(Path.Combine(directory,"task-result.json"),JsonSerializer.Serialize(new{ok=false,checks,error=e.ToString()}));return 1;}
    }
}
