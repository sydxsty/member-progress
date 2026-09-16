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
            var progress = new List<CodexProgressEvent>();
            var events = new CodexEvents(true, progress.Add);
            events.SetThread("fixture-thread");
            events.AcceptNotification("item/completed", new JsonObject { ["item"] = new JsonObject { ["id"] = "comment", ["type"] = "agentMessage", ["phase"] = "commentary", ["text"] = "Working" } });
            events.AcceptNotification("item/completed", new JsonObject { ["item"] = new JsonObject { ["id"] = "answer", ["type"] = "agentMessage", ["phase"] = "final_answer", ["text"] = "Final answer" } });
            events.AcceptNotification("turn/completed", new JsonObject { ["turn"] = new JsonObject { ["id"] = "fixture-turn", ["status"] = "completed", ["items"] = new JsonArray() } });
            Check(events.Completed && events.Answer == "Final answer" && !events.Failed && progress.Any(e => e.Kind == "message.completed"), "native_final_and_commentary_are_separate");
            var failure = new CodexEvents();
            failure.AcceptNotification("turn/completed", new JsonObject { ["turn"] = new JsonObject { ["id"] = "failed-turn", ["status"] = "failed", ["error"] = new JsonObject { ["message"] = "failed" } } });
            Check(failure.Failed && failure.Error == "failed", "native_failure_preserved");
            failure.AcceptNotification("item/started", new JsonObject { ["item"] = new JsonObject { ["id"] = "command", ["type"] = "commandExecution", ["command"] = "fixture" } });
            Check(failure.UnexpectedTool, "disabled_native_tool_detected");
            var connection=new JsonObject{["model"]="gpt-6-test",["baseUrl"]="https://example.invalid/v1",["key"]="assistant-test-secret",["apiMode"]="responses"};
            var start=CodexRunner.CreateStartInfo("not-launched.exe",dataRoot,connection);
            var arguments=start.ArgumentList.ToArray();
            Check(arguments.Contains("app-server") && arguments.Contains("--stdio") && arguments.Contains("shell_tool") && arguments.Contains("unified_exec") && arguments.Contains("multi_agent") &&
                !string.Join(' ', arguments).Contains("assistant-test-secret") && start.Environment[CodexRunner.KeyEnvironmentName] == "assistant-test-secret" &&
                start.Environment["CODEX_HOME"] == Path.Combine(Path.GetFullPath(dataRoot), ".codex-state") &&
                start.Environment["CODEX_SQLITE_HOME"] == Path.Combine(Path.GetFullPath(dataRoot), ".codex-state", "sqlite"), "native_transport_private_state_and_child_provider_credential");
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
