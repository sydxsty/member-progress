using System.Text.Json.Nodes;
using StudentProgress.Assistant;

namespace StudentProgress.Remote;

// Explicit isolated integration harness. Never used by normal application startup.
internal static class MobileTestHost
{
    public static int Run(string root, bool withAi)
    {
        if(!File.Exists(Path.Combine(root, ".allow-mobile-network-test"))) return 2;
        try
        {
            using var core = new AppService(root);
            if(!withAi) core.HandleAsync("settings.save", new JsonObject { ["baseUrl"] = "https://api.openai.com/v1", ["model"] = "", ["apiMode"] = "responses" }).GetAwaiter().GetResult();
            else core.HandleAsync("settings.detect", new JsonObject()).GetAwaiter().GetResult();
            if(core.Snapshot()["students"]!.AsArray().Count == 0)
            {
                foreach(var name in new[]{"联调成员甲", "联调成员乙"})
                    core.HandleAsync("student.save", new JsonObject { ["name"] = name }).GetAwaiter().GetResult();
                var id = core.Snapshot()["students"]![0]!["id"]!.GetValue<string>();
                core.HandleAsync("task.add", new JsonObject { ["scopeType"] = "student", ["scopeId"] = id, ["title"] = "联调任务", ["start"] = DateTime.Today.ToString("yyyy-MM-dd"), ["due"] = DateTime.Today.AddDays(2).ToString("yyyy-MM-dd") }).GetAwaiter().GetResult();
            }
            using var assistant = new AssistantService(root, AppContext.BaseDirectory, core.BuildAssistantContext, core.GetAssistantConnection, core.CreateAssistantReminders);
            var access = new MobileAccess(root);
            using var server = new MobileServer(core, assistant, AppContext.BaseDirectory, access.Login, access.Authorize);
            using var frp = new FrpClient(root, AppContext.BaseDirectory);
            var config = access.Configuration();
            server.Start(config.ListenPort); frp.Start(config);
            File.WriteAllText(Path.Combine(root, "network-ready.json"), new JsonObject { ["ready"] = server.IsRunning, ["port"] = server.ListenPort }.ToJsonString());
            var until = DateTimeOffset.Now.AddMinutes(60);
            while(DateTimeOffset.Now < until && !File.Exists(Path.Combine(root, "stop.txt")))
            {
                File.WriteAllText(Path.Combine(root, "network-status.json"), new JsonObject { ["running"] = server.IsRunning, ["frpConnected"] = frp.Connected, ["frpStatus"] = frp.Status }.ToJsonString());
                Thread.Sleep(1000);
            }
            return 0;
        }
        catch(Exception e)
        {
            File.WriteAllText(Path.Combine(root, "network-error.json"), new JsonObject { ["errorType"] = e.GetType().Name }.ToJsonString()); return 1;
        }
    }
}
