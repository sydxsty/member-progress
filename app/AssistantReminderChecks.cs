using System.Text.Json;
using System.Text.Json.Nodes;

namespace StudentProgress;

internal static class AssistantReminderChecks
{
    public static int Run(string root)
    {
        var checks=new List<string>();
        if(Directory.Exists(root)&&Directory.EnumerateFileSystemEntries(root).Any())return 1;
        Directory.CreateDirectory(root);
        try
        {
            var data=Path.Combine(root,"isolated-data");Directory.CreateDirectory(data);File.WriteAllText(Path.Combine(data,"initialized"),"offline");
            var when=DateTimeOffset.Now.AddDays(2).ToString("O");
            JsonObject Alarm(string type="global",string? id=null)=>new(){["scopeType"]=type,["studentId"]=type=="student"?id:null,["projectId"]=type=="project"?id:null,["title"]="核对进展",["when"]=when,["repeat"]="once"};
            JsonObject Request(string id,params JsonObject[] reminders)=>new(){["requestId"]=id,["reminders"]=new JsonArray(reminders.Select(x=>(JsonNode)x).ToArray())};
            void Check(bool ok,string label){if(!ok)throw new InvalidOperationException(label);checks.Add(label);}
            void Reject(AppService core,JsonObject request,string label)
            {var before=core.Snapshot().ToJsonString();var rejected=false;try{core.CreateAssistantReminders(request);}catch(InvalidOperationException){rejected=true;}Check(rejected&&core.Snapshot().ToJsonString()==before,label);}
            string memberId,projectId;JsonObject replay;
            using(var core=new AppService(data))
            {
                core.HandleAsync("student.save",new JsonObject{["name"]="离线联调成员",["tags"]=new JsonArray("联培")}).GetAwaiter().GetResult();
                memberId=core.Snapshot()["students"]![0]!["id"]!.GetValue<string>();
                core.HandleAsync("project.save",new JsonObject{["name"]="离线联调项目",["studentIds"]=new JsonArray(memberId)}).GetAwaiter().GetResult();
                projectId=core.Snapshot()["projects"]![0]!["id"]!.GetValue<string>();
                var originalMembers=core.Snapshot()["students"]!.ToJsonString();
                replay=Request("request-1",Alarm(),Alarm("student",memberId),Alarm("project",projectId));
                var result=core.CreateAssistantReminders(replay);
                Check(result["reminders"]!.AsArray().Count==3&&core.Snapshot()["alarms"]!.AsArray().Count==3,"one_batch_creates_global_member_and_project_reminders");
                Check(result["reminders"]![1]!["scopeLabel"]!.GetValue<string>()=="离线联调成员"&&result["reminders"]![2]!["scopeLabel"]!.GetValue<string>().Contains("离线联调项目"),"creation_receipts_use_validated_target_names");
                Check(core.CreateAssistantReminders(replay)["replayed"]!.GetValue<bool>()&&core.Snapshot()["alarms"]!.AsArray().Count==3,"same_request_is_idempotent");
                Check(!core.Snapshot().ContainsKey("assistantReminderReceipts")&&core.Snapshot()["students"]!.ToJsonString()==originalMembers,"internal_receipts_hidden_and_member_fields_unchanged");
                Reject(core,Request("invalid-member",Alarm(),Alarm("student","missing")),"invalid_member_rejects_entire_batch_without_partial_create");
                var past=Alarm();past["when"]=DateTimeOffset.Now.AddDays(-1).ToString("O");Reject(core,Request("past",past),"past_time_rejected_without_mutation");
                var ambiguous=Alarm();ambiguous["when"]=DateTime.Now.AddDays(1).ToString("yyyy-MM-ddTHH:mm:ss");Reject(core,Request("no-offset",ambiguous),"ambiguous_timestamp_rejected");
                var wrong=Alarm();wrong["repeat"]="unknown";Reject(core,Request("unknown-repeat",wrong),"unsupported_repeat_rejected");
                var day=new DateTime(DateTime.Now.Year+1,2,28,9,0,0);var month=Alarm();month["repeat"]="monthly";month["when"]=new DateTimeOffset(day).ToString("O");month["monthDay"]=31;
                if(DateTime.DaysInMonth(day.Year,2)==29)month["when"]=new DateTimeOffset(day.AddDays(1)).ToString("O");
                var monthly=core.CreateAssistantReminders(Request("monthly",month));Check(monthly["reminders"]![0]!["monthDay"]!.GetValue<int>()==31,"monthly_short_month_keeps_original_31_anchor");
                var overwrite=Alarm();overwrite["id"]=result["reminders"]![0]!["id"]!.GetValue<string>();overwrite["enabled"]=false;
                core.CreateAssistantReminders(Request("creation-only",overwrite));Check(core.Snapshot()["alarms"]!.AsArray().Count==5&&core.Snapshot()["alarms"]![0]!["enabled"]!.GetValue<bool>(),"payload_cannot_update_or_disable_existing_reminder");
            }
            using(var reopened=new AppService(data))
            {
                Check(reopened.CreateAssistantReminders(replay)["replayed"]!.GetValue<bool>()&&reopened.Snapshot()["alarms"]!.AsArray().Count==5,"idempotency_survives_restart");
                var id=reopened.Snapshot()["alarms"]![0]!["id"]!.GetValue<string>();reopened.HandleAsync("alarm.delete",new JsonObject{["id"]=id}).GetAwaiter().GetResult();
                Reject(reopened,replay,"deleted_creation_not_recreated_by_retry");
            }
            File.WriteAllText(Path.Combine(root,"reminder-result.json"),JsonSerializer.Serialize(new{ok=true,checks,liveModelCalls=0},new JsonSerializerOptions{WriteIndented=true}));return 0;
        }
        catch(Exception e){File.WriteAllText(Path.Combine(root,"reminder-result.json"),JsonSerializer.Serialize(new{ok=false,checks,error=e.Message}));return 1;}
    }
}
