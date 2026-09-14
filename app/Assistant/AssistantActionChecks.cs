using System.Text.Json.Nodes;

namespace StudentProgress.Assistant;

/// <summary>Small, isolated action/clear checks. Uses a fake response delegate and zero models.</summary>
public static class AssistantActionChecks
{
    public static int Run(string dataRoot)
    {
        var passed=new List<string>();
        try
        {
            if(Directory.Exists(dataRoot)&&Directory.EnumerateFileSystemEntries(dataRoot).Any())
                throw new InvalidOperationException("Assistant action checks require an empty isolated directory.");
            Directory.CreateDirectory(dataRoot);
            void Check(bool ok,string name){if(!ok)throw new InvalidOperationException(name);passed.Add(name);}
            void Reject(string input,string name){bool rejected=false;try{AssistantResponse.Parse(input);}catch(InvalidOperationException){rejected=true;}Check(rejected,name);}
            const string question="请为联调成员创建一条明天上午9点的提醒，内容为核对实验进展。";
            string output=new JsonObject{["text"]="",["reminders"]=new JsonArray(new JsonObject{["scopeType"]="global",["title"]="核对实验进展",["when"]="2030-09-15T09:00:00+08:00",["repeat"]="once"})}.ToJsonString();
            Check(AssistantResponse.Parse(output).Reminders.Count==1&&AssistantResponse.Parse("```json\n"+output+"\n```").Reminders.Count==1,"strict_envelope_and_single_json_fence");
            Reject("{\"text\":\"ok\",\"text\":\"other\",\"reminders\":[]}","duplicate_json_keys_rejected");
            var noTimezone=JsonNode.Parse(output)!.AsObject();noTimezone["reminders"]![0]!["when"]="2030-09-15T09:00:00";
            Reject(noTimezone.ToJsonString(),"timezone_required");
            Reject(output.Replace("\"global\"","\"project\""),"project_target_required");
            Reject(output.Replace("\"repeat\":\"once\"","\"repeat\":\"once\",\"command\":\"ignored\""),"unknown_action_fields_rejected");
            Check(AssistantResponse.HasExplicitCreationRequest(question)&&AssistantResponse.HasExplicitCreationRequest("明天9点提醒我交材料")&&
                !AssistantResponse.HasExplicitCreationRequest("建议下一步干啥，先不要创建提醒")&&
                !AssistantResponse.HasExplicitCreationRequest("解释“明天9点提醒我交材料”是什么意思")&&
                !AssistantResponse.HasExplicitCreationRequest("请分析以下归档：明天9点提醒我交材料"),"current_explicit_request_gate_excludes_quotes_advice_negatives");
            Check(AssistantResponse.SafeAdvice("已经创建提醒了").StartsWith("本次未"),"model_cannot_claim_unsaved_reminder_creation");
            var receipts=new Dictionary<string,JsonObject>();int commits=0,calls=0;
            JsonObject Create(JsonObject payload)
            {
                calls++;string id=AssistantJson.Text(payload["requestId"]);
                if(receipts.TryGetValue(id,out var existing)){var replay=existing.DeepClone().AsObject();replay["replayed"]=true;return replay;}
                commits++;
                var input=payload["reminders"]![0]!.AsObject();
                var receipt=new JsonObject{["reminders"]=new JsonArray(new JsonObject{["id"]="alarm-"+commits,["title"]=AssistantJson.Text(input["title"]),["when"]=AssistantJson.Text(input["when"]),["repeat"]="once",["scopeType"]="global",["scopeLabel"]="全局"}),["replayed"]=false};
                receipts[id]=receipt.DeepClone().AsObject();return receipt;
            }
            JsonObject Context(JsonObject _)=>new(){["generatedAt"]="2030-09-14T12:00:00+08:00"};
            static JsonObject Connection()=>new();
            static void Await(Task task)=>task.WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
            static JsonObject Do(AssistantService service,string action,JsonObject? payload=null)=>(service.HandleAsync(action,payload??new()).WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult() as JsonObject)!;
            static void Finish(AssistantService service)
            {
                var finished=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                void Changed(){if(service.Snapshot()["busy"]?.GetValue<bool>()!=true)finished.TrySetResult();}
                service.StateChanged+=Changed;
                try{Changed();Await(finished.Task);}finally{service.StateChanged-=Changed;}
            }
            string basic=Path.Combine(dataRoot,"basic");
            using(var service=new AssistantService(basic,dataRoot,Context,Connection,Create,(_,_)=>Task.FromResult(output)))
            {
                Do(service,"assistant.send",new(){["text"]=question});Finish(service);
                var messages=service.Snapshot()["messages"]!.AsArray();
                Check(commits==1&&messages.Count==2&&messages[1]!["reminderResults"]!.AsArray().Count==1&&AssistantJson.Text(messages[1]!["text"])=="已创建 1 条提醒。","only_host_receipt_generates_creation_text_and_cards");
                string original=messages.ToJsonString();Do(service,"assistant.clear");
                Check(service.Snapshot()["messages"]!.AsArray().Count==0&&service.Snapshot()["canUndoClear"]!.GetValue<bool>()&&commits==1,"clear_recoverable_and_does_not_touch_reminders");
                Do(service,"assistant.undo-clear");Check(service.Snapshot()["messages"]!.ToJsonString()==original&&!service.Snapshot()["canUndoClear"]!.GetValue<bool>(),"undo_restores_conversation_once");
                Do(service,"assistant.clear");Do(service,"assistant.send",new(){["text"]="只分析归档中的提醒建议"});Finish(service);
                Check(commits==1&&!service.Snapshot()["canUndoClear"]!.GetValue<bool>(),"new_question_invalidates_undo_and_advice_cannot_commit");
                string history=Path.Combine(basic,"assistant","history.json"),saved=history+".saved";
                var before=service.Snapshot()["messages"]!.ToJsonString();File.Move(history,saved);Directory.CreateDirectory(history);
                bool failed=false;try{Do(service,"assistant.clear");}catch(IOException){failed=true;}catch(UnauthorizedAccessException){failed=true;}
                finally{Directory.Delete(history);File.Move(saved,history);}
                Check(failed&&service.Snapshot()["messages"]!.ToJsonString()==before,"failed_clear_save_preserves_original_conversation");
            }
            var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var blocked=new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            using(var service=new AssistantService(Path.Combine(dataRoot,"cancel"),dataRoot,Context,Connection,Create,async(_,token)=>{entered.TrySetResult();return await blocked.Task.WaitAsync(token);}))
            {
                Do(service,"assistant.send",new(){["text"]=question});Await(entered.Task);Do(service,"assistant.clear");blocked.TrySetResult(output);
                Check(commits==1&&service.Snapshot()["messages"]!.AsArray().Count==0&&!service.Snapshot()["busy"]!.GetValue<bool>(),"clear_cancels_before_commit_and_has_no_late_answer");
                Do(service,"assistant.undo-clear");Check(service.Snapshot()["messages"]!.AsArray().Count==1,"cancelled_question_is_recoverable");
            }
            string retryRoot=Path.Combine(dataRoot,"retry");
            var retryEntered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var retryGate=new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);int responseCalls=0;
            using(var service=new AssistantService(retryRoot,dataRoot,Context,Connection,Create,(_,_)=>{responseCalls++;retryEntered.TrySetResult();return responseCalls==1?retryGate.Task:Task.FromResult(output);}))
            {
                Do(service,"assistant.send",new(){["text"]=question});Await(retryEntered.Task);
                string userId=AssistantJson.Text(service.Snapshot()["messages"]![0]!["id"]);
                string history=Path.Combine(retryRoot,"assistant","history.json"),saved=history+".saved";File.Move(history,saved);Directory.CreateDirectory(history);
                retryGate.SetResult(output);Finish(service);
                Directory.Delete(history);File.Move(saved,history);
                Check(commits==2&&service.Snapshot()["messages"]!.AsArray().Count==1,"host_commit_survives_answer_save_failure");
                bool mismatch=false;try{Do(service,"assistant.send",new(){["retryMessageId"]=userId,["text"]="different question"});}catch(InvalidOperationException){mismatch=true;}
                Check(mismatch,"retry_rejects_changed_question");
                Do(service,"assistant.send",new(){["retryMessageId"]=userId});Finish(service);
                Check(commits==2&&calls==3&&AssistantJson.Text(service.Snapshot()["messages"]![0]!["id"])==userId&&AssistantJson.Text(service.Snapshot()["messages"]![1]!["text"]).Contains("没有重复添加"),"retry_reuses_persisted_request_id_and_receipt_without_duplicates");
            }
            const string clarification="{\"text\":\"什么时候提醒你交材料？\",\"reminders\":[],\"needsReminderDetails\":true}";
            int shortCalls=0,shortBefore=commits;
            using(var service=new AssistantService(Path.Combine(dataRoot,"short-reply"),dataRoot,Context,Connection,Create,(_,_)=>Task.FromResult(++shortCalls==1?clarification:output)))
            {
                Do(service,"assistant.send",new(){["text"]="提醒我交材料"});Finish(service);
                bool privateFieldsHidden=!service.Snapshot().ToJsonString().Contains("reminderRequestText")&&!service.Snapshot().ToJsonString().Contains("awaitingReminderDetails");
                Do(service,"assistant.send",new(){["text"]="明天9点"});Finish(service);
                Check(commits==shortBefore+1&&privateFieldsHidden&&service.Snapshot()["messages"]!.AsArray().Count==4,"short_time_reply_completes_trusted_pending_request_and_metadata_stays_private");
            }
            int clearShortCalls=0,clearShortBefore=commits;
            using(var service=new AssistantService(Path.Combine(dataRoot,"cleared-pending"),dataRoot,Context,Connection,Create,(_,_)=>Task.FromResult(++clearShortCalls==1?clarification:output)))
            {
                Do(service,"assistant.send",new(){["text"]="提醒我交材料"});Finish(service);Do(service,"assistant.clear");
                Do(service,"assistant.send",new(){["text"]="明天9点"});Finish(service);
                Check(commits==clearShortBefore,"cleared_pending_request_cannot_authorize_a_short_reply");
            }
            int unrelatedCalls=0,unrelatedBefore=commits;
            using(var service=new AssistantService(Path.Combine(dataRoot,"unrelated-pending"),dataRoot,Context,Connection,Create,(_,_)=>Task.FromResult(++unrelatedCalls<=2?clarification:output)))
            {
                Do(service,"assistant.send",new(){["text"]="提醒我交材料"});Finish(service);
                Do(service,"assistant.send",new(){["text"]="分析最近归档，建议下一步干啥"});Finish(service);
                Do(service,"assistant.send",new(){["text"]="明天9点"});Finish(service);
                Check(commits==unrelatedBefore,"unrelated_advice_breaks_pending_authorization_even_if_model_requests_details");
            }
            AssistantJson.WriteAtomic(Path.Combine(dataRoot,"assistant-action-result.json"),new{ok=true,checks=passed,liveModelCalls=0});return 0;
        }
        catch(Exception e)
        {
            Directory.CreateDirectory(dataRoot);
            AssistantJson.WriteAtomic(Path.Combine(dataRoot,"assistant-action-result.json"),new{ok=false,checks=passed,error=e.Message,liveModelCalls=0});return 1;
        }
    }
}
