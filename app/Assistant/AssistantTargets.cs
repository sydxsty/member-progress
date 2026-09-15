using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace StudentProgress.Assistant;

/// <summary>Keep a model's default target tied to the user's configured member.</summary>
internal static class AssistantTargets
{
    internal static AssistantResponse Resolve(AssistantResponse response,string request,JsonObject context)
    {
        string currentId=AssistantJson.Text(context["currentUser"]?["id"]);
        var students=(context["students"] as JsonArray??[]).OfType<JsonObject>().ToList();
        var projects=(context["projects"] as JsonArray??[]).OfType<JsonObject>().ToList();
        var namedStudents=Named(students,request);
        var namedProjects=Named(projects,request);
        bool global=Regex.IsMatch(request,@"全局提醒|全局闹钟|(?:给|为|创建|新建|添加|设置).{0,8}全局|(?:^|\n)全局(?:[。！!\s]|$)|所有成员|全体成员|所有人|所有学生");
        bool personal=Regex.IsMatch(request,@"提醒我|叫我|(?:给|为)我|我自己|当前用户|当前成员");
        bool defaultPersonal=!global&&namedStudents.Count==0&&namedProjects.Count==0;
        if(defaultPersonal&&currentId.Length==0)
            throw new InvalidOperationException("请先在设置中选择“当前用户”，这样“我”的提醒和任务才会放到你自己的名下。");
        var reminders=response.Reminders.DeepClone().AsArray();
        var tasks=response.Tasks.DeepClone().AsArray();
        foreach(var reminder in reminders.OfType<JsonObject>())
        {
            if(defaultPersonal)
            {
                reminder["scopeType"]="student";reminder["studentId"]=currentId;reminder.Remove("projectId");
                continue;
            }
            string type=AssistantJson.Text(reminder["scopeType"]),id=AssistantJson.Text(reminder[type=="project"?"projectId":"studentId"]);
            if(type=="global"&&global||type=="student"&&(namedStudents.Contains(id)||personal&&id==currentId)||type=="project"&&namedProjects.Contains(id))continue;
            throw WrongTarget();
        }
        foreach(var task in tasks.OfType<JsonObject>())
        {
            if(defaultPersonal)
            {
                task["studentIds"]=new JsonArray(currentId);task.Remove("projectId");continue;
            }
            string project=AssistantJson.Text(task["projectId"]);
            if(project.Length>0)
            {
                if(!namedProjects.Contains(project))throw WrongTarget();
                // The host validates project membership. Its ids can be derived from the
                // explicitly named project without repeating all members in the question.
                continue;
            }
            if(task["studentIds"] is not JsonArray ids||ids.Any(id=>!namedStudents.Contains(AssistantJson.Text(id))&&!(personal&&AssistantJson.Text(id)==currentId)))throw WrongTarget();
        }
        return response with {Reminders=reminders,Tasks=tasks};
    }
    private static HashSet<string> Named(List<JsonObject> records,string request)
    {
        var mentioned=records.Where(r=>AssistantJson.Text(r["name"]) is string name&&name.Length>0&&request.Contains(name,StringComparison.Ordinal)).ToList();
        if(mentioned.GroupBy(r=>AssistantJson.Text(r["name"])).Any(g=>g.Count()>1))
            throw new InvalidOperationException("提到的名称对应多个成员或项目，请先区分名称后再创建。");
        return mentioned.Select(r=>AssistantJson.Text(r["id"])).ToHashSet(StringComparer.Ordinal);
    }
    private static InvalidOperationException WrongTarget()=>new("GPT 给出的对象与这次要求不一致，本次没有创建提醒或任务，请重新说明对象。");
}
