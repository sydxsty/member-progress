using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace StudentProgress.Assistant;

/// <summary>Legacy offline fixture parser and current host receipt readers. Production replies are plain native Codex messages.</summary>
internal sealed record AssistantResponse(string Text, JsonArray Reminders, JsonArray Tasks, bool NeedsReminderDetails = false)
{
    // Used only by the injected offline responseProvider fixtures, never by the live Codex path.
    internal static AssistantResponse Parse(string output)
    {
        string json=output.Trim();
        if(json.StartsWith("```json",StringComparison.Ordinal)&&json.EndsWith("```",StringComparison.Ordinal))
            json=json[7..^3].Trim();
        if(json.Length>60_000)throw Invalid();
        try
        {
            using var document=JsonDocument.Parse(json);
            CheckKeys(document.RootElement,["text","reminders","tasks","needsReminderDetails"]);
            var root=document.RootElement;
            if(!root.TryGetProperty("text",out var text)||text.ValueKind!=JsonValueKind.String||
                !root.TryGetProperty("reminders",out var list)||list.ValueKind!=JsonValueKind.Array||list.GetArrayLength()>8)
                throw Invalid();
            bool hasTasks=root.TryGetProperty("tasks",out var taskList);
            if(hasTasks&&(taskList.ValueKind!=JsonValueKind.Array||taskList.GetArrayLength()+list.GetArrayLength()>8))throw Invalid();
            int taskCount=hasTasks?taskList.GetArrayLength():0;
            bool needsDetails=false;
            if(root.TryGetProperty("needsReminderDetails",out var details))
            {
                if(details.ValueKind is not (JsonValueKind.True or JsonValueKind.False))throw Invalid();
                needsDetails=details.GetBoolean();
            }
            if(needsDetails&&(list.GetArrayLength()!=0||taskCount!=0))throw Invalid();
            string reply=text.GetString()!.Trim();
            if(reply.Length>40_000||reply.Length==0&&list.GetArrayLength()==0&&taskCount==0)throw Invalid();
            var reminders=new JsonArray();
            foreach(var item in list.EnumerateArray())
            {
                CheckKeys(item,["scopeType","studentId","projectId","title","when","repeat","intervalDays","monthDay"]);
                string type=Required(item,"scopeType"),title=Required(item,"title"),when=Required(item,"when"),repeat=Required(item,"repeat");
                if(type is not ("global" or "student" or "project")||title.Length>160||
                    repeat is not ("once" or "daily" or "weekly" or "monthly" or "interval")||
                    !Regex.IsMatch(when,@"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}(?::\d{2}(?:\.\d{1,7})?)?(?:Z|[+-]\d{2}:\d{2})$")||
                    !DateTimeOffset.TryParse(when,CultureInfo.InvariantCulture,DateTimeStyles.None,out _))throw Invalid();
                var reminder=new JsonObject{["scopeType"]=type,["title"]=title,["when"]=when,["repeat"]=repeat};
                string student=Optional(item,"studentId"),project=Optional(item,"projectId");
                if(type=="student"&&(student.Length==0||project.Length!=0)||
                    type=="project"&&(project.Length==0||student.Length!=0)||
                    type=="global"&&(student.Length!=0||project.Length!=0)||student.Length>100||project.Length>100)throw Invalid();
                if(type=="student")reminder["studentId"]=student;
                if(type=="project")reminder["projectId"]=project;
                if(repeat=="interval")
                {
                    if(!item.TryGetProperty("intervalDays",out var interval)||!interval.TryGetInt32(out int days)||days<1||days>3650)throw Invalid();
                    reminder["intervalDays"]=days;
                }
                else if(item.TryGetProperty("intervalDays",out _))throw Invalid();
                if(item.TryGetProperty("monthDay",out var monthDay))
                {
                    if(repeat!="monthly"||!monthDay.TryGetInt32(out int day)||day<1||day>31)throw Invalid();
                    reminder["monthDay"]=day;
                }
                reminders.Add(reminder);
            }
            var tasks=new JsonArray();
            if(hasTasks)foreach(var item in taskList.EnumerateArray())
            {
                CheckKeys(item,["title","start","due","studentIds","projectId"]);
                string title=Required(item,"title"),start=Required(item,"start"),due=Required(item,"due"),project=Optional(item,"projectId");
                if(title.Length>160||project.Length>100||!IsoDate(start,out var startDate)||!IsoDate(due,out var dueDate)||dueDate<startDate||
                    !item.TryGetProperty("studentIds",out var members)||members.ValueKind!=JsonValueKind.Array||members.GetArrayLength()>100)throw Invalid();
                var ids=new JsonArray();var seen=new HashSet<string>(StringComparer.Ordinal);
                foreach(var member in members.EnumerateArray())
                {
                    if(member.ValueKind!=JsonValueKind.String||member.GetString() is not string id||string.IsNullOrWhiteSpace(id)||id.Length>100||!seen.Add(id))throw Invalid();
                    ids.Add(id);
                }
                if(ids.Count==0&&project.Length==0)throw Invalid();
                var task=new JsonObject{["title"]=title,["start"]=start,["due"]=due,["studentIds"]=ids};
                if(project.Length>0)task["projectId"]=project;
                tasks.Add(task);
            }
            return new(reply,reminders,tasks,needsDetails);
        }
        catch(Exception e) when(e is JsonException or InvalidOperationException or FormatException)
        {throw Invalid();}
    }

    internal static List<AssistantReminderResult> ReadReceipt(JsonObject receipt)
    {
        if(receipt["reminders"] is not JsonArray records||records.Count>8)
            throw new InvalidOperationException("提醒操作没有返回完整结果，请查看提醒列表。");
        var results=new List<AssistantReminderResult>();
        foreach(var node in records)
        {
            if(node is not JsonObject r||AssistantJson.Text(r["id"]).Length==0)throw new InvalidOperationException("提醒操作没有返回完整结果，请查看提醒列表。");
            results.Add(new(){Id=AssistantJson.Text(r["id"]),Title=AssistantJson.Text(r["title"]),When=AssistantJson.Text(r["when"]),ScopeType=AssistantJson.Text(r["scopeType"]),StudentId=AssistantJson.Text(r["studentId"]),ProjectId=AssistantJson.Text(r["projectId"]),ScopeLabel=AssistantJson.Text(r["scopeLabel"]),Repeat=AssistantJson.Text(r["repeat"]),MonthDay=r["monthDay"] is JsonValue day&&day.TryGetValue<int>(out int value)?value:null});
        }
        return results;
    }
    internal static List<AssistantTaskResult> ReadTaskReceipt(JsonObject receipt)
    {
        if(receipt["tasks"]==null)return []; // Existing reminder receipts remain readable.
        if(receipt["tasks"] is not JsonArray records||records.Count>8)throw new InvalidOperationException("任务操作没有返回完整结果，请查看任务列表。");
        var results=new List<AssistantTaskResult>();
        foreach(var node in records)
        {
            if(node is not JsonObject r||AssistantJson.Text(r["id"]).Length==0||r["studentIds"] is not JsonArray ids)
                throw new InvalidOperationException("任务操作没有返回完整结果，请查看任务列表。");
            results.Add(new(){Id=AssistantJson.Text(r["id"]),Title=AssistantJson.Text(r["title"]),Start=AssistantJson.Text(r["start"]),Due=AssistantJson.Text(r["due"]),
                StudentIds=ids.Select(AssistantJson.Text).ToList(),ProjectId=AssistantJson.Text(r["projectId"]),ScopeLabel=AssistantJson.Text(r["scopeLabel"])});
        }
        return results;
    }
    private static bool IsoDate(string value,out DateOnly date)=>DateOnly.TryParseExact(value,"yyyy-MM-dd",CultureInfo.InvariantCulture,DateTimeStyles.None,out date);
    private static void CheckKeys(JsonElement item,string[] allowed)
    {
        if(item.ValueKind!=JsonValueKind.Object)throw Invalid();
        var seen=new HashSet<string>(StringComparer.Ordinal);
        foreach(var property in item.EnumerateObject())if(!allowed.Contains(property.Name)||!seen.Add(property.Name))throw Invalid();
    }
    private static string Required(JsonElement item,string key)
    {
        string result=Optional(item,key).Trim();
        return result.Length==0?throw Invalid():result;
    }
    private static string Optional(JsonElement item,string key)
    {
        if(!item.TryGetProperty(key,out var value))return "";
        if(value.ValueKind!=JsonValueKind.String)throw Invalid();
        return value.GetString()??"";
    }
    private static InvalidOperationException Invalid()=>new("GPT 返回的回答格式不完整，本次未创建提醒或任务。问题已保留，可以重试。");
}
