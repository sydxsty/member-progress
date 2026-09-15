using System.Text.Json;
using System.Text.Json.Nodes;
using StudentProgress.WeChat;

namespace StudentProgress;

internal static class WeChatRevisionChecks
{
    internal static int Run(string directory)
    {
        if(Directory.Exists(directory)&&Directory.EnumerateFileSystemEntries(directory).Any())return 1;
        Directory.CreateDirectory(directory);var checks=new List<string>();bool ok=false;string? error=null;
        try { RunAsync(directory,checks).GetAwaiter().GetResult();ok=true; }
        catch(Exception e){error=e.ToString();}
        File.WriteAllText(Path.Combine(directory,"wechat-revision-result.json"),JsonSerializer.Serialize(new{ok,checks,count=checks.Count,error,modelCalls=0},new JsonSerializerOptions{WriteIndented=true}));return ok?0:1;
    }
    static JsonObject Obj(object o)=>JsonSerializer.SerializeToNode(o)!.AsObject();
    static string S(JsonNode? value,string key)=>value?[key]?.GetValue<string>()??"";
    static async Task RunAsync(string directory,List<string> checks)
    {
        void Check(bool condition,string name){if(!condition)throw new InvalidOperationException(name);checks.Add(name);}
        var legacy=Obj(new{enabled=false,bindings=new[]{new{conversationId="one",memberId="m1",archiveFrom="2026-09-15"},new{conversationId="two",memberId="m2",archiveFrom="2026-07-15"}}});
        var original=legacy.ToJsonString();var normalized=WeChatService.UpgradeGlobalArchiveDate(legacy);
        Check(S(normalized,"archiveFrom")=="2026-07-15","legacy_dates_use_earliest_global_cutoff");
        Check(normalized["bindings"]!.AsArray().All(x=>!x!.AsObject().ContainsKey("archiveFrom"))&&legacy.ToJsonString()==original,"remove_per_conversation_dates_without_mutating_original");
        var all=legacy.DeepClone().AsObject();all["bindings"]![0]!["archiveFrom"]="all";
        Check(S(WeChatService.UpgradeGlobalArchiveDate(all),"archiveFrom")=="all","any_legacy_all_history_preserves_all_history");
        all["archiveFrom"]="2026-08-01";Check(S(WeChatService.UpgradeGlobalArchiveDate(all),"archiveFrom")=="2026-08-01","explicit_global_date_takes_precedence");
        var module=Path.Combine(directory,"data","wechat");Directory.CreateDirectory(module);File.WriteAllText(Path.Combine(module,"config.json"),legacy.ToJsonString());
        var business=Obj(new{students=new[]{new{id="m1",name="测试甲"},new{id="m2",name="测试乙"}},projects=Array.Empty<object>()});var archived=new List<JsonObject>();
        using var service=new WeChatService(Path.Combine(directory,"data"),Path.Combine(directory,"no-runtime"),()=>business.DeepClone().AsObject(),p=>{archived.Add(p.DeepClone().AsObject());return new(){["messageId"]="saved"};});
        Check(File.ReadAllText(Path.Combine(module,"config.pre-global-date.json"))==original&&S(service.Configuration,"archiveFrom")=="2026-07-15","migration_persists_global_date_and_preserves_original_config");
        var now=DateTimeOffset.Now;
        service.Store.SaveConversation(Obj(new{id="one",accountId="account",externalId="friend-one",name="测试甲",kind="friend",sourceLastMessageAt=now.AddMinutes(-10).ToString("O")}));
        service.Store.SaveConversation(Obj(new{id="two",accountId="account",externalId="friend-two",name="测试乙",kind="friend",sourceLastMessageAt=now.ToString("O")}));
        service.Store.SaveConversation(Obj(new{id="none",accountId="account",externalId="no-messages",name="测试丙",kind="friend"}));
        var conversationRows=service.Store.Conversations();
        Check(S(conversationRows[0],"id")=="two"&&S(conversationRows[1],"id")=="one"&&conversationRows.Count==3,"unimported_conversations_sort_by_wechat_last_message_time_and_remain_selectable");
        service.SaveConfiguration(Obj(new{enabled=false,checkReminders=false,archiveFrom=DateTime.Today.ToString("yyyy-MM-dd"),selectedConversationIds=new[]{"one","two"},bindings=new[]{new{conversationId="one",memberId="m1"},new{conversationId="two",memberId="m2"}}}));
        Directory.CreateDirectory(Path.Combine(module,"files"));File.WriteAllText(Path.Combine(module,"files","original.txt"),"原件");
        foreach(var conversation in new[]{"one","two"})foreach(var age in new[]{0,2})
        {
            string id=conversation+age;service.Store.UpsertMessage(Obj(new{id,accountId="account",conversationId=conversation,senderId="sender",senderName="测试",isSelf=false,kind="file",text="文件",when=now.AddDays(-age).ToString("O")}));
            service.Store.UpsertFile(Obj(new{id="file-"+id,messageId=id,name=id+".txt",size=6L,type="file",available=true}),"","original.txt","hash-"+id,"");
        }
        await service.HandleAsync("wechat.binding.save",Obj(new{conversationId="one",memberId="m1",archiveFrom="all"}));
        Check(archived.Count==2&&archived.Select(p=>S(p,"scopeId")).Distinct().Count()==2,"one_global_cutoff_applies_to_both_members_and_ignores_legacy_row_override");
        service.SaveConfiguration(Obj(new{archiveFrom="all"}));
        await service.HandleAsync("wechat.binding.save",Obj(new{conversationId="one",memberId="m1"}));
        Check(archived.Count==4,"expanding_global_date_backfills_all_conversations_without_duplicates");
        var before=service.Configuration.ToJsonString();bool rejected=false;try{service.SaveConfiguration(Obj(new{enabled=true,archiveFrom="2026-02-30"}));}catch(InvalidOperationException){rejected=true;}
        Check(rejected&&before==service.Configuration.ToJsonString(),"invalid_global_date_does_not_partially_change_settings");
        var withImported=service.Store.Conversations();Check(S(withImported[0],"id")=="two"&&withImported.Any(x=>S(x,"id")=="none"&&x!["messageCount"]!.GetValue<long>()==0),"source_order_and_zero_message_settings_survive_import");
    }
}
