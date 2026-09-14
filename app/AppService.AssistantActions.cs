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
                return new JsonObject { ["reminders"]=existing.DeepClone(),["replayed"]=true };
            }
            if(payload["reminders"] is not JsonArray input||input.Count is <1 or >8||input.Any(r=>r is not JsonObject))
                throw new InvalidOperationException("一次可以创建 1 至 8 条简单提醒，请重新说明需要的提醒。");
            var normalized=input.OfType<JsonObject>().Select(NormalizeAssistantReminder).ToArray();
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
                state["assistantReminderReceipts"] ??= new JsonArray();
                ((JsonArray)state["assistantReminderReceipts"]!).Add(new JsonObject
                { ["requestId"]=requestId,["createdAt"]=Now(),["reminders"]=created.DeepClone() });
                Save();
                result=new JsonObject { ["reminders"]=created,["replayed"]=false };
            }
            catch {state=previous;throw;}
        }
        Notify();return result;
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
