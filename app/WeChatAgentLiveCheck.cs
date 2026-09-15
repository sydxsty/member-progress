using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using StudentProgress.WeChat;

namespace StudentProgress;

// Opt-in, one native Codex invocation. Only synthetic chat/files and an isolated calendar are sent.
internal static class WeChatAgentLiveCheck
{
    internal static int Run(string emptyDataDir,string productionConnectionDataDir)
    {
        string directory=Path.GetFullPath(emptyDataDir),production=Path.GetFullPath(productionConnectionDataDir);
        if(directory.Equals(production,StringComparison.OrdinalIgnoreCase)||directory.StartsWith(production+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase)||production.StartsWith(directory+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase))return 1;
        if(Directory.Exists(directory)&&Directory.EnumerateFileSystemEntries(directory).Any())return 1;
        Directory.CreateDirectory(directory);
        var report=new JsonObject{["ok"]=false,["connectionRequests"]=0,["agentInvocationsRequested"]=0,["checks"]=new JsonArray(),["syntheticSourcesOnly"]=true};string key="";
        try
        {
            // Deliberately do not construct an AppService for the production directory.
            var productionState=ReadJson(Path.Combine(production,"state.json"));var settings=productionState["settings"]?.AsObject()??throw new InvalidOperationException("生产连接设置不存在。");
            byte[] protectedKey=File.ReadAllBytes(Path.Combine(production,"secret.dat"));key=Encoding.UTF8.GetString(ProtectedData.Unprotect(protectedKey,null,DataProtectionScope.CurrentUser));
            var connection=new JsonObject{["baseUrl"]=S(settings,"baseUrl"),["model"]=S(settings,"model"),["apiMode"]=S(settings,"apiMode","responses"),["key"]=key};
            if(key.Length==0||S(connection,"model").Length==0||S(connection,"apiMode")!="responses")throw new InvalidOperationException("需要已配置的 Responses 连接。");
            string beforeBusiness=BusinessHash(productionState);var keyFingerprint=SHA256.HashData(protectedKey);
            RunAsync(directory,connection,report).GetAwaiter().GetResult();
            bool businessUnchanged=beforeBusiness==BusinessHash(ReadJson(Path.Combine(production,"state.json")));
            bool credentialUnchanged=CryptographicOperations.FixedTimeEquals(keyFingerprint,SHA256.HashData(File.ReadAllBytes(Path.Combine(production,"secret.dat"))));
            report["productionBusinessUnchanged"]=businessUnchanged;report["productionCredentialsUnchanged"]=credentialUnchanged;
            if(!businessUnchanged||!credentialUnchanged)throw new InvalidOperationException("检查期间生产数据或连接文件发生变化，需要单独核对来源。");
            report["ok"]=true;Write();return 0;
        }
        catch(Exception e)
        {
            string error=e is InvalidOperationException?e.Message:"联调失败："+e.GetType().Name;
            if(key.Length>0)error=error.Replace(key,"[REDACTED]",StringComparison.Ordinal);
            report["error"]=error;Write();return 1;
        }
        void Write(){var json=report.ToJsonString(new JsonSerializerOptions(JsonSerializerOptions.Default){WriteIndented=true});if(key.Length>0)json=json.Replace(key,"[REDACTED]",StringComparison.Ordinal);File.WriteAllText(Path.Combine(directory,"wechat-agent-live-result.json"),json,new UTF8Encoding(false));}
    }
    static string S(JsonNode? n,string field,string fallback="")=>n?[field] is JsonValue value&&value.TryGetValue<string>(out var text)?text:fallback;
    static JsonObject ReadJson(string path)=>JsonNode.Parse(File.ReadAllText(path))?.AsObject()??throw new InvalidDataException("设置格式无效。");
    static string BusinessHash(JsonObject state)
    {
        var business=new JsonObject();foreach(string field in new[]{"students","projects","tasks","messages","alarms","archivedStudents"})business[field]=state[field]?.DeepClone();return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(business.ToJsonString())));
    }
    static async Task RunAsync(string directory,JsonObject connection,JsonObject report)
    {
        var checks=report["checks"]!.AsArray();void Check(bool value,string label){if(!value)throw new InvalidOperationException(label);checks.Add(label);}
        var data=Path.Combine(directory,"isolated-data");Directory.CreateDirectory(data);File.WriteAllText(Path.Combine(data,"initialized"),"isolated-wechat-agent-no-settings-detection");
        const string member="fixture-current-person";var china=TimeZoneInfo.FindSystemTimeZoneById("China Standard Time");var now=TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow,china);var tomorrow=DateOnly.FromDateTime(now.DateTime).AddDays(1);var finalTime=new DateTimeOffset(tomorrow.ToDateTime(new TimeOnly(15,0)),TimeSpan.FromHours(8));var originalTime=finalTime.AddHours(-1);
        var initial=new JsonObject{
            ["schemaVersion"]=1,["students"]=new JsonArray(new JsonObject{["id"]=member,["name"]="个人（隔离联调）",["grade"]="",["tags"]=new JsonArray(),["bio"]="",["currentSummary"]="原有手动进展",["summaryStatus"]="idle"}),
            ["projects"]=new JsonArray(),["tasks"]=new JsonArray(),["messages"]=new JsonArray(),["alarms"]=new JsonArray(),
            ["settings"]=new JsonObject{["currentStudentId"]=member,["baseUrl"]="http://127.0.0.1:1/v1",["model"]="",["apiMode"]="responses"},["status"]=new JsonObject()};
        File.WriteAllText(Path.Combine(data,"state.json"),initial.ToJsonString(),new UTF8Encoding(false));
        var wxRoot=Path.Combine(data,"wechat");Directory.CreateDirectory(wxRoot);File.WriteAllText(Path.Combine(wxRoot,"config.json"),new JsonObject{["enabled"]=false,["checkReminders"]=false,["checkMinutes"]=60,["selectedConversationIds"]=new JsonArray(),["bindings"]=new JsonArray()}.ToJsonString());
        using var app=new AppService(data);
        using var wx=new WeChatService(data,AppContext.BaseDirectory,app.Snapshot,app.ImportWeChatArchive);
        int connections=0;wx.ConfigureAgent(()=>app.BuildAssistantContext(new(){["includeReminders"]=true,["includeRecent"]=false}),()=>{int count=Interlocked.Increment(ref connections);report["connectionRequests"]=count;report["agentInvocationsRequested"]=count;if(count!=1)throw new InvalidOperationException("本次联调只允许一次 Codex 调用。");return connection.DeepClone().AsObject();},app.CreateAssistantReminders);
        const string account="synthetic-account",friend="synthetic-friend";string conversation=WeChatStore.Stable(account,friend);
        wx.Store.SaveConversation(new(){["id"]=conversation,["accountId"]=account,["externalId"]=friend,["name"]="与成员不同的合成微信备注",["kind"]="friend"});
        wx.SaveConfiguration(new(){["enabled"]=false,["checkReminders"]=false,["selectedConversationIds"]=new JsonArray(conversation)});
        JsonObject Message(string id,string text,int minutes,bool self=false,string kind="text")=>new(){["id"]=id,["accountId"]=account,["conversationId"]=conversation,["senderId"]=self?account:friend,["senderName"]=self?"个人（隔离联调）":"合成联系人",["isSelf"]=self,["kind"]=kind,["text"]=text,["rawContent"]=text,["when"]=now.AddMinutes(minutes).ToString("O")};
        wx.Store.UpsertMessage(Message("synthetic-original-time","明天14点开项目讨论会，请你参加。",-5));
        wx.Store.UpsertMessage(Message("synthetic-final-file","之前14点作废，最终时间和地点以附件为准。",-4,kind:"file"));
        wx.Store.UpsertMessage(Message("synthetic-confirmation","收到，我会按最终附件参加。担心忘了时间和地点，需要一起提醒我。",-3,self:true));
        string fileText="项目讨论最终安排\n日期："+tomorrow.ToString("yyyy-MM-dd")+"\n开始时间：15:00（北京时间）\n地点：科技楼406\n原14:00安排已取消，以本安排为准。\n";
        byte[] fileBytes=Encoding.UTF8.GetBytes(fileText);string hash=Convert.ToHexString(SHA256.HashData(fileBytes)).ToLowerInvariant(),stored=hash+".txt";await File.WriteAllBytesAsync(Path.Combine(wxRoot,"files",stored),fileBytes);
        // No extracted text is seeded: final time exists exclusively in the original attached file.
        wx.Store.UpsertFile(new(){["id"]="synthetic-original-file",["messageId"]="synthetic-final-file",["name"]="最终会议安排.txt",["size"]=(long)fileBytes.Length,["type"]="file",["available"]=true},"",stored,hash,"");
        string beforeAnalysis=BusinessHash(app.Snapshot());var watch=System.Diagnostics.Stopwatch.StartNew();
        try
        {
            await wx.HandleAsync("wechat.suggestions.check",new());
            var deadline=DateTimeOffset.UtcNow.AddMinutes(12);JsonObject snapshot;
            while(true){snapshot=wx.Snapshot();if(snapshot["analysis"]?["running"]?.GetValue<bool>()!=true)break;if(DateTimeOffset.UtcNow>=deadline){await wx.CancelSuggestionsAsync();throw new InvalidOperationException("微信建议联调超过12分钟，已取消且不会重试模型。");}await Task.Delay(500);}
            report["milliseconds"]=watch.ElapsedMilliseconds;report["analysisError"]=S(snapshot["analysis"],"error");Check(connections==1,"exactly_one_native_agent_request");Check(S(snapshot["analysis"],"error").Length==0,"native_suggestion_pipeline_completed");
            Check(BusinessHash(app.Snapshot())==beforeAnalysis,"analysis_does_not_change_business_calendar_or_progress");
            var candidates=snapshot["suggestions"]!.AsArray().OfType<JsonObject>().Where(x=>S(x,"status")=="pending").ToArray();report["candidates"]=new JsonArray(candidates.Select(x=>(JsonNode?)x.DeepClone()).ToArray());
            var correct=candidates.Where(x=>S(x,"kind")=="reminder"&&DateTimeOffset.TryParse(S(x,"when"),out var when)&&when==finalTime).ToArray();Check(correct.Length==1,"original_file_final_time_resolves_to_tomorrow_1500");Check(!candidates.Any(x=>DateTimeOffset.TryParse(S(x,"when"),out var when)&&when==originalTime),"cancelled_1400_time_is_not_suggested");
            var candidate=correct[0];var ids=(candidate["sourceMessageIds"] as JsonArray??new()).Select(x=>x!.GetValue<string>()).ToArray();var known=new HashSet<string>{"synthetic-original-time","synthetic-final-file","synthetic-confirmation"};Check(ids.Length>0&&ids.All(known.Contains),"suggestion_ids_point_to_real_synthetic_source_messages");
            var refs=(candidate["sourceReferences"] as JsonArray??new()).DeepClone().AsArray();Check(refs.Count>0,"suggestion_retains_clickable_original_references");foreach(var reference in refs.OfType<JsonObject>()){var resolved=wx.GetReference(S(reference,"scopeId"));Check(S(resolved,"conversationId")==conversation,"source_reference_resolves_to_selected_conversation");}
            var diagnostic=ReadJson(Path.Combine(wxRoot,"suggestions.json"));int toolCalls=diagnostic["lastToolCalls"]?.GetValue<int>()??0;report["nativeToolCalls"]=toolCalls;Check(toolCalls>0,"native_agent_used_tools_to_read_original_materials");
            var evidence=await wx.WriteReferenceWorkspaceAsync(new JsonArray(wx.CreateReference(conversation)),Path.Combine(directory,"source-evidence"),CancellationToken.None);Check(evidence["originalFileCount"]!.GetValue<int>()==1,"original_file_remains_available_after_agent_analysis");
            var created=await wx.HandleAsync("wechat.suggestion.create",new(){["id"]=S(candidate,"id")});var alarms=app.Snapshot()["alarms"]!.AsArray();var alarm=alarms.OfType<JsonObject>().Single();Check(created["created"]?.GetValue<bool>()==true&&S(alarm,"scopeType")=="student"&&S(alarm,"studentId")==member&&DateTimeOffset.Parse(S(alarm,"when"))==finalTime,"one_click_creates_exact_current_person_reminder");
            var repeat=await wx.HandleAsync("wechat.suggestion.create",new(){["id"]=S(candidate,"id")});Check(repeat["replayed"]?.GetValue<bool>()==true&&app.Snapshot()["alarms"]!.AsArray().Count==1,"second_click_is_idempotent");
            Check(S(app.Snapshot()["students"]![0],"currentSummary")=="原有手动进展"&&app.Snapshot()["tasks"]!.AsArray().Count==0,"creating_suggestion_preserves_unrelated_business_fields");
            report["expectedWhen"]=finalTime.ToString("O");report["createdReminderCount"]=1;report["checksCount"]=checks.Count;
        }
        finally{await wx.CancelSuggestionsAsync();}
    }
}
