using System.Text.Json;
using System.Text.Json.Nodes;

namespace StudentProgress.Assistant;

/// <summary>Bounded offline checks; never launches a model or touches production data.</summary>
public static class AssistantChecks
{
    public static int Run(string dataRoot)
    {
        var passed=new List<string>();
        try
        {
            if(Directory.Exists(dataRoot)&&Directory.EnumerateFileSystemEntries(dataRoot).Any())
                throw new InvalidOperationException("Assistant checks require an empty isolated directory.");
            Directory.CreateDirectory(dataRoot);
            void Check(bool ok,string name){if(!ok)throw new InvalidOperationException(name);passed.Add(name);}
            var events=new CodexEvents();
            events.Accept("not JSON");
            events.Accept("{\"type\":\"item.completed\",\"item\":{\"type\":\"agent_message\",\"text\":\"Interim\"}}");
            events.Accept("{\"type\":\"item.completed\",\"item\":{\"type\":\"agent_message\",\"text\":\"Final answer\"}}");
            events.Accept("{\"type\":\"turn.completed\"}");
            Check(events.Completed&&events.Answer=="Final answer"&&!events.Failed,"jsonl_final_natural_language_answer");
            var failure=new CodexEvents();failure.Accept("{\"type\":\"turn.failed\",\"error\":{\"message\":\"failed\"}}");
            Check(failure.Failed&&failure.Error=="failed","jsonl_error_preserved");
            failure.Accept("{\"type\":\"item.started\",\"item\":{\"type\":\"command_execution\"}}");
            Check(failure.UnexpectedTool,"unexpected_tool_detected");
            string whole=new string('x',12000)+"FULL_REFERENCE_END";
            var context=new JsonObject
            {
                ["generatedAt"]=DateTimeOffset.Now.ToString("O"),
                ["referencedArchives"]=new JsonArray(new JsonObject{["scopeType"]="student",["scopeId"]="a",["title"]="A",["messages"]=new JsonArray(new JsonObject{["text"]=whole})}),
                ["recentArchives"]=new JsonArray(new JsonObject{["scopeType"]="student",["scopeId"]="b",["messages"]=new JsonArray(new JsonObject{["text"]=new string('z',30000)})})
            };
            string before=context.ToJsonString();
            string prompt=AssistantPrompt.Build("Question",[],context);
            Check(prompt.Contains(whole)&&context.ToJsonString()==before,"explicit_reference_complete_and_context_detached");
            context["referencedArchives"]![0]!["messages"]![0]!["text"]=new string('x',400000);
            bool rejected=false;try{AssistantPrompt.Build("Question",[],context);}catch(InvalidOperationException){rejected=true;}
            Check(rejected,"oversize_reference_rejected_not_silently_shortened");
            var connection=new JsonObject{["model"]="gpt-6-test",["baseUrl"]="https://example.invalid/v1",["key"]="assistant-test-secret",["apiMode"]="responses"};
            var start=CodexRunner.CreateStartInfo("not-launched.exe",dataRoot,connection);
            var arguments=start.ArgumentList.ToArray();
            Check(arguments.Contains("--ignore-user-config")&&arguments.Contains("--ignore-rules")&&arguments.Contains("read-only")&&arguments.Contains("never")&&arguments.Contains("shell_tool")&&arguments.Contains("unified_exec")&&arguments.Contains("multi_agent")&&!string.Join(' ',arguments).Contains("assistant-test-secret")&&start.Environment[CodexRunner.KeyEnvironmentName]=="assistant-test-secret","readonly_cli_and_secret_only_in_child_environment");
            var store=new AssistantStore(Path.Combine(dataRoot,"store"));
            var saved=new AssistantState{AutoUpdate=true,Interrupted=true,Messages=[new(){Role="user",Text="Preserved question",References=[new(){ScopeId="a",Title="A"}]}]};
            store.Save(saved);var restored=store.Load();
            Check(restored.AutoUpdate&&restored.Messages.Single().Text=="Preserved question"&&!restored.Interrupted&&restored.Error.Contains("中断"),"separate_history_settings_and_interrupted_request_recovery");
            Check(!File.ReadAllText(Path.Combine(dataRoot,"store","history.json")).Contains("autoUpdate")&&!File.ReadAllText(Path.Combine(dataRoot,"store","settings.json")).Contains("Preserved question"),"backup_files_have_separate_responsibilities");
            bool traversal=false;try{CodexRuntime.SafeEntryPath(dataRoot,"package/../../outside.exe");}catch(InvalidDataException){traversal=true;}
            bool host=false;try{CodexRuntime.ValidateTarball(new Uri("https://example.invalid/codex.tgz"),"0.154.0");}catch(InvalidDataException){host=true;}
            Check(traversal&&host&&CodexRuntime.ParseIntegrity("sha512-"+Convert.ToBase64String(new byte[64])).Length==64,"updater_rejects_traversal_and_nonofficial_sources_requires_sha512");
            AssistantJson.WriteAtomic(Path.Combine(dataRoot,"assistant-check-result.json"),new{ok=true,checks=passed,liveModelCalls=0});
            return 0;
        }
        catch(Exception e)
        {
            Directory.CreateDirectory(dataRoot);
            AssistantJson.WriteAtomic(Path.Combine(dataRoot,"assistant-check-result.json"),new{ok=false,checks=passed,error=e.Message,liveModelCalls=0});
            return 1;
        }
    }
}
