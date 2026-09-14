using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace StudentProgress.Assistant;

/// <summary>The model proposes an action; the host alone validates and commits it.</summary>
internal sealed record AssistantResponse(string Text, JsonArray Reminders, bool NeedsReminderDetails = false)
{
    internal static AssistantResponse Parse(string output)
    {
        string json=output.Trim();
        if(json.StartsWith("```json",StringComparison.Ordinal)&&json.EndsWith("```",StringComparison.Ordinal))
            json=json[7..^3].Trim();
        if(json.Length>60_000)throw Invalid();
        try
        {
            using var document=JsonDocument.Parse(json);
            CheckKeys(document.RootElement,["text","reminders","needsReminderDetails"]);
            var root=document.RootElement;
            if(!root.TryGetProperty("text",out var text)||text.ValueKind!=JsonValueKind.String||
                !root.TryGetProperty("reminders",out var list)||list.ValueKind!=JsonValueKind.Array||list.GetArrayLength()>8)
                throw Invalid();
            bool needsDetails=false;
            if(root.TryGetProperty("needsReminderDetails",out var details))
            {
                if(details.ValueKind is not (JsonValueKind.True or JsonValueKind.False))throw Invalid();
                needsDetails=details.GetBoolean();
            }
            if(needsDetails&&list.GetArrayLength()!=0)throw Invalid();
            string reply=text.GetString()!.Trim();
            if(reply.Length>40_000||reply.Length==0&&list.GetArrayLength()==0)throw Invalid();
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
            return new(reply,reminders,needsDetails);
        }
        catch(Exception e) when(e is JsonException or InvalidOperationException or FormatException)
        {throw Invalid();}
    }

    internal static bool HasExplicitCreationRequest(string question)
    {
        // Quoted examples and pasted blocks cannot independently authorize a write.
        string current=Regex.Replace(question,@"```[\s\S]*?```|[“\""「『][^”\""」』]*[”\""」』]","");
        if(Regex.IsMatch(current,@"(?:不要|别|不必|无需|不需要|勿|暂不|先不).{0,24}(?:创建|新建|新增|添加|设置|设定|安排|提醒|闹钟)"))return false;
        if(Regex.IsMatch(current,@"^\s*(?:请|帮我)?(?:分析|总结|翻译|解释|比较|评估|推荐|建议|举例|例如|比如|如何|怎么|怎样)")&&
            !Regex.IsMatch(current,@"(?:并|然后|同时|再)(?:请|帮我)?(?:创建|新建|添加|设置)"))return false;
        return Regex.IsMatch(current,@"(?:创建|新建|新增|添加|设置|设定|设个|加个|加一[条个]|安排).{0,40}(?:提醒|闹钟)|(?:请|帮我|麻烦).{0,30}提醒|提醒(?:我|我们)|(?:今天|明天|后天|每[天周月]|下周|[0-9一二三四五六七八九十]+[点时日号]).{0,40}提醒");
    }
    internal static bool IsReminderDetailReply(string question,JsonObject activeContext)
    {
        string value=question.Trim().TrimEnd('。','！','!');
        if(value.Length==0||value.Length>80||value.Contains('\n')||value.Contains('\r'))return false;
        if(value is "全局" or "全局提醒" or "所有成员")return true;
        foreach(string key in new[]{"students","projects"})
            if(activeContext[key] is JsonArray records&&records.OfType<JsonObject>().Any(r=>AssistantJson.Text(r["name"])==value))return true;
        // Entire reply must consist of date/time/recurrence words. Advice or a pasted
        // instruction with a time somewhere inside cannot continue a pending action.
        string rest=Regex.Replace(value,@"(?:下个月|这个月|每个月|今天|明天|后天|大后天|今晚|明早|明晚|上午|下午|晚上|早上|中午|凌晨|傍晚|本周|下周|这周|本月|下月|每天|每周|每月|每年|每隔|星期|礼拜|时间是|定在|就定|就是|设置为|改成|改到|[每隔周月年天日号点时分秒刻半整一二三四五六七八九十百两零〇是吧]|[0-9０-９:TtZz+\-/.：，,\s])","");
        return rest.Length==0&&Regex.IsMatch(value,@"[0-9０-９一二三四五六七八九十两]|今天|明天|后天|明早|明晚|今晚|下周|本周|下月|本月|上午|下午|晚上|早上|中午|凌晨|傍晚|每天|每周|每月|每年");
    }
    internal static string SafeAdvice(string text)
    {
        if(Regex.IsMatch(text,@"(?:已|已经|成功).{0,8}(?:创建|添加|设置|设定|安排|删除|修改|清空|更新).{0,10}(?:提醒|闹钟)|(?:提醒|闹钟).{0,8}(?:已创建|已添加|已设置|已修改|已删除|设置好了|创建好了)"))
            return "本次未创建或修改提醒。需要创建时，请直接告诉我提醒事项和日期时间。";
        return text;
    }
    internal static List<AssistantReminderResult> ReadReceipt(JsonObject receipt)
    {
        if(receipt["reminders"] is not JsonArray records||records.Count==0||records.Count>8)
            throw new InvalidOperationException("提醒操作没有返回完整结果，请查看提醒列表。");
        var results=new List<AssistantReminderResult>();
        foreach(var node in records)
        {
            if(node is not JsonObject r||AssistantJson.Text(r["id"]).Length==0)throw new InvalidOperationException("提醒操作没有返回完整结果，请查看提醒列表。");
            results.Add(new(){Id=AssistantJson.Text(r["id"]),Title=AssistantJson.Text(r["title"]),When=AssistantJson.Text(r["when"]),ScopeType=AssistantJson.Text(r["scopeType"]),StudentId=AssistantJson.Text(r["studentId"]),ProjectId=AssistantJson.Text(r["projectId"]),ScopeLabel=AssistantJson.Text(r["scopeLabel"]),Repeat=AssistantJson.Text(r["repeat"]),MonthDay=r["monthDay"] is JsonValue day&&day.TryGetValue<int>(out int value)?value:null});
        }
        return results;
    }
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
    private static InvalidOperationException Invalid()=>new("GPT 返回的回答格式不完整，本次未创建提醒。问题已保留，可以重试。");
}
