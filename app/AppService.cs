using System.IO.Compression;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace StudentProgress;

public sealed partial class AppService : IDisposable
{
    readonly object gate = new();
    readonly string root;
    JsonObject state;
    bool disposed;
    readonly Dictionary<string, CancellationTokenSource> pending = new();
    readonly Dictionary<string, long> revisions = new();
    readonly HttpClient client = new() { Timeout = TimeSpan.FromSeconds(90) };
    public event Action? StateChanged;

    public AppService(string dataRoot)
    {
        root = Path.GetFullPath(dataRoot);
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(Path.Combine(root, "attachments"));
        state = ReadState();
        var gradesChanged = AdvanceStudentGrades(state, DateTimeOffset.Now);
        foreach (var scope in Rows("students").Concat(Rows("projects")))
            if (S(scope,"summaryStatus") is "running" or "pending")
            { scope["summaryStatus"] = "error"; scope["summaryError"] = "上次摘要未完成，可点击重试。"; }
        if (!File.Exists(Path.Combine(root, "initialized")))
        {
            try { DetectSettings(); } catch { /* Optional local configuration must not prevent startup. */ }
            Save();
            File.WriteAllText(Path.Combine(root,"initialized"), "1");
        }
        else if(gradesChanged) Save();
    }

    static JsonObject EmptyState() => new()
    {
        ["schemaVersion"] = 1,
        ["students"] = new JsonArray(), ["projects"] = new JsonArray(), ["tasks"] = new JsonArray(),
        ["archivedStudents"] = new JsonArray(), ["deletedTasks"] = new JsonArray(),
        ["messages"] = new JsonArray(), ["deletedMessages"] = new JsonArray(), ["alarms"] = new JsonArray(),
        ["preferences"] = new JsonObject { ["recentStudentLabels"]=new JsonArray(), ["recentGlobalAlarmLabels"]=new JsonArray() },
        ["settings"] = new JsonObject { ["baseUrl"]="https://api.openai.com/v1", ["model"]="", ["apiMode"]="responses" },
        ["status"] = new JsonObject { ["notificationStatus"]="等待 Windows 通知服务初始化" }
    };

    JsonObject ReadState()
    {
        var path = Path.Combine(root,"state.json");
        if (!File.Exists(path)) return EmptyState();
        try { return ParseState(File.ReadAllText(path)); }
        catch
        {
            var recovery = Path.Combine(root,"state.previous.json");
            if (!File.Exists(recovery)) throw new InvalidOperationException("本地数据无法读取。请保留 Data 文件夹并从备份恢复。");
            try
            {
                var recovered = ParseState(File.ReadAllText(recovery));
                File.Copy(path, Path.Combine(root,$"state.unreadable-{DateTime.Now:yyyyMMddHHmmss}.json"),true);
                return recovered;
            }
            catch { throw new InvalidOperationException("本地数据和恢复副本均无法读取。请保留 Data 文件夹并从备份恢复。"); }
        }
    }

    static JsonObject ParseState(string text)
    {
        var s = JsonNode.Parse(text, documentOptions:new JsonDocumentOptions { MaxDepth=40 }) as JsonObject
            ?? throw new InvalidDataException("数据格式无效。");
        if (I(s,"schemaVersion",1)!=1) throw new InvalidDataException("此备份版本暂不支持。");
        foreach (var key in new[]{"students","projects","tasks","messages","alarms"})
            if (s[key] is not JsonArray a || a.Any(x=>x is not JsonObject)) throw new InvalidDataException("备份缺少有效数据列表。");
        if(s["assistantReminderReceipts"] is JsonNode receipts)
        {
            if(receipts is not JsonArray items||items.Any(x=>x is not JsonObject r||S(r,"requestId").Length==0||r["reminders"] is not JsonArray reminders||reminders.Any(y=>y is not JsonObject alarm||S(alarm,"id").Length==0)))
                throw new InvalidDataException("聊天提醒的创建记录格式无效。");
            if(items.OfType<JsonObject>().Any(r=>r["tasks"] is JsonNode tasks&&(tasks is not JsonArray taskItems||taskItems.Any(y=>y is not JsonObject task||S(task,"id").Length==0))))
                throw new InvalidDataException("聊天任务的创建记录格式无效。");
        }
        if(s["mobileMutationReceipts"] is JsonNode mobileReceipts &&
            (mobileReceipts is not JsonArray mobileItems || mobileItems.Any(x=>x is not JsonObject r||S(r,"requestId").Length==0||S(r,"fingerprint").Length!=64)))
            throw new InvalidDataException("手机修改的保存记录格式无效。");
        foreach (var key in new[]{"archivedStudents","deletedTasks","deletedMessages"})
        {
            s[key] ??= new JsonArray();
            if (s[key] is not JsonArray a || a.Any(x=>x is not JsonObject)) throw new InvalidDataException("备份包含无效的历史列表。");
        }
        s["preferences"] ??= new JsonObject();
        if(s["preferences"] is not JsonObject preferences) throw new InvalidDataException("最近标签格式无效。");
        foreach(var key in new[]{"recentStudentLabels","recentGlobalAlarmLabels"})
            if(preferences[key]!=null && (preferences[key] is not JsonArray labels || labels.Any(x=>x is not JsonValue v || !v.TryGetValue<string>(out _))))throw new InvalidDataException("最近标签格式无效。");
        var students=((JsonArray)s["students"]!).OfType<JsonObject>();
        var alarms=((JsonArray)s["alarms"]!).OfType<JsonObject>();
        foreach(var alarm in alarms)NormalizeMonthlyAlarm(alarm);
        preferences["recentStudentLabels"]=RecentLabels(preferences["recentStudentLabels"] ?? new JsonArray(students.Reverse().SelectMany(x=>(x["tags"] as JsonArray ?? new()).Select(y=>y?.DeepClone())).ToArray()),8);
        preferences["recentGlobalAlarmLabels"]=RecentLabels(preferences["recentGlobalAlarmLabels"] ?? new JsonArray(alarms.Reverse().Where(x=>S(x,"scopeType","global")=="global").Select(x=>JsonValue.Create(S(x,"title"))).ToArray()),6);
        s["settings"] ??= EmptyState()["settings"]!.DeepClone();
        s["status"] ??= EmptyState()["status"]!.DeepClone();
        if(s["settings"] is not JsonObject || s["status"] is not JsonObject) throw new InvalidDataException("配置格式无效。");
        if(((JsonArray)s["deletedMessages"]!).OfType<JsonObject>().Any(x=>x["message"] is not JsonObject m||S(x,"id").Length==0||S(x,"id")!=S(m,"id")))
            throw new InvalidDataException("已删除的归档记录格式无效。");
        foreach(var msg in AllArchiveMessages(s))
            foreach(var f in (msg["attachments"] as JsonArray ?? new()).OfType<JsonObject>())
                if(!SafeStoredName(S(f,"storedName"))) throw new InvalidDataException("附件路径无效。");
        return s;
    }

    void Save()
    {
        var path = Path.Combine(root,"state.json");
        var temp = Path.Combine(root,"state.tmp");
        var bytes = Encoding.UTF8.GetBytes(state.ToJsonString(new JsonSerializerOptions { WriteIndented=true }));
        using (var f = new FileStream(temp,FileMode.Create,FileAccess.Write,FileShare.None)) { f.Write(bytes); f.Flush(true); }
        if(File.Exists(path)) File.Replace(temp,path,Path.Combine(root,"state.previous.json"),true);
        else File.Move(temp,path);
    }

    public JsonObject Snapshot()
    {
        lock(gate)
        {
            var view = (JsonObject)state.DeepClone();
            view.Remove("deletedTasks");
            view.Remove("deletedMessages");
            view.Remove("assistantReminderReceipts");
            view.Remove("mobileMutationReceipts");
            var settings = (JsonObject)view["settings"]!;
            settings["aiConfigured"] = IsAiConfigured();
            settings["dataRoot"] = root;
            settings.Remove("key"); settings.Remove("apiKey");
            foreach(var msg in ((JsonArray)view["messages"]!).OfType<JsonObject>())
                foreach(var f in (msg["attachments"] as JsonArray ?? new()).OfType<JsonObject>())
                { f.Remove("storedName"); f.Remove("extractedText"); }
            return view;
        }
    }

    public Task<JsonNode?> HandleAsync(string action, JsonObject payload)
    {
        JsonNode? result;
        string? scheduleType=null, scheduleId=null, deletedStudentId=null, manuallyEditedSummaryId=null;
        (string Type,string Id)? changedArchiveScope=null;
        bool changed=true, alreadyPersisted=false;
        lock(gate)
        {
            ObjectDisposedException.ThrowIf(disposed,this);
            if(action=="app.get") return Task.FromResult<JsonNode?>(Snapshot());
            if(action=="backup.export") return Task.FromResult<JsonNode?>(ExportBackup(Required(payload,"path")));
            var previous = (JsonObject)state.DeepClone();
            try
            {
                switch(action)
                {
                    case "student.save": SaveStudent(payload); break;
                    case "student.reorder": ReorderStudents(payload); break;
                    case "student.patch":
                        PatchStudent(payload);
                        if(S(payload,"field")=="currentSummary")manuallyEditedSummaryId=Required(payload,"id");
                        break;
                    case "student.delete": deletedStudentId=Required(payload,"id"); DeleteStudent(deletedStudentId); break;
                    case "project.save": SaveProject(payload); break;
                    case "task.add": AddTask(payload); break;
                    case "task.update": UpdateTask(payload); break;
                    case "task.toggle": var task = Find("tasks",Required(payload,"id")); task["done"]=!B(task,"done"); break;
                    case "task.delete": DeleteTask(Find("tasks",Required(payload,"id"))); break;
                    case "task.restore": RestoreTask(Required(payload,"id")); break;
                    case "chat.send":
                        SendChat(payload);
                        scheduleType=S(payload,"scopeType"); scheduleId=S(payload,"scopeId");
                        MarkPending(scheduleType,scheduleId); break;
                    case "chat.delete": changedArchiveScope=DeleteArchiveMessage(Required(payload,"id")); break;
                    case "chat.restore": changedArchiveScope=RestoreArchiveMessage(Required(payload,"id")); break;
                    case "summary.retry":
                        scheduleType=Required(payload,"scopeType"); scheduleId=Required(payload,"scopeId");
                        Scope(scheduleType,scheduleId); MarkPending(scheduleType,scheduleId); break;
                    case "alarm.save": SaveAlarm(payload); break;
                    case "alarm.toggle": var alarm = Find("alarms",Required(payload,"id")); alarm["enabled"]=!B(alarm,"enabled"); break;
                    case "alarm.enabled":
                        if(payload["enabled"] is not JsonValue enabledValue||!enabledValue.TryGetValue<bool>(out var alarmEnabled))
                            throw new InvalidOperationException("请选择有效的提醒状态。");
                        Find("alarms",Required(payload,"id"))["enabled"]=alarmEnabled;
                        break;
                    case "alarm.skip": SkipAlarm(payload); break;
                    case "alarm.delete": Arr("alarms").Remove(Find("alarms",Required(payload,"id"))); break;
                    case "reminder.poll":
                        result=ClaimReminders(payload,out changed);
                        if(changed) Save();
                        if(changed) Notify();
                        return Task.FromResult<JsonNode?>(result);
                    case "reminder.snooze":
                        var snoozed=Find("alarms",Required(payload,"id"));
                        if(S(snoozed,"repeat")=="monthly")
                        {
                            NormalizeMonthlyAlarm(snoozed);
                            if(!DateTimeOffset.TryParse(S(snoozed,"monthlyResumeWhen"),out _))snoozed["monthlyResumeWhen"]=S(snoozed,"when");
                        }
                        snoozed["when"]=DateTimeOffset.Now.AddMinutes(Math.Clamp(I(payload,"minutes",10),1,1440)).ToString("O");
                        snoozed["enabled"]=true;
                        break;
                    case "settings.save": SaveSettings(payload); CancelSummaries(); break;
                    case "settings.detect": DetectSettings(); CancelSummaries(); break;
                    case "notification.status": state["status"]!["notificationStatus"]=S(payload,"status"); break;
                    case "backup.import": ImportBackup(Required(payload,"path")); CancelSummaries(); alreadyPersisted=true; break;
                    default: throw new InvalidOperationException("不支持的操作。");
                }
                if(!alreadyPersisted)Save();
                if(deletedStudentId!=null)CancelSummaryScope("student",deletedStudentId);
                if(manuallyEditedSummaryId!=null)CancelSummaryScope("student",manuallyEditedSummaryId);
                if(changedArchiveScope is {} archiveScope)CancelSummaryScope(archiveScope.Type,archiveScope.Id);
                result=Snapshot();
            }
            catch { state=previous; throw; }
        }
        if(changed) Notify();
        if(scheduleType!=null && scheduleId!=null) ScheduleSummary(scheduleType,scheduleId);
        return Task.FromResult<JsonNode?>(result);
    }

    void Notify() { try { StateChanged?.Invoke(); } catch { /* UI disposal must not corrupt a completed save. */ } }
    JsonArray Arr(string key) => (JsonArray)state[key]!;
    IEnumerable<JsonObject> Rows(string key) => Arr(key).OfType<JsonObject>();
    JsonObject Find(string key,string id) => Rows(key).FirstOrDefault(x=>S(x,"id")==id) ?? throw new InvalidOperationException("记录不存在，请刷新后重试。");
    JsonObject Scope(string type,string id) => type switch { "student"=>Find("students",id), "project"=>Find("projects",id), _=>throw new InvalidOperationException("归档对象无效。") };
    internal static string S(JsonObject p,string key,string fallback="") => p[key] is JsonValue v && v.TryGetValue<string>(out var text) ? text ?? fallback : fallback;
    static int I(JsonObject p,string key,int fallback=0) => p[key] is JsonValue v && v.TryGetValue<int>(out var n) ? n:fallback;
    static bool B(JsonObject p,string key,bool fallback=false) => p[key] is JsonValue v && v.TryGetValue<bool>(out var b) ? b:fallback;
    static string Required(JsonObject p,string key,int max=500) { var x=S(p,key).Trim(); if(x.Length==0) throw new InvalidOperationException("请填写必要内容。"); if(x.Length>max) throw new InvalidOperationException("填写内容过长。"); return x; }
    static string NewId() => Guid.NewGuid().ToString("N");
    static string Now() => DateTimeOffset.Now.ToString("O");
    static JsonArray Strings(JsonNode? value,int max=100) => new((value as JsonArray ?? new()).Select(x=>x?.GetValue<string>()?.Trim()).Where(x=>!string.IsNullOrEmpty(x)).Distinct().Take(max).Select(x=>JsonValue.Create(x!.Length>200?x[..200]:x)).ToArray());
    static JsonArray RecentLabels(JsonNode? value,int max) => new((value as JsonArray ?? new()).OfType<JsonValue>().Select(x=>x.TryGetValue<string>(out var label)?label.Trim():"").Where(x=>x.Length>0).Distinct(StringComparer.Ordinal).Take(max).Select(x=>JsonValue.Create(x)).ToArray());
    void RememberLabels(string key,JsonArray labels,int max)
    {
        var preferences=(JsonObject)state["preferences"]!;
        preferences[key]=RecentLabels(new JsonArray(labels.Concat(preferences[key] as JsonArray ?? new()).Select(x=>x?.DeepClone()).ToArray()),max);
    }
    static string? DateValue(JsonObject p,string key) { var text=S(p,key); if(text.Length==0)return null; if(!DateOnly.TryParseExact(text,"yyyy-MM-dd",out _))throw new InvalidOperationException("日期格式应为 年-月-日。");return text; }
    void ValidateMembers(JsonArray members) { foreach(var id in members) Find("students",id!.GetValue<string>()); }

    void SaveStudent(JsonObject p)
    {
        var id=S(p,"id"); var s=id.Length>0?Find("students",id):new JsonObject { ["id"]=NewId(),["currentSummary"]="",["summaryStatus"]="idle",["summaryError"]="" };
        s["gradeAcademicYear"]=AcademicYear(DateTimeOffset.Now);
        s["name"]=Required(p,"name",100); s["grade"]=S(p,"grade").Trim(); s["tags"]=Strings(p["tags"],30); s["bio"]=S(p,"bio");
        if(id.Length==0) Arr("students").Add(s);
        RememberLabels("recentStudentLabels",(JsonArray)s["tags"]!,8);
    }
    void PatchStudent(JsonObject p)
    {
        var student=Find("students",Required(p,"id"));var field=Required(p,"field");
        if(field is not ("name" or "grade" or "tags" or "bio" or "currentSummary"))throw new InvalidOperationException("此项不能直接修改。");
        if(field=="tags")
        {
            if(p["value"] is not JsonArray values||values.Any(x=>x is not JsonValue v||!v.TryGetValue<string>(out _)))throw new InvalidOperationException("标签格式无效。");
            student["tags"]=Strings(values,30);RememberLabels("recentStudentLabels",(JsonArray)student["tags"]!,8);
        }
        else
        {
            if(p["value"] is not JsonValue value||!value.TryGetValue<string>(out var text))throw new InvalidOperationException("请填写文字内容。");
            if(field=="name")text=Required(p,"value",100);
            if(text.Length>100000)throw new InvalidOperationException("填写内容过长。");
            student[field]=field=="grade"?text.Trim():text;
            if(field=="grade")student["gradeAcademicYear"]=AcademicYear(DateTimeOffset.Now);
            if(field=="currentSummary")
            {
                student["summaryStatus"]="idle";student["summaryError"]="";
                student.Remove("currentSummaryMessageId");
                student["currentSummaryRevision"]=I(student,"currentSummaryRevision")+1;
            }
        }
    }
    void DeleteStudent(string id)
    {
        var student=Find("students",id);
        if(S((JsonObject)state["settings"]!,"currentStudentId")==id)((JsonObject)state["settings"]!).Remove("currentStudentId");
        Arr("students").Remove(student);
        student["deletedAt"]=Now();student["summaryStatus"]="idle";student["summaryError"]="";
        Arr("archivedStudents").Add(student);
        foreach(var project in Rows("projects"))RemoveMember(project,id);
        foreach(var task in Rows("tasks").ToList())
        {
            if(!(task["studentIds"] as JsonArray ?? new()).Any(x=>x?.GetValue<string>()==id))continue;
            RemoveMember(task,id);
            if(!HasActiveTaskScope(task))DeleteTask(task);
        }
        foreach(var task in Rows("deletedTasks"))RemoveMember(task,id);
        foreach(var alarm in Rows("alarms").Where(x=>S(x,"scopeType")=="student"&&S(x,"studentId")==id).ToList())Arr("alarms").Remove(alarm);
    }
    static void RemoveMember(JsonObject row,string id)
    {
        if(row["studentIds"] is JsonArray members)
            foreach(var member in members.Where(x=>x?.GetValue<string>()==id).ToList())members.Remove(member);
    }
    void SaveProject(JsonObject p)
    {
        var id=S(p,"id"); var s=id.Length>0?Find("projects",id):new JsonObject { ["id"]=NewId(),["currentSummary"]="",["summaryStatus"]="idle",["summaryError"]="" };
        var members=Strings(p["studentIds"]); ValidateMembers(members);
        s["name"]=Required(p,"name",200);s["studentIds"]=members;
        if(id.Length==0) Arr("projects").Add(s);
    }
    void AddTask(JsonObject p)
    {
        var type=Required(p,"scopeType");var id=Required(p,"scopeId");var scope=Scope(type,id);
        var members=type=="student"?new JsonArray(id):Strings(p.ContainsKey("studentIds")?p["studentIds"]:scope["studentIds"]);
        ValidateMembers(members);
        var task=new JsonObject { ["id"]=NewId(),["title"]=Required(p,"title",2000),["studentIds"]=members,["projectId"]=type=="project"?id:null,
            ["start"]=DateValue(p,"start"),["due"]=DateValue(p,"due"),["done"]=false,["tags"]=Strings(p["tags"],30),["createdAt"]=Now() };
        ValidateTaskDates(task);Arr("tasks").Add(task);
    }
    static void ValidateTaskDates(JsonObject task) { if(S(task,"start").Length>0&&S(task,"due").Length>0&&string.CompareOrdinal(S(task,"start"),S(task,"due"))>0)throw new InvalidOperationException("截止日期不能早于开始日期。"); }
    void UpdateTask(JsonObject p)
    {
        var task=Find("tasks",Required(p,"id"));
        if(p.ContainsKey("title"))task["title"]=Required(p,"title",2000);
        foreach(var key in new[]{"start","due"}) if(p.ContainsKey(key))task[key]=DateValue(p,key);
        if(p.ContainsKey("tags"))task["tags"]=Strings(p["tags"],30);
        if(p.ContainsKey("studentIds")){ var members=Strings(p["studentIds"]);ValidateMembers(members);task["studentIds"]=members; }
        ValidateTaskDates(task);
    }
    static bool TaskInScope(JsonObject task,string type,string id) => type=="project"?S(task,"projectId")==id:(task["studentIds"] as JsonArray ?? new()).Any(x=>x?.GetValue<string>()==id);
    bool HasActiveTaskScope(JsonObject task) => Rows("projects").Any(x=>S(x,"id")==S(task,"projectId")) || Rows("students").Any(x=>TaskInScope(task,"student",S(x,"id")));
    void DeleteTask(JsonObject task)
    {
        Arr("tasks").Remove(task);task["deletedAt"]=Now();Arr("deletedTasks").Add(task);
    }
    void RestoreTask(string id)
    {
        var task=Find("deletedTasks",id);
        if(!HasActiveTaskScope(task))throw new InvalidOperationException("任务关联的成员已删除，无法撤销。");
        ValidateMembers(task["studentIds"] as JsonArray ?? new());
        Arr("deletedTasks").Remove(task);task.Remove("deletedAt");Arr("tasks").Add(task);
    }

    void SendChat(JsonObject p)
    {
        var type=Required(p,"scopeType");var id=Required(p,"scopeId");Scope(type,id);
        var text=S(p,"text").Trim(); if(text.Length>100000)throw new InvalidOperationException("单次文字请控制在 10 万字以内。");
        var files=p["files"] as JsonArray ?? new();if(text.Length==0&&files.Count==0)throw new InvalidOperationException("请发送文字或文件。");
        if(files.Count>20)throw new InvalidOperationException("单次最多添加 20 个附件。");
        JsonObject? task=null;var taskId=S(p,"completeTaskId");
        if(taskId.Length>0){task=Find("tasks",taskId);if(!TaskInScope(task,type,id))throw new InvalidOperationException("该任务不属于当前归档。");}
        var attachments=new JsonArray();var saved=new List<string>();long total=0;
        try
        {
            foreach(var f in files.OfType<JsonObject>())
            {
                var data=S(f,"dataBase64");if(data.Length>28_000_000)throw new InvalidOperationException("单个文件不能超过 20 MB。");
                byte[] bytes;try{bytes=Convert.FromBase64String(data);}catch{throw new InvalidOperationException("附件内容无效，请重新选择文件。");}
                total+=bytes.Length;if(bytes.Length>20*1024*1024||total>40*1024*1024)throw new InvalidOperationException("单个文件最多 20 MB，单次合计最多 40 MB。");
                var name=Path.GetFileName(S(f,"name","附件").Replace('\\','/'));if(string.IsNullOrWhiteSpace(name))name="附件";
                var aid=NewId();var ext=Path.GetExtension(name);if(ext.Length>12||ext.Any(c=>!char.IsAsciiLetterOrDigit(c)&&c!='.'))ext="";
                var stored=aid+ext;var path=Path.Combine(root,"attachments",stored);File.WriteAllBytes(path,bytes);saved.Add(path);
                var extracted=AttachmentText.Extract(bytes,ext);
                attachments.Add(new JsonObject{["id"]=aid,["name"]=name,["mime"]=S(f,"mime","application/octet-stream"),["size"]=bytes.Length,["storedName"]=stored,["textStatus"]=extracted.Status,["extractedText"]=extracted.Text});
            }
            Arr("messages").Add(new JsonObject{["id"]=NewId(),["scopeType"]=type,["scopeId"]=id,["role"]="user",["text"]=text,["createdAt"]=Now(),["attachments"]=attachments,["suggestions"]=new JsonArray()});
            if(task!=null)task["done"]=true;
            // The caller atomically persists the message and completion in one state replacement.
        }
        catch{foreach(var path in saved)try{File.Delete(path);}catch{} throw;}
    }

    void SaveAlarm(JsonObject p)
    {
        var type=S(p,"scopeType","global");if(type is not ("student" or "project" or "global"))throw new InvalidOperationException("提醒类型无效。");
        var sid=type=="student"?Required(p,"studentId"):null;if(sid!=null)Find("students",sid);
        var pid=type=="project"?Required(p,"projectId"):null;if(pid!=null)Find("projects",pid);
        if(!DateTimeOffset.TryParse(S(p,"when"),out var when))throw new InvalidOperationException("请选择提醒时间。");
        var repeat=S(p,"repeat","once");if(repeat is not ("once" or "daily" or "weekly" or "interval" or "monthly"))throw new InvalidOperationException("重复规则无效。");
        var id=S(p,"id");var a=id.Length>0?Find("alarms",id):new JsonObject{["id"]=NewId()};
        var sameMonthlySchedule=repeat=="monthly"&&S(a,"repeat")=="monthly"&&DateTimeOffset.TryParse(S(a,"when"),out var oldWhen)&&oldWhen==when;
        if(repeat=="monthly")
        {
            if(!sameMonthlySchedule)
            {
                var local=TimeZoneInfo.ConvertTime(when,TimeZoneInfo.Local);
                a["monthDay"]=local.Day;a["monthTime"]=local.ToString("HH:mm:ss.fffffff",System.Globalization.CultureInfo.InvariantCulture);
                a.Remove("monthlyResumeWhen");
            }
        }
        else {a.Remove("monthDay");a.Remove("monthTime");a.Remove("monthlyResumeWhen");}
        a["scopeType"]=type;a["studentId"]=sid;a["projectId"]=pid;a["title"]=Required(p,"title",500);a["when"]=when.ToString("O");a["repeat"]=repeat;a["intervalDays"]=Math.Clamp(I(p,"intervalDays",1),1,3650);a["enabled"]=B(p,"enabled",true);
        a.Remove("lastTriggeredWhen");
        NormalizeMonthlyAlarm(a);
        if(id.Length==0)Arr("alarms").Add(a);
        if(type=="global")RememberLabels("recentGlobalAlarmLabels",new JsonArray(S(a,"title")),6);
    }
    JsonArray ClaimReminders(JsonObject p,out bool changed)
    {
        if(!DateTimeOffset.TryParse(S(p,"now"),out var now))now=DateTimeOffset.Now;
        var result=new JsonArray();changed=AdvanceStudentGrades(state,now);
        foreach(var a in Rows("alarms"))
        {
            var due=B(a,"enabled")&&DateTimeOffset.TryParse(S(a,"when"),out var when)&&when<=now;
            if(!due)continue;
            result.Add(new JsonObject{["id"]=S(a,"id"),["title"]=S(a,"title"),["scopeType"]=S(a,"scopeType"),["scopeId"]=S(a,"scopeType")=="project"?S(a,"projectId"):S(a,"studentId"),["when"]=S(a,"when")});
            a["lastTriggeredWhen"]=S(a,"when");
            if(due)
            {
                if(S(a,"repeat")=="once")a["enabled"]=false;
                else AdvanceRecurringAlarm(a,now);
            }
            changed=true;
        }
        return result;
    }

    void SkipAlarm(JsonObject p)
    {
        var alarm=Find("alarms",Required(p,"id"));
        if(!B(alarm,"enabled")||S(alarm,"repeat") is not ("daily" or "weekly" or "monthly" or "interval"))
            throw new InvalidOperationException("只有启用中的重复提醒可以取消本次。");
        if(!DateTimeOffset.TryParse(Required(p,"when"),out var expected))
            throw new InvalidOperationException("请选择要取消的提醒时间。");
        if(!DateTimeOffset.TryParse(S(alarm,"when"),out var current)||current!=expected)
            throw new Remote.MobileApiException(409,"这次提醒已更新，请查看新的提醒时间。","conflict");
        var now=DateTimeOffset.Now;
        // Match the occurrence shown to the caller, so a second click cannot skip the following week.
        // Preserve actual delivery history: cancellation is never reported as a notification firing.
        AdvanceRecurringAlarm(alarm,current>now?current:now);
    }

    static void AdvanceRecurringAlarm(JsonObject alarm,DateTimeOffset after)
    {
        if(S(alarm,"repeat")=="monthly")
        {
            NormalizeMonthlyAlarm(alarm);
            var regular=DateTimeOffset.TryParse(S(alarm,"monthlyResumeWhen"),out var resumed)?resumed:DateTimeOffset.Parse(S(alarm,"when"));
            alarm["when"]=(regular>after?regular:NextMonthly(alarm,regular,after)).ToString("O");
            alarm.Remove("monthlyResumeWhen");
            return;
        }
        var old=DateTimeOffset.Parse(S(alarm,"when"));
        var days=S(alarm,"repeat") switch{"daily"=>1,"weekly"=>7,_=>Math.Clamp(I(alarm,"intervalDays",1),1,3650)};
        var localOld=TimeZoneInfo.ConvertTime(old,TimeZoneInfo.Local).DateTime;
        var localAfter=TimeZoneInfo.ConvertTime(after,TimeZoneInfo.Local).DateTime;
        var steps=Math.Max(1,(int)Math.Floor((localAfter-localOld).TotalDays/days)+1);
        var next=localOld.AddDays((double)days*steps);
        while(new DateTimeOffset(next,TimeZoneInfo.Local.GetUtcOffset(next))<=after)next=next.AddDays(days);
        alarm["when"]=new DateTimeOffset(next,TimeZoneInfo.Local.GetUtcOffset(next)).ToString("O");
    }

    static void NormalizeMonthlyAlarm(JsonObject alarm)
    {
        if(S(alarm,"repeat")!="monthly"||!DateTimeOffset.TryParse(S(alarm,"when"),out var when))return;
        var local=TimeZoneInfo.ConvertTime(when,TimeZoneInfo.Local);
        if(I(alarm,"monthDay") is <1 or >31)alarm["monthDay"]=local.Day;
        if(!TimeOnly.TryParse(S(alarm,"monthTime"),System.Globalization.CultureInfo.InvariantCulture,out _))alarm["monthTime"]=local.ToString("HH:mm:ss.fffffff",System.Globalization.CultureInfo.InvariantCulture);
    }
    static DateTimeOffset NextMonthly(JsonObject alarm,DateTimeOffset previous,DateTimeOffset now)
    {
        var localOld=TimeZoneInfo.ConvertTime(previous,TimeZoneInfo.Local).DateTime;
        var localNow=TimeZoneInfo.ConvertTime(now,TimeZoneInfo.Local).DateTime;
        var day=I(alarm,"monthDay",localOld.Day);
        var time=TimeOnly.Parse(S(alarm,"monthTime"),System.Globalization.CultureInfo.InvariantCulture);
        var months=Math.Max(1,(localNow.Year-localOld.Year)*12+localNow.Month-localOld.Month);
        var month=new DateTime(localOld.Year,localOld.Month,1).AddMonths(months);
        while(true)
        {
            var local=new DateTime(month.Year,month.Month,Math.Min(day,DateTime.DaysInMonth(month.Year,month.Month))).Add(time.ToTimeSpan());
            // A skipped DST clock time moves to the first valid local minute for this occurrence only.
            while(TimeZoneInfo.Local.IsInvalidTime(local))local=local.AddMinutes(1);
            var next=new DateTimeOffset(local,TimeZoneInfo.Local.GetUtcOffset(local));
            if(next>now)return next;
            month=month.AddMonths(1);
        }
    }

    public string? ResolveAttachment(string attachmentId)
    {
        lock(gate)
        {
            foreach(var msg in Rows("messages"))foreach(var f in (msg["attachments"] as JsonArray??new()).OfType<JsonObject>())
                if(S(f,"id")==attachmentId&&SafeStoredName(S(f,"storedName"))){var path=Path.Combine(root,"attachments",S(f,"storedName"));return File.Exists(path)?path:null;}
            return null;
        }
    }
    static bool SafeStoredName(string name) => name.Length is >0 and <100 && name.All(c=>char.IsAsciiLetterOrDigit(c)||c=='.'||c=='-'||c=='_') && name is not ("." or "..") && !name.Contains("..");
    public void Dispose(){lock(gate){disposed=true;foreach(var c in pending.Values)c.Cancel();pending.Clear();client.Dispose();}}
}
