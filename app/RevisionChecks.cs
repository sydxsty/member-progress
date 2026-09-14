using System.IO.Compression;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace StudentProgress;

// A single, bounded pass over a new directory. No local API settings or live AI are used.
internal static class RevisionChecks
{
    public static int Run(string dataDir)
    {
        var passed=new List<string>();var ownsDirectory=false;
        try
        {
            if(Directory.Exists(dataDir)&&Directory.EnumerateFileSystemEntries(dataDir).Any())return 1;
            Directory.CreateDirectory(dataDir);ownsDirectory=true;
            RunAsync(dataDir,passed).GetAwaiter().GetResult();
            File.WriteAllText(Path.Combine(dataDir,"revision-result.json"),JsonSerializer.Serialize(new { ok=true,checks=passed,liveApiCalls=0 },new JsonSerializerOptions { WriteIndented=true }));
            return 0;
        }
        catch(Exception ex)
        {
            if(ownsDirectory)File.WriteAllText(Path.Combine(dataDir,"revision-result.json"),JsonSerializer.Serialize(new { ok=false,checks=passed,error=ex.Message,liveApiCalls=0 },new JsonSerializerOptions { WriteIndented=true }));
            return 1;
        }
    }

    static JsonObject Payload(object value)=>JsonSerializer.SerializeToNode(value)!.AsObject();
    static JsonArray Rows(JsonObject state,string name)=>state[name]!.AsArray();
    static string Id(JsonNode? row)=>row!["id"]!.GetValue<string>();
    static string[] Labels(JsonObject state,string name)=>state["preferences"]![name]!.AsArray().Select(x=>x!.GetValue<string>()).ToArray();
    static void Check(bool condition,string label,List<string> passed)
    {
        if(!condition)throw new InvalidOperationException(label);
        passed.Add(label);
    }
    static async Task Reject(AppService app,string action,object payload,string label,List<string> passed)
    {
        var before=app.Snapshot().ToJsonString();var rejected=false;
        try { await app.HandleAsync(action,Payload(payload)); }
        catch(InvalidOperationException) { rejected=true; }
        Check(rejected&&before==app.Snapshot().ToJsonString(),label,passed);
    }

    static async Task RunAsync(string dataDir,List<string> passed)
    {
        var actualData=Path.Combine(dataDir,"isolated-data");Directory.CreateDirectory(actualData);
        File.WriteAllText(Path.Combine(actualData,"initialized"),"revision-check-no-settings-detection");
        var legacy=Payload(new
        {
            schemaVersion=1,
            students=new[]{new { id="legacy-student",name="旧版验证",grade="研二",tags=new[]{"旧标签","实习"},bio="",currentSummary="",summaryStatus="idle",summaryError="" }},
            projects=Array.Empty<object>(),tasks=Array.Empty<object>(),messages=Array.Empty<object>(),
            alarms=new[]{new { id="legacy-alarm",scopeType="global",title="旧提醒",when=DateTimeOffset.Now.AddDays(1).ToString("O"),repeat="once",enabled=true }},
            settings=new { baseUrl="http://127.0.0.1:1/v1",model="",apiMode="responses" },status=new { notificationStatus="isolated-check" }
        });
        File.WriteAllText(Path.Combine(actualData,"state.json"),legacy.ToJsonString());
        string studentB,project,sharedTask,personalTask,attachmentId;int historyCount;
        var backup=Path.Combine(dataDir,"revision-backup.zip");
        string[] expectedStudentLabels,expectedAlarmLabels;
        using(var app=new AppService(actualData))
        {
            async Task Do(string action,object value)=>await app.HandleAsync(action,Payload(value));
            var migrated=app.Snapshot();
            Check(migrated["schemaVersion"]!.GetValue<int>()==1&&Rows(migrated,"archivedStudents").Count==0&&!migrated.ContainsKey("deletedTasks"),"schema1_compatible_defaults_and_private_deleted_tasks",passed);
            Check(Labels(migrated,"recentStudentLabels").SequenceEqual(new[]{"旧标签","实习"})&&Labels(migrated,"recentGlobalAlarmLabels").SequenceEqual(new[]{"旧提醒"}),"legacy_recent_labels_seeded",passed);
            Check(!migrated["settings"]!["aiConfigured"]!.GetValue<bool>()&&!File.Exists(Path.Combine(actualData,"secret.dat")),"real_ai_disabled_without_local_settings_import",passed);
            await Do("student.save",new { name="验证乙",tags=Enumerable.Range(1,10).Select(x=>$"标签{x}").ToArray() });
            studentB=Id(Rows(app.Snapshot(),"students")[1]);
            await Do("student.save",new { id="legacy-student",name="旧版验证",tags=new[]{" 联培 ","实习","联培"} });
            Check(Labels(app.Snapshot(),"recentStudentLabels").SequenceEqual(new[]{"联培","实习","标签1","标签2","标签3","标签4","标签5","标签6"}),"student_labels_trim_deduplicate_promote_and_cap_8",passed);
            await Do("student.save",new { id="legacy-student",name="旧版验证",tags=Array.Empty<string>() });
            Check(Labels(app.Snapshot(),"recentStudentLabels")[0]=="联培", "removing_profile_tags_preserves_recent_choices",passed);
            for(var i=1;i<=7;i++)await Do("alarm.save",new { scopeType="global",title=$"提醒{i}",when=DateTimeOffset.Now.AddDays(1).ToString("O"),repeat="once" });
            var alarmId=Id(Rows(app.Snapshot(),"alarms")[1]);
            await Do("alarm.save",new { id=alarmId,scopeType="global",title=" 提醒3 ",when=DateTimeOffset.Now.AddDays(2).ToString("O"),repeat="once" });
            expectedAlarmLabels=new[]{"提醒3","提醒7","提醒6","提醒5","提醒4","提醒2"};
            Check(Labels(app.Snapshot(),"recentGlobalAlarmLabels").SequenceEqual(expectedAlarmLabels),"global_titles_trim_deduplicate_promote_and_cap_6",passed);
            await Do("alarm.save",new { scopeType="student",studentId="legacy-student",title="个人提醒不进入全局",when=DateTimeOffset.Now.AddDays(1).ToString("O"),repeat="daily" });
            Check(Labels(app.Snapshot(),"recentGlobalAlarmLabels").SequenceEqual(expectedAlarmLabels),"personal_alarm_does_not_change_global_recent_labels",passed);
            await Do("project.save",new { name="共享验证",studentIds=new[]{"legacy-student",studentB} });
            project=Id(Rows(app.Snapshot(),"projects")[0]);
            await Do("task.add",new { scopeType="project",scopeId=project,title="共享任务",start="2026-09-13",due="2026-09-17" });
            sharedTask=Id(Rows(app.Snapshot(),"tasks")[0]);
            await Do("task.add",new { scopeType="student",scopeId="legacy-student",title="个人任务" });
            personalTask=Id(Rows(app.Snapshot(),"tasks")[1]);
            await Do("chat.send",new { scopeType="student",scopeId="legacy-student",text="历史进展必须保留",files=new[]{new { name="归档.txt",mime="text/plain",dataBase64=Convert.ToBase64String(Encoding.UTF8.GetBytes("历史附件必须保留")) }} });
            var message=Rows(app.Snapshot(),"messages")[0]!;attachmentId=Id(message["attachments"]!.AsArray()[0]);historyCount=Rows(app.Snapshot(),"messages").Count;
            await Do("task.delete",new { id=sharedTask });
            var aiInput=(string)typeof(AppService).GetMethod("BuildInput",BindingFlags.NonPublic|BindingFlags.Instance)!.Invoke(app,new object[]{"project",project})!;
            Check(Rows(app.Snapshot(),"tasks").All(x=>Id(x)!=sharedTask)&&!aiInput.Contains("共享任务",StringComparison.Ordinal)&&Rows(app.Snapshot(),"messages").Count==historyCount,"task_delete_excludes_active_view_and_ai_input_keeps_history",passed);
            await Do("task.restore",new { id=sharedTask });
            var restored=Rows(app.Snapshot(),"tasks").First(x=>Id(x)==sharedTask)!;
            Check(restored["start"]!.GetValue<string>()=="2026-09-13"&&restored["studentIds"]!.AsArray().Count==2,"task_undo_preserves_identity_dates_and_members",passed);
            // A pending scope is injected locally to exercise cancellation without a network request.
            using var pendingSource=new CancellationTokenSource();
            var pending=(Dictionary<string,CancellationTokenSource>)typeof(AppService).GetField("pending",BindingFlags.NonPublic|BindingFlags.Instance)!.GetValue(app)!;
            pending["student:legacy-student"]=pendingSource;
            await Do("student.delete",new { id="legacy-student" });
            var removed=app.Snapshot();
            Check(Rows(removed,"students").Count==1&&Id(Rows(removed,"students")[0])==studentB&&Id(Rows(removed,"archivedStudents")[0])=="legacy-student"&&Rows(removed,"archivedStudents")[0]!["deletedAt"]!=null,"student_removed_from_roster_and_archived_with_name",passed);
            Check(pendingSource.IsCancellationRequested&&!pending.ContainsKey("student:legacy-student"),"deleting_student_cancels_pending_summary",passed);
            Check(Rows(removed,"tasks").Count==1&&Id(Rows(removed,"tasks")[0])==sharedTask&&Rows(removed,"tasks")[0]!["studentIds"]!.AsArray().Select(x=>x!.GetValue<string>()).SequenceEqual(new[]{studentB})&&Rows(removed,"projects")[0]!["studentIds"]!.AsArray().Select(x=>x!.GetValue<string>()).SequenceEqual(new[]{studentB}),"delete_removes_personal_task_and_project_membership_preserves_shared_task",passed);
            Check(Rows(removed,"alarms").All(x=>x!["scopeType"]!.GetValue<string>()=="global")&&Rows(removed,"messages").Count==historyCount&&File.ReadAllText(app.ResolveAttachment(attachmentId)!)=="历史附件必须保留","delete_removes_personal_alarms_preserves_all_history_and_attachment",passed);
            await Reject(app,"student.save",new { id="legacy-student",name="不可复活" },"archived_profile_read_only",passed);
            await Reject(app,"chat.send",new { scopeType="student",scopeId="legacy-student",text="不可追加" },"archived_chat_read_only",passed);
            await Reject(app,"summary.retry",new { scopeType="student",scopeId="legacy-student" },"archived_summary_cannot_restart",passed);
            await Reject(app,"task.add",new { scopeType="student",scopeId="legacy-student",title="不可新增" },"archived_student_cannot_add_tasks",passed);
            await Reject(app,"alarm.save",new { scopeType="student",studentId="legacy-student",title="不可提醒",when=DateTimeOffset.Now.ToString("O") },"archived_student_cannot_add_alarms",passed);
            await Reject(app,"task.restore",new { id=personalTask },"personal_task_cannot_restore_deleted_scope",passed);
            await Do("task.delete",new { id=sharedTask });
            expectedStudentLabels=Labels(app.Snapshot(),"recentStudentLabels");
            await Do("backup.export",new { path=backup });
            using(var zip=ZipFile.OpenRead(backup))Check(zip.Entries.Any(x=>x.FullName.StartsWith("attachments/"))&&zip.Entries.All(x=>!x.FullName.Contains("secret")),"backup_keeps_archive_attachment_without_credentials",passed);
            await Do("student.save",new { name="恢复时移除",tags=new[]{"恢复时移除标签"} });
            await Do("backup.import",new { path=backup });
            Check(Rows(app.Snapshot(),"students").Count==1&&Rows(app.Snapshot(),"archivedStudents").Count==1&&Labels(app.Snapshot(),"recentStudentLabels").SequenceEqual(expectedStudentLabels)&&Labels(app.Snapshot(),"recentGlobalAlarmLabels").SequenceEqual(expectedAlarmLabels)&&Rows(app.Snapshot(),"messages").Count==historyCount&&File.Exists(app.ResolveAttachment(attachmentId)),"backup_restore_keeps_mru_archived_profiles_messages_and_files",passed);
        }
        using(var reopened=new AppService(actualData))
        {
            Check(Rows(reopened.Snapshot(),"students").Count==1&&Rows(reopened.Snapshot(),"archivedStudents").Count==1&&Labels(reopened.Snapshot(),"recentStudentLabels").SequenceEqual(expectedStudentLabels)&&Labels(reopened.Snapshot(),"recentGlobalAlarmLabels").SequenceEqual(expectedAlarmLabels)&&Rows(reopened.Snapshot(),"messages").Count==historyCount&&File.Exists(reopened.ResolveAttachment(attachmentId)),"restart_preserves_mru_archive_and_attachment_links",passed);
            await reopened.HandleAsync("task.restore",Payload(new { id=sharedTask }));
            Check(Id(Rows(reopened.Snapshot(),"tasks")[0])==sharedTask&&Rows(reopened.Snapshot(),"tasks")[0]!["studentIds"]!.AsArray().Select(x=>x!.GetValue<string>()).SequenceEqual(new[]{studentB}),"backup_and_restart_preserve_task_undo_without_deleted_member",passed);
        }
    }
}
