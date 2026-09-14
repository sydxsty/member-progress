using System.Text.Json;
using System.Text.Json.Nodes;

namespace StudentProgress;

internal static class V4Checks
{
    public static int Run(string dataDir)
    {
        var passed=new List<string>();var ownsDirectory=false;
        try
        {
            if(Directory.Exists(dataDir)&&Directory.EnumerateFileSystemEntries(dataDir).Any())return 1;
            Directory.CreateDirectory(dataDir);ownsDirectory=true;
            RunAsync(dataDir,passed).GetAwaiter().GetResult();
            File.WriteAllText(Path.Combine(dataDir,"v4-result.json"),JsonSerializer.Serialize(new { ok=true,checks=passed,liveApiCalls=0 },new JsonSerializerOptions { WriteIndented=true }));
            return 0;
        }
        catch(Exception ex)
        {
            if(ownsDirectory)File.WriteAllText(Path.Combine(dataDir,"v4-result.json"),JsonSerializer.Serialize(new { ok=false,checks=passed,error=ex.Message,liveApiCalls=0 },new JsonSerializerOptions { WriteIndented=true }));
            return 1;
        }
    }
    static JsonObject P(object value)=>JsonSerializer.SerializeToNode(value)!.AsObject();
    static JsonArray Rows(JsonObject state,string name)=>state[name]!.AsArray();
    static string Id(JsonNode? row)=>row!["id"]!.GetValue<string>();
    static DateTimeOffset Local(int year,int month,int day,int hour=9)
    {
        var value=new DateTime(year,month,day,hour,0,0,DateTimeKind.Unspecified);
        return new DateTimeOffset(value,TimeZoneInfo.Local.GetUtcOffset(value));
    }
    static void Check(bool condition,string label,List<string> passed)
    {
        if(!condition)throw new InvalidOperationException(label);
        passed.Add(label);
    }
    static async Task Reject(AppService app,string action,object value,string label,List<string> passed)
    {
        var before=app.Snapshot().ToJsonString();var rejected=false;
        try { await app.HandleAsync(action,P(value)); } catch(InvalidOperationException) { rejected=true; }
        Check(rejected&&app.Snapshot().ToJsonString()==before,label,passed);
    }
    static async Task RunAsync(string dataDir,List<string> passed)
    {
        var actual=Path.Combine(dataDir,"isolated-data");Directory.CreateDirectory(actual);
        File.WriteAllText(Path.Combine(actual,"initialized"),"v4-check-no-settings-detection");
        var local=DateTime.Now;var year=local.Month>=9?local.Year:local.Year-1;
        File.WriteAllText(Path.Combine(actual,"state.json"),P(new
        {
            schemaVersion=1,
            students=new object[]{
                new { id="legacy",name="旧学生",grade="博零",tags=new[]{"研一","实习"},bio="研究内容",currentSummary="保留进展" },
                new { id="anchored",name="已有学年",grade="博九",gradeAcademicYear=year-1,tags=new[]{"联培"} },
                new { id="custom",name="自定义",grade="本科",gradeAcademicYear=year-1,tags=new[]{"高年级"} }
            },
            projects=Array.Empty<object>(),tasks=Array.Empty<object>(),messages=Array.Empty<object>(),alarms=Array.Empty<object>(),
            settings=new { baseUrl="http://127.0.0.1:1/v1",model="",apiMode="responses" },status=new { }
        }).ToJsonString());
        string projectId,studentAlarm,projectAlarm;
        using(var app=new AppService(actual))
        {
            async Task Do(string action,object value)=>await app.HandleAsync(action,P(value));
            JsonObject Student(string id)=>Rows(app.Snapshot(),"students").First(x=>Id(x)==id)!.AsObject();
            JsonObject Alarm(string id)=>Rows(app.Snapshot(),"alarms").First(x=>Id(x)==id)!.AsObject();
            Check(Student("legacy")["grade"]!.GetValue<string>()=="博零"&&Student("legacy")["gradeAcademicYear"]!.GetValue<int>()==year,"legacy_grade_migration_initializes_current_year_without_guessing_past",passed);
            Check(Student("anchored")["grade"]!.GetValue<string>()=="博十"&&Student("custom")["grade"]!.GetValue<string>()=="本科","startup_catches_up_recognized_grade_preserving_custom_label",passed);
            Check(Student("legacy")["tags"]!.ToJsonString()==new JsonArray("研一","实习").ToJsonString(),"grade_migration_never_advances_meta_labels",passed);
            Check(AppService.AdvanceGrade("研二",2)=="研四"&&AppService.AdvanceGrade("博9",1)=="博10"&&AppService.AdvanceGrade("大三年级",1)=="大四年级"&&AppService.AdvanceGrade("博零",1)=="博一"&&AppService.AdvanceGrade("访学",2)=="访学","recognized_grade_styles_advance_without_rewriting_custom_labels",passed);
            await Do("student.patch",new { id="anchored",field="grade",value="研一" });
            await Do("student.save",new { name="新同学",grade="大一",tags=new[]{"实习"} });
            var fresh=Id(Rows(app.Snapshot(),"students").Last());
            Check(Student("anchored")["gradeAcademicYear"]!.GetValue<int>()==year&&Student(fresh)["gradeAcademicYear"]!.GetValue<int>()==year,"manual_grade_and_new_student_anchor_in_current_academic_year",passed);

            await Do("project.save",new { name="合作项目",studentIds=new[]{"legacy","anchored"} });projectId=Id(Rows(app.Snapshot(),"projects")[0]);
            await Do("task.add",new { scopeType="project",scopeId=projectId,title="共有任务" });
            var prior=Rows(app.Snapshot(),"students").ToDictionary(Id,x=>x!.ToJsonString());
            await Do("student.reorder",new { ids=new[]{fresh,"anchored","custom","legacy"} });
            Check(Rows(app.Snapshot(),"students").Select(Id).SequenceEqual(new[]{fresh,"anchored","custom","legacy"})&&Rows(app.Snapshot(),"students").All(x=>prior[Id(x)]==x!.ToJsonString()),"reorder_changes_only_array_order_preserving_every_student_field",passed);
            Check(Rows(app.Snapshot(),"projects")[0]!["studentIds"]!.ToJsonString()==new JsonArray("legacy","anchored").ToJsonString()&&Rows(app.Snapshot(),"tasks")[0]!["studentIds"]!.ToJsonString()==new JsonArray("legacy","anchored").ToJsonString(),"reorder_preserves_project_and_task_membership",passed);
            await Reject(app,"student.reorder",new { ids=new[]{fresh,"legacy"} },"partial_reorder_rejected_atomically",passed);
            await Reject(app,"student.reorder",new { ids=new[]{fresh,"anchored","custom","custom"} },"duplicate_reorder_rejected_atomically",passed);
            await Reject(app,"student.reorder",new { ids=new[]{fresh,"anchored","custom","missing"} },"unknown_reorder_rejected_atomically",passed);

            var due=Local(year+1,8,9);var when=due.ToString("O");
            await Do("alarm.save",new { scopeType="global",title="全局提醒",when,repeat="once" });var globalAlarm=Id(Rows(app.Snapshot(),"alarms").Last());
            await Do("alarm.save",new { scopeType="student",studentId="legacy",title="对进度",when,repeat="daily" });studentAlarm=Id(Rows(app.Snapshot(),"alarms").Last());
            await Do("alarm.save",new { scopeType="project",projectId,title="项目交流",when,repeat="monthly" });projectAlarm=Id(Rows(app.Snapshot(),"alarms").Last());
            Check(Alarm(projectAlarm)["projectId"]!.GetValue<string>()==projectId&&Alarm(projectAlarm)["studentId"]==null&&Alarm(projectAlarm)["monthDay"]!.GetValue<int>()==9,"project_monthly_reminder_keeps_project_scope_and_month_anchor",passed);
            await Reject(app,"alarm.save",new { scopeType="project",projectId="missing",title="无效项目",when },"unknown_project_alarm_rejected_atomically",passed);
            var claimed=(await app.HandleAsync("reminder.poll",P(new { now=when })))!.AsArray();
            Check(claimed.Count==3&&claimed.First(x=>Id(x)==projectAlarm)!["scopeId"]!.GetValue<string>()==projectId&&claimed.First(x=>Id(x)==studentAlarm)!["scopeId"]!.GetValue<string>()=="legacy","project_and_student_notifications_route_to_their_archive",passed);
            Check(new[]{globalAlarm,studentAlarm,projectAlarm}.All(id=>Alarm(id)["lastTriggeredWhen"]!.GetValue<string>()==when)&&!Alarm(globalAlarm)["enabled"]!.GetValue<bool>()&&DateTimeOffset.Parse(Alarm(projectAlarm)["when"]!.GetValue<string>())==Local(year+1,9,9),"fired_reminders_retain_gantt_date_while_once_disables_and_monthly_advances",passed);
            var repeated=(await app.HandleAsync("reminder.poll",P(new { now=when })))!.AsArray();
            Check(repeated.Count==0&&Student("legacy")["grade"]!.GetValue<string>()=="博零","repeat_poll_does_not_duplicate_reminders_or_advance_grade_before_september",passed);
            await Do("alarm.save",new { id=projectAlarm,scopeType="project",projectId,title="项目交流改名",when=Alarm(projectAlarm)["when"]!.GetValue<string>(),repeat="monthly" });
            Check(!Alarm(projectAlarm).ContainsKey("lastTriggeredWhen")&&Alarm(projectAlarm)["monthDay"]!.GetValue<int>()==9,"editing_reminder_clears_obsolete_marker_preserving_month_anchor",passed);

            var context=app.BuildAssistantContext(P(new { includeReminders=true,includeRecent=false }));
            Check(Rows(context,"projects")[0]!["studentIds"]!.AsArray().Count==2&&Rows(context,"reminders").First(x=>Id(x)==projectAlarm)!["projectId"]!.GetValue<string>()==projectId,"assistant_context_includes_project_reminder_and_current_members",passed);
            await Do("student.delete",new { id="anchored" });
            Check(Rows(app.Snapshot(),"projects")[0]!["studentIds"]!.ToJsonString()==new JsonArray("legacy").ToJsonString()&&Alarm(projectAlarm)["projectId"]!.GetValue<string>()==projectId,"deleting_student_keeps_project_reminder_for_remaining_members",passed);

            // Keep all alarms off to exercise annual maintenance without a due notification.
            foreach(var alarm in Rows(app.Snapshot(),"alarms").Where(x=>x!["enabled"]!.GetValue<bool>()).ToArray())await Do("alarm.toggle",new { id=Id(alarm) });
            var september=(await app.HandleAsync("reminder.poll",P(new { now=Local(year+1,9,1,0).ToString("O") })))!.AsArray();
            Check(september.Count==0&&Student("legacy")["grade"]!.GetValue<string>()=="博一"&&Student(fresh)["grade"]!.GetValue<string>()=="大二"&&Student("custom")["grade"]!.GetValue<string>()=="本科","september_rollover_works_without_due_alarm_and_preserves_custom_grade",passed);
            var septemberState=app.Snapshot().ToJsonString();
            await Do("reminder.poll",new { now=Local(year+1,9,1,0).ToString("O") });
            Check(app.Snapshot().ToJsonString()==septemberState&&!app.RefreshStudentGrades(),"same_academic_year_or_clock_rollback_never_advances_twice",passed);
            await Do("reminder.poll",new { now=Local(year+3,9,1,0).ToString("O") });
            Check(Student("legacy")["grade"]!.GetValue<string>()=="博三"&&Student("legacy")["tags"]!.ToJsonString()==new JsonArray("研一","实习").ToJsonString()&&Student("legacy")["currentSummary"]!.GetValue<string>()=="保留进展","multi_year_catchup_preserves_meta_and_progress",passed);
        }
        using(var reopened=new AppService(actual))
        {
            Check(Rows(reopened.Snapshot(),"students").Last()!["id"]!.GetValue<string>()=="legacy"&&Rows(reopened.Snapshot(),"students").Last()!["grade"]!.GetValue<string>()=="博三"&&Rows(reopened.Snapshot(),"alarms").First(x=>Id(x)==projectAlarm)!["projectId"]!.GetValue<string>()==projectId,"reorder_grade_anchor_and_project_reminder_survive_restart",passed);
        }
    }
}
