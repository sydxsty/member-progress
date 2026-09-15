using System.Text.Json.Nodes;
using StudentProgress.Remote;

namespace StudentProgress;

public sealed partial class AppService
{
    // Remote callers receive an explicit projection, never settings or local attachment paths.
    public JsonObject MobileSnapshot()
    {
        lock (gate)
        {
            var result = new JsonObject { ["protocolVersion"] = MobileProtocol.Version };
            var versions = new JsonObject();
            foreach (var (key, type, fields) in new[]
            {
                ("students", "student", new[]{"id","name","grade","tags","bio","currentSummary","summaryStatus","summaryError"}),
                ("projects", "project", new[]{"id","name","studentIds","currentSummary","summaryStatus","summaryError"}),
                ("tasks", "task", new[]{"id","title","studentIds","projectId","start","due","done","tags","createdAt"}),
                ("alarms", "alarm", new[]{"id","scopeType","studentId","projectId","title","when","repeat","intervalDays","enabled","monthDay","monthTime","monthlyResumeWhen","lastTriggeredWhen"})
            })
            {
                var rows = new JsonArray();
                foreach (var row in Rows(key))
                {
                    rows.Add(AssistantFields(row, fields));
                    versions[type + ":" + S(row, "id")] = MobileRecordVersion(row);
                }
                result[key] = rows;
            }
            var messages = new JsonArray();
            foreach (var message in Rows("messages"))
            {
                var row = AssistantFields(message, "id", "scopeType", "scopeId", "role", "text", "createdAt", "suggestions");
                row["attachments"] = new JsonArray((message["attachments"] as JsonArray ?? new()).OfType<JsonObject>()
                    .Select(a => (JsonNode)AssistantFields(a, "id", "name", "mime", "size", "textStatus")).ToArray());
                messages.Add(row);
                versions["message:" + S(message, "id")] = MobileRecordVersion(message);
            }
            result["messages"] = messages;
            result["preferences"] = AssistantFields((JsonObject)state["preferences"]!, "recentStudentLabels", "recentGlobalAlarmLabels");
            result["currentUser"] = CurrentUser();
            result["recordVersions"] = versions;
            result["serverTime"] = Now();
            return result;
        }
    }

    static string MobileRecordVersion(JsonObject row)
    {
        var content = (JsonObject)row.DeepClone();
        content.Remove("gradeAcademicYear");
        return MobileProtocol.Fingerprint(content);
    }

    public JsonObject HandleMobileAction(JsonObject request)
    {
        var payload = MobileProtocol.ReadRequest(request);
        var action = S(request, "action");
        var requestId = S(request, "requestId");
        var fingerprint = MobileProtocol.Fingerprint(request);
        string? scheduleType = null, scheduleId = null;
        (string Type, string Id)? changedArchiveScope = null;
        JsonObject result;
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            var previousReceipt = (state["mobileMutationReceipts"] as JsonArray ?? new()).OfType<JsonObject>()
                .FirstOrDefault(x => S(x, "requestId") == requestId);
            if (previousReceipt != null)
            {
                if (S(previousReceipt, "fingerprint") != fingerprint)
                    throw new MobileApiException(409, "这次请求编号对应的内容不同，请重新提交。", "request_mismatch");
                var replay = MobileSnapshot(); replay["replayed"] = true; return replay;
            }
            ValidateMobilePayload(action, payload);
            var recordKey = action switch
            {
                "student.patch" => "students",
                "task.update" or "task.delete" => "tasks",
                "chat.delete" => "messages",
                "alarm.save" when S(payload, "id").Length > 0 => "alarms",
                "alarm.enabled" or "alarm.delete" or "alarm.skip" or "reminder.snooze" => "alarms",
                _ => ""
            };
            if (recordKey.Length > 0)
            {
                var record = Rows(recordKey).FirstOrDefault(x => S(x, "id") == S(payload, "id"));
                if (record == null) throw new MobileApiException(409, "这条记录已被删除，请刷新。", "conflict");
                MobileProtocol.CheckVersion(request, MobileRecordVersion(record));
            }
            if (action == "chat.send" && S(payload, "completeTaskId").Length > 0)
                MobileProtocol.CheckVersion(request, MobileRecordVersion(Find("tasks", S(payload, "completeTaskId"))));
            var previous = (JsonObject)state.DeepClone();
            try
            {
                switch (action)
                {
                    case "student.patch": PatchStudent(payload); break;
                    case "task.add": AddTask(payload); break;
                    case "task.update":
                        UpdateTask(payload);
                        if (payload.ContainsKey("done")) Find("tasks", Required(payload, "id"))["done"] = B(payload, "done");
                        break;
                    case "task.delete": DeleteTask(Find("tasks", Required(payload, "id"))); break;
                    case "task.restore": RestoreTask(Required(payload, "id")); break;
                    case "chat.send":
                        SendChat(payload); scheduleType = S(payload, "scopeType"); scheduleId = S(payload, "scopeId");
                        MarkPending(scheduleType, scheduleId); break;
                    case "chat.delete": changedArchiveScope = DeleteArchiveMessage(Required(payload, "id")); break;
                    case "chat.restore": changedArchiveScope = RestoreArchiveMessage(Required(payload, "id")); break;
                    case "summary.retry":
                        scheduleType = Required(payload, "scopeType"); scheduleId = Required(payload, "scopeId");
                        Scope(scheduleType, scheduleId); MarkPending(scheduleType, scheduleId); break;
                    case "alarm.save": SaveAlarm(payload); break;
                    case "alarm.enabled": Find("alarms", Required(payload, "id"))["enabled"] = B(payload, "enabled"); break;
                    case "alarm.delete": Arr("alarms").Remove(Find("alarms", Required(payload, "id"))); break;
                    case "alarm.skip": SkipAlarm(payload); break;
                    case "reminder.snooze":
                        var alarm = Find("alarms", Required(payload, "id"));
                        if (S(alarm, "repeat") == "monthly")
                        {
                            NormalizeMonthlyAlarm(alarm);
                            if (!DateTimeOffset.TryParse(S(alarm, "monthlyResumeWhen"), out _)) alarm["monthlyResumeWhen"] = S(alarm, "when");
                        }
                        alarm["when"] = DateTimeOffset.Now.AddMinutes(Math.Clamp(I(payload, "minutes", 10), 1, 1440)).ToString("O");
                        alarm["enabled"] = true;
                        break;
                }
                state["mobileMutationReceipts"] ??= new JsonArray();
                ((JsonArray)state["mobileMutationReceipts"]!).Add(new JsonObject
                { ["requestId"] = requestId, ["fingerprint"] = fingerprint, ["createdAt"] = Now() });
                // The write and its replay marker are one atomic replacement.
                Save();
                if (action == "student.patch") CancelSummaryScope("student", Required(payload, "id"));
                if (changedArchiveScope is {} archiveScope) CancelSummaryScope(archiveScope.Type, archiveScope.Id);
                result = MobileSnapshot();
            }
            catch { state = previous; throw; }
        }
        Notify();
        if (scheduleType != null && scheduleId != null) ScheduleSummary(scheduleType, scheduleId);
        return result;
    }

    static void ValidateMobilePayload(string action, JsonObject p)
    {
        switch (action)
        {
            case "student.patch":
                MobileProtocol.Only(p, "id", "field", "value");
                MobileProtocol.StringFields(p, "id", "field", "value");
                if (S(p, "field") != "currentSummary") throw new MobileApiException(400, "手机端只修改当前进展。");
                break;
            case "task.add":
                MobileProtocol.Only(p, "scopeType", "scopeId", "title", "start", "due", "tags", "studentIds");
                MobileProtocol.StringFields(p, "scopeType", "scopeId", "title", "start", "due");
                MobileProtocol.StringArray(p, "tags", 30); MobileProtocol.StringArray(p, "studentIds", 100); break;
            case "task.update":
                MobileProtocol.Only(p, "id", "done", "title", "start", "due", "tags");
                MobileProtocol.StringFields(p, "id", "title", "start", "due");
                MobileProtocol.BooleanFields(p, "done"); MobileProtocol.StringArray(p, "tags", 30); break;
            case "task.delete": case "task.restore": case "alarm.delete": case "chat.delete": case "chat.restore":
                MobileProtocol.Only(p, "id"); MobileProtocol.Required(p, "id"); break;
            case "chat.send":
                MobileProtocol.Only(p, "scopeType", "scopeId", "text", "completeTaskId");
                MobileProtocol.StringFields(p, "scopeType", "scopeId", "text", "completeTaskId"); break;
            case "summary.retry":
                MobileProtocol.Only(p, "scopeType", "scopeId"); MobileProtocol.StringFields(p, "scopeType", "scopeId"); break;
            case "alarm.save":
                MobileProtocol.Only(p, "id", "scopeType", "studentId", "projectId", "title", "when", "repeat", "intervalDays", "enabled");
                MobileProtocol.StringFields(p, "id", "scopeType", "studentId", "projectId", "title", "when", "repeat");
                MobileProtocol.BooleanFields(p, "enabled");
                if (p.ContainsKey("intervalDays") && (p["intervalDays"] is not JsonValue iv || !iv.TryGetValue<int>(out var days) || days is < 1 or > 3650))
                    throw new MobileApiException(400, "提醒间隔需要是 1 至 3650 天。");
                break;
            case "alarm.enabled":
                MobileProtocol.Only(p, "id", "enabled"); MobileProtocol.Required(p, "id");
                if (!p.ContainsKey("enabled")) throw new MobileApiException(400, "请选择提醒状态。");
                MobileProtocol.BooleanFields(p, "enabled"); break;
            case "alarm.skip":
                MobileProtocol.Only(p, "id", "when"); MobileProtocol.Required(p, "id"); MobileProtocol.Required(p, "when");
                MobileProtocol.StringFields(p, "id", "when"); break;
            case "reminder.snooze":
                MobileProtocol.Only(p, "id", "minutes"); MobileProtocol.Required(p, "id");
                if (p.ContainsKey("minutes") && (p["minutes"] is not JsonValue mv || !mv.TryGetValue<int>(out var minutes) || minutes is < 1 or > 1440))
                    throw new MobileApiException(400, "延后时间需要是 1 至 1440 分钟。");
                break;
            default: throw new MobileApiException(400, "手机端不支持此操作。");
        }
    }

    public JsonObject MobileReminderPlan(DateTimeOffset? at = null)
    {
        var now = at ?? DateTimeOffset.Now;
        var from = now.AddDays(-1); var until = now.AddDays(90);
        var occurrences = new List<JsonObject>(); var seen = new HashSet<string>(StringComparer.Ordinal);
        lock (gate)
        {
            foreach (var source in Rows("alarms"))
            {
                var alarm = (JsonObject)source.DeepClone();
                NormalizeMonthlyAlarm(alarm);
                var type = S(alarm, "scopeType", "global");
                var scopeId = type == "student" ? S(alarm, "studentId") : type == "project" ? S(alarm, "projectId") : "";
                var label = type == "student" ? Rows("students").FirstOrDefault(s => S(s, "id") == scopeId)
                    : type == "project" ? Rows("projects").FirstOrDefault(p => S(p, "id") == scopeId) : null;
                if (type != "global" && label == null) continue;
                void Add(DateTimeOffset when)
                {
                    if (when < from || when > until) return;
                    var occurrenceId = S(alarm, "id") + ":" + when.ToUnixTimeMilliseconds().ToString(System.Globalization.CultureInfo.InvariantCulture);
                    if (!seen.Add(occurrenceId)) return;
                    occurrences.Add(new JsonObject
                    {
                        ["id"] = occurrenceId, ["reminderId"] = S(alarm, "id"), ["title"] = S(alarm, "title"),
                        ["scopeType"] = type, ["scopeId"] = scopeId,
                        ["scopeLabel"] = type == "global" ? "全局" : (type == "project" ? "项目 · " : "") + S(label!, "name"),
                        ["when"] = when.ToString("O")
                    });
                }
                if (DateTimeOffset.TryParse(S(alarm, "lastTriggeredWhen"), out var triggered)) Add(triggered);
                if (!B(alarm, "enabled") || !DateTimeOffset.TryParse(S(alarm, "when"), out var next)) continue;
                var repeat = S(alarm, "repeat", "once");
                if (repeat == "once") { Add(next); continue; }
                if (repeat == "monthly")
                {
                    // A snooze is an extra occurrence; its preserved regular anchor remains intact.
                    if (DateTimeOffset.TryParse(S(alarm, "monthlyResumeWhen"), out var resume))
                    { Add(next); next = resume > next ? resume : NextMonthly(alarm, resume, next); }
                    if (next < from) next = NextMonthly(alarm, next, from.AddTicks(-1));
                    for (var i = 0; next <= until && i < 100; i++)
                    { Add(next); next = NextMonthly(alarm, next, next); }
                }
                else
                {
                    var days = repeat == "daily" ? 1 : repeat == "weekly" ? 7 : Math.Clamp(I(alarm, "intervalDays", 1), 1, 3650);
                    var local = TimeZoneInfo.ConvertTime(next, TimeZoneInfo.Local).DateTime;
                    if (next < from)
                    {
                        var localFrom = TimeZoneInfo.ConvertTime(from, TimeZoneInfo.Local).DateTime;
                        local = local.AddDays(Math.Max(0, Math.Floor((localFrom - local).TotalDays / days)) * days);
                        next = new DateTimeOffset(local, TimeZoneInfo.Local.GetUtcOffset(local));
                    }
                    for (var i = 0; next <= until && i < 100; i++)
                    {
                        Add(next); local = local.AddDays(days);
                        next = new DateTimeOffset(local, TimeZoneInfo.Local.GetUtcOffset(local));
                    }
                }
            }
        }
        return new JsonObject
        {
            ["serverTime"] = now.ToString("O"), ["timeZone"] = TimeZoneInfo.Local.Id, ["validUntil"] = until.ToString("O"),
            ["occurrences"] = new JsonArray(occurrences.OrderBy(o => DateTimeOffset.Parse(S(o, "when"))).ThenBy(o => S(o, "id"), StringComparer.Ordinal).Select(o => (JsonNode)o).ToArray())
        };
    }
}
