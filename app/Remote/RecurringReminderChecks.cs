using System.Text.Json;
using System.Text.Json.Nodes;

namespace StudentProgress.Remote;

/// <summary>Exercises occurrence cancellation against isolated data, with no model or network calls.</summary>
public static class RecurringReminderChecks
{
    public static int Run(string dataRoot)
    {
        var passed=new List<string>();
        try
        {
            if(Directory.Exists(dataRoot)&&Directory.EnumerateFileSystemEntries(dataRoot).Any())
                throw new InvalidOperationException("Recurring reminder checks require an empty isolated directory.");
            Directory.CreateDirectory(dataRoot);
            var coreRoot=Path.Combine(dataRoot,"core"); Directory.CreateDirectory(coreRoot);
            File.WriteAllText(Path.Combine(coreRoot,"initialized"),"1");
            void Check(bool ok,string label){if(!ok)throw new InvalidOperationException(label);passed.Add(label);}
            static string Text(JsonNode? node)=>node is JsonValue value&&value.TryGetValue<string>(out var text)?text:"";
            static JsonNode? Desktop(AppService core,string action,JsonObject payload)=>core.HandleAsync(action,payload).GetAwaiter().GetResult();
            static JsonObject Alarm(AppService core,string id)=>core.Snapshot()["alarms"]!.AsArray().OfType<JsonObject>().Single(x=>Text(x["id"])==id);
            static DateTimeOffset When(JsonObject alarm)=>DateTimeOffset.Parse(Text(alarm["when"]));
            static JsonObject Request(AppService core,string id,string when)=>new()
            {
                ["action"]="alarm.skip",["payload"]=new JsonObject{["id"]=id,["when"]=when},
                ["requestId"]=Guid.NewGuid().ToString("N"),["expectedVersion"]=Text(core.MobileSnapshot()["recordVersions"]!["alarm:"+id])
            };
            static bool PlanHas(AppService core,string id,DateTimeOffset when,DateTimeOffset now)
                =>core.MobileReminderPlan(now)["occurrences"]!.AsArray().OfType<JsonObject>().Any(x=>Text(x["reminderId"])==id&&When(x)==when);
            void Reject(Action action,string label,int? status=null)
            {
                var rejected=false;
                try{action();}catch(InvalidOperationException error){rejected=status==null||(error is MobileApiException api&&api.Status==status);}
                Check(rejected,label);
            }
            var year=DateTime.Now.Year+4;
            DateTimeOffset Local(int month,int day){var local=new DateTime(year,month,day,9,0,0);return new(local,TimeZoneInfo.Local.GetUtcOffset(local));}
            JsonObject? skipRequest=null; string weeklyId="";
            using(var core=new AppService(coreRoot))
            {
                string Create(string title,string repeat,DateTimeOffset when,int days=1,bool enabled=true)
                {
                    Desktop(core,"alarm.save",new(){["scopeType"]="global",["title"]=title,["repeat"]=repeat,["when"]=when.ToString("O"),["intervalDays"]=days,["enabled"]=enabled});
                    return Text(core.Snapshot()["alarms"]!.AsArray().OfType<JsonObject>().Single(x=>Text(x["title"])==title)["id"]);
                }
                var initial=Local(1,10); weeklyId=Create("每周进展","weekly",initial);
                skipRequest=Request(core,weeklyId,initial.ToString("O"));
                core.HandleMobileAction(skipRequest);
                var weekly=Alarm(core,weeklyId);
                Check(When(weekly)==initial.AddDays(7)&&weekly["enabled"]!.GetValue<bool>()&&!weekly.ContainsKey("lastTriggeredWhen"),
                    "skip_advances_weekly_without_disabling_or_claiming_notification");
                Check(!PlanHas(core,weeklyId,initial,initial.AddDays(-1))&&PlanHas(core,weeklyId,initial.AddDays(7),initial.AddDays(-1)),
                    "phone_plan_removes_cancelled_occurrence_and_keeps_next_week");
                core.HandleMobileAction(skipRequest);
                Check(When(Alarm(core,weeklyId))==initial.AddDays(7),"same_mobile_request_cannot_skip_two_weeks");
                Reject(()=>Desktop(core,"alarm.skip",new(){["id"]=weeklyId,["when"]=initial.ToString("O")}),"desktop_double_click_rejects_outdated_occurrence",409);
                var stale=skipRequest.DeepClone().AsObject();stale["requestId"]=Guid.NewGuid().ToString("N");
                Reject(()=>core.HandleMobileAction(stale),"stale_mobile_record_version_rejected",409);
                Reject(()=>core.HandleMobileAction(Request(core,weeklyId,initial.ToString("O"))),"fresh_version_with_old_occurrence_rejected",409);

                var monthlyId=Create("月底","monthly",Local(1,31));
                Desktop(core,"reminder.poll",new(){["now"]=Local(1,31).AddMinutes(1).ToString("O")});
                var feb=Local(2,DateTime.DaysInMonth(year,2));
                Check(When(Alarm(core,monthlyId))==feb,"monthly_regular_schedule_reaches_short_month");
                Desktop(core,"alarm.skip",new(){["id"]=monthlyId,["when"]=feb.ToString("O")});
                var monthly=Alarm(core,monthlyId);
                Check(When(monthly)==Local(3,31)&&monthly["monthDay"]!.GetValue<int>()==31&&DateTimeOffset.Parse(Text(monthly["lastTriggeredWhen"]))==Local(1,31),
                    "skip_february_preserves_31st_anchor_and_actual_delivery_history");
                Check(PlanHas(core,monthlyId,Local(1,31),Local(1,31).AddMinutes(1))&&!PlanHas(core,monthlyId,feb,Local(1,31).AddMinutes(1))
                    &&PlanHas(core,monthlyId,Local(3,31),Local(1,31).AddMinutes(1)),"mobile_plan_keeps_actual_history_but_omits_cancelled_month");
                var claimed=Desktop(core,"reminder.poll",new(){["now"]=feb.AddMinutes(1).ToString("O")})!.AsArray();
                Check(!claimed.OfType<JsonObject>().Any(x=>Text(x["id"])==monthlyId),"windows_does_not_fire_cancelled_monthly_occurrence");

                var snoozedId=Create("月末延后","monthly",Local(1,31));
                Desktop(core,"reminder.snooze",new(){["id"]=snoozedId,["minutes"]=10});
                var snoozed=Alarm(core,snoozedId);var snoozedWhen=When(snoozed);
                Desktop(core,"alarm.skip",new(){["id"]=snoozedId,["when"]=snoozedWhen.ToString("O")});
                var resumed=Alarm(core,snoozedId);
                Check(When(resumed)==Local(1,31)&&resumed["monthDay"]!.GetValue<int>()==31&&!resumed.ContainsKey("monthlyResumeWhen")
                    &&!PlanHas(core,snoozedId,snoozedWhen,DateTimeOffset.Now),"skip_snoozed_occurrence_resumes_regular_monthly_schedule");

                var dailyId=Create("每日","daily",Local(5,10));var intervalId=Create("每三天","interval",Local(5,10),3);
                Desktop(core,"alarm.skip",new(){["id"]=dailyId,["when"]=Local(5,10).ToString("O")});
                Desktop(core,"alarm.skip",new(){["id"]=intervalId,["when"]=Local(5,10).ToString("O")});
                Check(When(Alarm(core,dailyId))==Local(5,11)&&When(Alarm(core,intervalId))==Local(5,13),"daily_and_interval_cancellation_keep_their_original_spacing");
                var onceId=Create("单次","once",initial);var disabledId=Create("已停用","weekly",initial,enabled:false);
                Reject(()=>Desktop(core,"alarm.skip",new(){["id"]=onceId,["when"]=initial.ToString("O")}),"one_time_reminder_cannot_be_skipped_as_recurring");
                Reject(()=>Desktop(core,"alarm.skip",new(){["id"]=disabledId,["when"]=initial.ToString("O")}),"disabled_reminder_cannot_be_skipped");
            }
            using(var core=new AppService(coreRoot))
            {
                var before=When(Alarm(core,weeklyId));core.HandleMobileAction(skipRequest!);
                Check(When(Alarm(core,weeklyId))==before,"skip_receipt_survives_restart");
            }
            File.WriteAllText(Path.Combine(dataRoot,"recurring-reminder-result.json"),JsonSerializer.Serialize(new{ok=true,checks=passed,liveModelCalls=0},new JsonSerializerOptions{WriteIndented=true}));
            return 0;
        }
        catch(Exception error)
        {
            Directory.CreateDirectory(dataRoot);
            File.WriteAllText(Path.Combine(dataRoot,"recurring-reminder-result.json"),JsonSerializer.Serialize(new{ok=false,checks=passed,error=error.Message,liveModelCalls=0},new JsonSerializerOptions{WriteIndented=true}));
            return 1;
        }
    }
}
