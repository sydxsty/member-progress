using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace StudentProgress;

public sealed partial class AppService
{
    // Only the in-process assistant receives this delegate. It is not a general web RPC.
    public JsonObject CreateAssistantReminders(JsonObject payload)
    {
        JsonObject result;
        lock(gate)
        {
            ObjectDisposedException.ThrowIf(disposed,this);
            var requestId=Required(payload,"requestId",100);
            var receipts=state["assistantReminderReceipts"] as JsonArray;
            var saved=receipts?.OfType<JsonObject>().FirstOrDefault(r=>S(r,"requestId")==requestId);
            if(saved!=null)
            {
                var existing=(JsonArray)saved["reminders"]!;
                if(existing.OfType<JsonObject>().Any(r=>!Rows("alarms").Any(a=>S(a,"id")==S(r,"id"))))
                    throw new InvalidOperationException("这次请求创建的提醒已被删除，不会重复创建。需要重新添加时，请发送新的创建请求。");
                var existingTasks=saved["tasks"] as JsonArray ?? new();
                if(existingTasks.OfType<JsonObject>().Any(t=>!Rows("tasks").Any(a=>S(a,"id")==S(t,"id"))))
                    throw new InvalidOperationException("这次请求创建的任务已被删除，不会重复创建。需要重新添加时，请发送新的创建请求。");
                return new JsonObject { ["reminders"]=existing.DeepClone(),["tasks"]=existingTasks.DeepClone(),["replayed"]=true };
            }
            if(payload.ContainsKey("expectedCurrentStudentId")&&S(payload,"expectedCurrentStudentId")!=S((JsonObject)state["settings"]!,"currentStudentId"))
                throw new InvalidOperationException("当前用户已变更，本次没有创建。请按新的当前用户重新发送请求。");
            if(payload["reminders"] is not JsonArray input||input.Any(r=>r is not JsonObject)||
                payload["tasks"] is not null and not JsonArray)
                throw new InvalidOperationException("任务或提醒格式无效，本次没有创建。");
            var taskInput=payload["tasks"] as JsonArray ?? new();
            if(input.Count+taskInput.Count is <1 or >8||taskInput.Any(t=>t is not JsonObject))
                throw new InvalidOperationException("一次可以创建合计 1 至 8 条任务或提醒。");
            var normalized=input.OfType<JsonObject>().Select(NormalizeAssistantReminder).ToArray();
            var normalizedTasks=taskInput.OfType<JsonObject>().Select(NormalizeAssistantTask).ToArray();
            var previous=(JsonObject)state.DeepClone();
            try
            {
                var created=new JsonArray();
                foreach(var reminder in normalized)
                {
                    SaveAlarm(reminder);
                    var alarm=Rows("alarms").Last();
                    if(S(alarm,"repeat")=="monthly"&&reminder["monthDay"]!=null)alarm["monthDay"]=I(reminder,"monthDay");
                    created.Add(AssistantReminderResult(alarm));
                }
                var createdTasks=new JsonArray();
                foreach(var task in normalizedTasks)
                {
                    AddTask(task);
                    var savedTask=Rows("tasks").Last();
                    savedTask["studentIds"]=task["studentIds"]!.DeepClone();
                    var record=AssistantFields(savedTask,"id","title","start","due","studentIds","projectId");
                    record["scopeLabel"]=S(task,"scopeType")=="project"?"项目 · "+S(Find("projects",S(task,"scopeId")),"name"):
                        string.Join("、",((JsonArray)task["studentIds"]!).Select(id=>S(Find("students",id!.GetValue<string>()),"name")));
                    createdTasks.Add(record);
                }
                state["assistantReminderReceipts"] ??= new JsonArray();
                ((JsonArray)state["assistantReminderReceipts"]!).Add(new JsonObject
                { ["requestId"]=requestId,["createdAt"]=Now(),["reminders"]=created.DeepClone(),["tasks"]=createdTasks.DeepClone() });
                Save();
                result=new JsonObject { ["reminders"]=created,["tasks"]=createdTasks,["replayed"]=false };
            }
            catch {state=previous;throw;}
        }
        Notify();return result;
    }

    JsonObject NormalizeAssistantTask(JsonObject input)
    {
        var title=Required(input,"title",500);
        var start=Required(input,"start",10);var due=Required(input,"due",10);
        if(!DateOnly.TryParseExact(start,"yyyy-MM-dd",CultureInfo.InvariantCulture,DateTimeStyles.None,out var first)||
            !DateOnly.TryParseExact(due,"yyyy-MM-dd",CultureInfo.InvariantCulture,DateTimeStyles.None,out var last)||last<first)
            throw new InvalidOperationException("任务日期范围无效，本次没有创建。");
        if(last<DateOnly.FromDateTime(DateTime.Now))throw new InvalidOperationException("任务日期已经过去，请重新指定日期。");
        if(input["studentIds"] is not JsonArray ids||ids.Any(id=>id is not JsonValue v||!v.TryGetValue<string>(out _)))
            throw new InvalidOperationException("任务的成员无法识别，请重新说明。");
        var members=Strings(ids);ValidateMembers(members);
        var project=S(input,"projectId");
        if(project.Length>0)
        {
            var projectMembers=Strings(Find("projects",project)["studentIds"]);
            if(members.Count==0)members=projectMembers;
            else if(members.Any(id=>!projectMembers.Any(p=>p!.GetValue<string>()==id!.GetValue<string>())))
                throw new InvalidOperationException("任务成员不属于指定项目，请重新说明。");
            ValidateMembers(members);
        }
        else if(members.Count==0)throw new InvalidOperationException("请先在设置中选择当前用户，或指定任务成员。");
        return new JsonObject { ["title"]=title,["start"]=start,["due"]=due,["studentIds"]=members,
            ["scopeType"]=project.Length>0?"project":"student",["scopeId"]=project.Length>0?project:members[0]!.GetValue<string>() };
    }

    JsonObject NormalizeAssistantReminder(JsonObject input)
    {
        var type=S(input,"scopeType","global");
        if(type is not ("global" or "student" or "project"))throw new InvalidOperationException("提醒对象无法识别，请说明是全局、成员还是项目。");
        var title=Required(input,"title",500);
        var whenText=Required(input,"when",100);
        if(!Regex.IsMatch(whenText,@"(?:Z|[+-]\d{2}:\d{2})$",RegexOptions.CultureInvariant)||
            !DateTimeOffset.TryParse(whenText,CultureInfo.InvariantCulture,DateTimeStyles.None,out var when))
            throw new InvalidOperationException("提醒时间无法识别，请说明日期和时刻。");
        if(when<DateTimeOffset.Now.AddMinutes(-1))throw new InvalidOperationException("提醒时间已经过去，请重新指定时间。本次没有创建提醒。");
        var repeat=S(input,"repeat","once");
        if(repeat is not ("once" or "daily" or "weekly" or "monthly" or "interval"))
            throw new InvalidOperationException("暂时无法识别这个重复规则，请用每天、每周或每月等简单安排。");
        var normalized=new JsonObject { ["scopeType"]=type,["title"]=title,["when"]=when.ToString("O"),["repeat"]=repeat,["enabled"]=true };
        if(type=="student") {var id=Required(input,"studentId");Find("students",id);normalized["studentId"]=id;}
        if(type=="project") {var id=Required(input,"projectId");Find("projects",id);normalized["projectId"]=id;}
        if(repeat=="interval")
        {
            var days=I(input,"intervalDays");
            if(days is <1 or >3650)throw new InvalidOperationException("提醒间隔需要是 1 至 3650 天。");
            normalized["intervalDays"]=days;
        }
        if(repeat=="monthly"&&input["monthDay"]!=null)
        {
            var anchor=I(input,"monthDay");var local=when.LocalDateTime;
            if(anchor is <1 or >31||local.Day!=Math.Min(anchor,DateTime.DaysInMonth(local.Year,local.Month)))
                throw new InvalidOperationException("每月的提醒日与首次提醒日期不一致，请重新说明。");
            normalized["monthDay"]=anchor;
        }
        return normalized;
    }

    JsonObject AssistantReminderResult(JsonObject alarm)
    {
        var result=AssistantFields(alarm,"id","title","when","scopeType","studentId","projectId","repeat","intervalDays","monthDay");
        result["scopeLabel"]=S(alarm,"scopeType") switch
        {
            "student"=>S(Find("students",S(alarm,"studentId")),"name"),
            "project"=>"项目 · "+S(Find("projects",S(alarm,"projectId")),"name"),
            _=>"全局"
        };
        return result;
    }
}
