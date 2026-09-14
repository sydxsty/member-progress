using System.IO.Compression;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace StudentProgress;

internal static class MonthlyChecks
{
    public static int Run(string dataDir)
    {
        var passed=new List<string>();var ownsDirectory=false;
        try
        {
            if(Directory.Exists(dataDir)&&Directory.EnumerateFileSystemEntries(dataDir).Any())return 1;
            Directory.CreateDirectory(dataDir);ownsDirectory=true;
            RunAsync(dataDir,passed).GetAwaiter().GetResult();
            File.WriteAllText(Path.Combine(dataDir,"monthly-result.json"),JsonSerializer.Serialize(new { ok=true,checks=passed,liveApiCalls=0 },new JsonSerializerOptions { WriteIndented=true }));
            return 0;
        }
        catch(Exception ex)
        {
            if(ownsDirectory)File.WriteAllText(Path.Combine(dataDir,"monthly-result.json"),JsonSerializer.Serialize(new { ok=false,checks=passed,error=ex.Message,liveApiCalls=0 },new JsonSerializerOptions { WriteIndented=true }));
            return 1;
        }
    }
    static JsonObject P(object value)=>JsonSerializer.SerializeToNode(value)!.AsObject();
    static JsonArray Rows(JsonObject state,string name)=>state[name]!.AsArray();
    static string Id(JsonNode? row)=>row!["id"]!.GetValue<string>();
    static DateTimeOffset Local(int year,int month,int day,int hour=9,int minute=15)
    {
        var value=new DateTime(year,month,day,hour,minute,0,DateTimeKind.Unspecified);
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
        File.WriteAllText(Path.Combine(actual,"initialized"),"monthly-check-no-settings-detection");
        File.WriteAllText(Path.Combine(actual,"state.json"),P(new
        {
            schemaVersion=1,students=Array.Empty<object>(),projects=Array.Empty<object>(),tasks=Array.Empty<object>(),messages=Array.Empty<object>(),
            alarms=new[]{new { id="legacy-monthly",scopeType="global",title="旧月度提醒",repeat="monthly",when=Local(2026,1,31).ToString("O"),enabled=true }},
            settings=new { baseUrl="http://127.0.0.1:1/v1",model="",apiMode="responses" },status=new { }
        }).ToJsonString());
        string remainingAlarm;
        using(var app=new AppService(actual))
        {
            async Task Do(string action,object value)=>await app.HandleAsync(action,P(value));
            JsonObject Student(string id)=>Rows(app.Snapshot(),"students").First(x=>Id(x)==id)!.AsObject();
            JsonObject Alarm(string id)=>Rows(app.Snapshot(),"alarms").First(x=>Id(x)==id)!.AsObject();
            async Task Poll(DateTimeOffset now)=>await app.HandleAsync("reminder.poll",P(new { now=now.ToString("O") }));
            Check(Alarm("legacy-monthly")["monthDay"]!.GetValue<int>()==31&&Alarm("legacy-monthly")["monthTime"]!.GetValue<string>().StartsWith("09:15"),"legacy_monthly_anchor_derived_without_schema_change",passed);
            await Do("alarm.delete",new { id="legacy-monthly" });
            await Do("student.save",new { name="验证甲",grade="研二",tags=new[]{"实习"},bio="保留研究背景" });
            var a=Id(Rows(app.Snapshot(),"students")[0]);
            var before=Student(a);await Do("student.patch",new { id=a,field="grade",value="博一" });
            var after=Student(a);before.Remove("grade");after.Remove("grade");
            Check(before.ToJsonString()==after.ToJsonString()&&Student(a)["grade"]!.GetValue<string>()=="博一","inline_grade_patch_preserves_all_other_fields",passed);
            await Do("student.patch",new { id=a,field="name",value=" 验证甲改名 " });
            await Do("student.patch",new { id=a,field="tags",value=new[]{" 联培 ","实习","联培"} });
            await Do("student.patch",new { id=a,field="bio",value="第一行\n第二行" });
            Check(Student(a)["name"]!.GetValue<string>()=="验证甲改名"&&Student(a)["tags"]!.AsArray().Count==2&&Student(a)["bio"]!.GetValue<string>()=="第一行\n第二行"&&app.Snapshot()["preferences"]!["recentStudentLabels"]![0]!.GetValue<string>()=="联培","inline_name_meta_bio_and_recent_labels",passed);
            using var pendingSource=new CancellationTokenSource();
            var pending=(Dictionary<string,CancellationTokenSource>)typeof(AppService).GetField("pending",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(app)!;
            pending["student:"+a]=pendingSource;
            await Do("student.patch",new { id=a,field="currentSummary",value="教师确认的当前进展" });
            Check(pendingSource.IsCancellationRequested&&!pending.ContainsKey("student:"+a)&&Student(a)["currentSummary"]!.GetValue<string>()=="教师确认的当前进展"&&Student(a)["summaryStatus"]!.GetValue<string>()=="idle"&&Rows(app.Snapshot(),"messages").Count==0,"manual_summary_invalidates_pending_ai_without_editing_history",passed);
            await Reject(app,"student.patch",new { id=a,field="name",value="  " },"inline_blank_name_rejected_without_data_loss",passed);
            await Reject(app,"student.patch",new { id=a,field="id",value="replacement" },"inline_unknown_field_rejected",passed);
            await Reject(app,"student.patch",new { id=a,field="tags",value="实习" },"inline_meta_requires_string_list",passed);

            async Task<string> Monthly(DateTimeOffset when,bool personal=false)
            {
                await Do("alarm.save",new { scopeType=personal?"student":"global",studentId=personal?a:null,title="月度验证",when=when.ToString("O"),repeat="monthly" });
                return Id(Rows(app.Snapshot(),"alarms").Last());
            }
            var nonLeap=await Monthly(Local(2026,1,31));await Poll(Local(2026,1,31));
            Check(DateTimeOffset.Parse(Alarm(nonLeap)["when"]!.GetValue<string>())==Local(2026,2,28)&&Alarm(nonLeap)["monthDay"]!.GetValue<int>()==31,"monthly_jan31_to_nonleap_feb28",passed);
            await Do("alarm.save",new { id=nonLeap,scopeType="global",title="只改提醒文字",when=Alarm(nonLeap)["when"]!.GetValue<string>(),repeat="monthly" });
            await Poll(Local(2026,2,28));
            Check(DateTimeOffset.Parse(Alarm(nonLeap)["when"]!.GetValue<string>())==Local(2026,3,31),"monthly_returns_to_mar31_and_title_edit_keeps_anchor",passed);
            await Do("alarm.delete",new { id=nonLeap });
            var leap=await Monthly(Local(2028,1,31),true);await Poll(Local(2028,1,31));
            Check(DateTimeOffset.Parse(Alarm(leap)["when"]!.GetValue<string>())==Local(2028,2,29),"student_monthly_jan31_to_leap_feb29",passed);
            await Poll(Local(2028,2,29));Check(DateTimeOffset.Parse(Alarm(leap)["when"]!.GetValue<string>())==Local(2028,3,31),"student_monthly_leap_feb_returns_to_mar31",passed);
            await Do("alarm.delete",new { id=leap });
            var ninth=await Monthly(Local(2026,9,9));
            var caught=(await app.HandleAsync("reminder.poll",P(new { now=Local(2026,11,9).ToString("O") })))!.AsArray();
            var again=(await app.HandleAsync("reminder.poll",P(new { now=Local(2026,11,9).ToString("O") })))!.AsArray();
            Check(caught.Count==1&&again.Count==0&&DateTimeOffset.Parse(Alarm(ninth)["when"]!.GetValue<string>())==Local(2026,12,9),"monthly_ninth_catchup_claims_once_and_advances_strictly_past_now",passed);
            await Do("alarm.delete",new { id=ninth });
            var future=Local(DateTime.Now.Year+1,1,31);remainingAlarm=await Monthly(future);
            await Do("reminder.snooze",new { id=remainingAlarm,minutes=10 });
            var snoozed=Alarm(remainingAlarm);
            Check(snoozed["monthDay"]!.GetValue<int>()==31&&snoozed["monthTime"]!.GetValue<string>().StartsWith("09:15"),"monthly_snooze_keeps_day_and_wall_clock_anchor",passed);
            await Poll(DateTimeOffset.Parse(snoozed["when"]!.GetValue<string>()));
            Check(DateTimeOffset.Parse(Alarm(remainingAlarm)["when"]!.GetValue<string>())==future&&!Alarm(remainingAlarm).ContainsKey("monthlyResumeWhen"),"snooze_returns_to_original_regular_schedule",passed);
            await Do("alarm.save",new { id=remainingAlarm,scopeType="global",title="月度验证",when=Local(DateTime.Now.Year+1,2,9,17,0).ToString("O"),repeat="monthly" });
            Check(Alarm(remainingAlarm)["monthDay"]!.GetValue<int>()==9&&Alarm(remainingAlarm)["monthTime"]!.GetValue<string>().StartsWith("17:00"),"explicit_schedule_edit_resets_monthly_day_and_time",passed);

            await Do("student.save",new { name="待删除验证乙" });var b=Id(Rows(app.Snapshot(),"students")[1]);
            await Do("project.save",new { name="验证项目",studentIds=new[]{a,b} });var project=Id(Rows(app.Snapshot(),"projects")[0]);
            await Do("task.add",new { scopeType="student",scopeId=a,title="上下文保留任务" });
            await Do("task.add",new { scopeType="student",scopeId=a,title="上下文排除任务" });var removedTask=Id(Rows(app.Snapshot(),"tasks").Last());
            await Do("task.delete",new { id=removedTask });
            var fullText=new string('甲',1500)+"全文末尾必须保留";var fileText=new string('乙',5000)+"附件末尾必须保留";
            for(var i=0;i<4;i++)await Do("chat.send",new { scopeType="student",scopeId=a,text="记录"+i });
            await Do("chat.send",new { scopeType="student",scopeId=a,text=fullText,files=new[]{new { name="全文.txt",mime="text/plain",dataBase64=Convert.ToBase64String(Encoding.UTF8.GetBytes(fileText)) }} });
            await Do("chat.send",new { scopeType="student",scopeId=b,text="已删学生的完整历史" });
            await Do("chat.send",new { scopeType="project",scopeId=project,text="项目完整历史" });
            await Do("student.delete",new { id=b });
            await Reject(app,"student.patch",new { id=b,field="bio",value="不能修改" },"deleted_student_inline_edit_rejected",passed);
            const string secret="monthly-test-secret-only";
            typeof(AppService).GetField("cachedKey",BindingFlags.Instance|BindingFlags.NonPublic)!.SetValue(app,secret);
            var selection=P(new { includeRecent=true,includeReminders=true,references=new[]{new { scopeType="student",scopeId=a },new { scopeType="student",scopeId=a },new { scopeType="student",scopeId=b },new { scopeType="project",scopeId=project }} });
            var context=app.BuildAssistantContext(selection);var contextText=context.ToJsonString();
            Check(Rows(context,"students").Count==1&&Rows(context,"tasks").Count==1&&Rows(context,"reminders").Count==1&&Rows(context,"reminders")[0]!["monthDay"]!.GetValue<int>()==9,"assistant_context_active_profiles_tasks_and_all_reminders",passed);
            var recent=Rows(context,"recentArchives").First(x=>x!["scopeId"]!.GetValue<string>()==a)!;
            Check(recent["messages"]!.AsArray().Count==3&&recent["messages"]!.AsArray().Last()!["truncated"]!.GetValue<bool>()&&recent["messages"]!.AsArray().Last()!["text"]!.GetValue<string>().Contains("已截取")&&!recent.ToJsonString().Contains("extractedText")&&Rows(context,"recentArchives").All(x=>x!["scopeId"]!.GetValue<string>()!=b),"recent_context_last_three_with_explicit_truncation_and_file_names_only",passed);
            var whole=Rows(context,"referencedArchives").First(x=>x!["scopeId"]!.GetValue<string>()==a)!;
            Check(Rows(context,"referencedArchives").Count==3&&whole["messages"]!.AsArray().Count==5&&whole["messages"]!.AsArray().Last()!["text"]!.GetValue<string>()==fullText&&whole["messages"]!.AsArray().Last()!["attachments"]![0]!["extractedText"]!.GetValue<string>()==fileText&&Rows(context,"referencedArchives").First(x=>x!["scopeId"]!.GetValue<string>()==b)!["archived"]!.GetValue<bool>(),"whole_archive_deduplicated_complete_with_full_extracted_text_and_deleted_student",passed);
            Check(!contextText.Contains(secret)&&!contextText.Contains("storedName")&&!contextText.Contains("dataRoot")&&!contextText.Contains("dataBase64")&&!contextText.Contains(actual)&&app.GetAssistantConnection()["key"]!.GetValue<string>()==secret&&!app.Snapshot().ToJsonString().Contains(secret),"assistant_context_omits_secrets_paths_base64_connection_is_private",passed);
            Rows(context,"students")[0]!["name"]="不得修改原数据";
            Check(Student(a)["name"]!.GetValue<string>()=="验证甲改名","assistant_context_is_detached_from_state",passed);
            var excluded=app.BuildAssistantContext(P(new { includeRecent=false,includeReminders=false }));
            var rejected=false;try{app.BuildAssistantContext(P(new { references=new[]{new { scopeType="student",scopeId="missing" }} }));}catch(InvalidOperationException){rejected=true;}
            Check(Rows(excluded,"recentArchives").Count==0&&Rows(excluded,"reminders").Count==0&&rejected,"assistant_selection_flags_and_unknown_reference_rejection",passed);

            app.RegisterBackupFiles(new[]{"assistant/history.json","assistant/settings.json"});
            var badPathsRejected=0;
            foreach(var path in new[]{"../outside.json","C:/outside.json","assistant/runtime/tool.exe","assistant/secret.dat"})
                try{app.RegisterBackupFiles(new[]{path});}catch(InvalidOperationException){badPathsRejected++;}
            Check(badPathsRejected==4,"registered_backup_paths_reject_escape_runtime_and_credentials",passed);
            var history=Path.Combine(actual,"assistant","history.json");var settings=Path.Combine(actual,"assistant","settings.json");
            Directory.CreateDirectory(Path.GetDirectoryName(history)!);
            File.WriteAllText(history,"{\"messages\":[\"原助手历史\"]}");File.WriteAllText(settings,"{\"autoUpdate\":false}");
            var registeredBackup=Path.Combine(dataDir,"registered-backup.zip");
            await Do("backup.export",new { path=registeredBackup });
            using(var zip=ZipFile.OpenRead(registeredBackup))Check(zip.Entries.Any(x=>x.FullName=="assistant/history.json")&&zip.Entries.Any(x=>x.FullName=="assistant/settings.json")&&zip.Entries.All(x=>!x.FullName.Contains("runtime")&&!x.FullName.Contains("secret")),"backup_includes_only_registered_assistant_files",passed);
            File.WriteAllText(history,"{\"messages\":[\"恢复失败应保留\"]}");File.WriteAllText(settings,"{\"autoUpdate\":true}");
            var beforeFailedRestore=app.Snapshot().ToJsonString();var failed=false;
            using(var blockedState=new FileStream(Path.Combine(actual,"state.tmp"),FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None))
            {
                try{await Do("backup.import",new { path=registeredBackup });}catch(IOException){failed=true;}
            }
            Check(failed&&File.ReadAllText(history).Contains("恢复失败应保留")&&File.ReadAllText(settings).Contains("true")&&app.Snapshot().ToJsonString()==beforeFailedRestore,"restore_failure_rolls_back_registered_files_and_core_state",passed);
            await Do("backup.import",new { path=registeredBackup });
            Check(File.ReadAllText(history).Contains("原助手历史")&&File.ReadAllText(settings).Contains("false"),"backup_restore_recovers_registered_assistant_files",passed);
            var oldBackup=Path.Combine(dataDir,"legacy-without-assistant.zip");
            using(var original=ZipFile.OpenRead(registeredBackup))
            using(var older=ZipFile.Open(oldBackup,ZipArchiveMode.Create))
                foreach(var entry in original.Entries.Where(x=>!x.FullName.StartsWith("assistant/")))
                {
                    using var from=entry.Open();using var to=older.CreateEntry(entry.FullName).Open();from.CopyTo(to);
                }
            File.WriteAllText(history,"{\"messages\":[\"旧备份应保留现有助手历史\"]}");
            await Do("backup.import",new { path=oldBackup });
            Check(File.ReadAllText(history).Contains("旧备份应保留现有助手历史"),"old_backup_without_registered_files_preserves_current_assistant_history",passed);
        }
        using(var reopened=new AppService(actual))Check(Rows(reopened.Snapshot(),"alarms").First(x=>Id(x)==remainingAlarm)!["monthDay"]!.GetValue<int>()==9&&Rows(reopened.Snapshot(),"alarms").First(x=>Id(x)==remainingAlarm)!["monthTime"]!.GetValue<string>().StartsWith("17:00"),"monthly_anchor_survives_restart",passed);
    }
}
