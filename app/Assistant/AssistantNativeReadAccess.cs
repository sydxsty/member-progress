using System.Text.Json.Nodes;

namespace StudentProgress.Assistant;

internal sealed class AssistantNativeReadAccess(JsonArray descriptions,
    Func<string, JsonObject, string, CancellationToken, Task<JsonObject>> reader)
{
    internal async Task<string> RunAsync(CodexRunner runner, string executable, string workspace, JsonObject connection, string prompt, CancellationToken cancellation,
        Action<CodexProgressEvent>? progress = null)
    {
        string session = Path.Combine(Path.GetTempPath(), "MemberProgressCodex", Guid.NewGuid().ToString("N"));
        try
        {
            var result = await runner.RunConversationAsync(executable, workspace, session, connection, null, AssistantNativeTools.ReadInstructions,
                new JsonArray(new JsonObject { ["type"] = "text", ["text"] = prompt }), AssistantNativeTools.Reads(descriptions), async (call, ct) =>
                {
                    var d = descriptions.OfType<JsonObject>().SingleOrDefault(x => AssistantNativeTools.NativeName(AssistantJson.Text(x["name"])) == call.Name);
                    if (d == null) return CodexToolResult.Text("此后台分析仅提供查询工具。", false);
                    try { return CodexToolResult.Json(await reader(AssistantJson.Text(d["name"]), call.Arguments, workspace, ct).ConfigureAwait(false)); }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex) { return CodexToolResult.Text(ex is InvalidOperationException or ArgumentException ? ex.Message : "本次查询未完成。", false); }
                }, progress, cancellation).ConfigureAwait(false);
            return result.Answer;
        }
        finally { try { if (Directory.Exists(session)) Directory.Delete(session, true); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
    }
}
