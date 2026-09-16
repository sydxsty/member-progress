using System.Text.Json.Nodes;
using StudentProgress.Assistant;

namespace StudentProgress.WeChat;

public sealed partial class WeChatService
{
    JsonObject analysisProgress = NewAnalysisProgress();
    long lastAnalysisPublish;
    string analysisActivityItem = "";
    public event Action? AnalysisProgressChanged;

    static JsonObject NewAnalysisProgress() => new()
    {
        ["running"] = false, ["phase"] = "idle", ["lookbackHours"] = 24,
        ["from"] = "", ["through"] = "", ["startedAt"] = "", ["updatedAt"] = "",
        ["messageCount"] = 0, ["conversationCount"] = 0, ["attachmentCount"] = 0,
        ["preparedMessages"] = 0, ["preparedFiles"] = 0,
        ["toolCalls"] = 0, ["activity"] = "", ["suggestionCount"] = 0, ["updatedSuggestionCount"] = 0,
        ["error"] = "", ["canRetry"] = false
    };
    public JsonObject AnalysisProgressSnapshot() { lock (suggestionGate) return analysisProgress.DeepClone().AsObject(); }
    void StartAnalysisProgress(DateTimeOffset at, int lookbackHours)
    {
        lock (suggestionGate)
        {
            analysisProgress = NewAnalysisProgress(); analysisActivityItem = "";
            analysisProgress["running"] = true; analysisProgress["phase"] = "selecting";
            analysisProgress["lookbackHours"] = lookbackHours; analysisProgress["from"] = at.AddHours(-lookbackHours).ToString("O");
            analysisProgress["through"] = at.ToString("O"); analysisProgress["startedAt"] = at.ToString("O");
        }
        PublishAnalysisProgress(force: true);
    }
    void SetAnalysisProgress(string phase, JsonObject? fields = null)
    {
        lock (suggestionGate)
        {
            analysisProgress["phase"] = phase;
            analysisProgress["running"] = phase is "selecting" or "preparing" or "analyzing" or "saving";
            analysisProgress["canRetry"] = phase is "error" or "cancelled";
            if (fields != null) foreach (var item in fields) analysisProgress[item.Key] = item.Value?.DeepClone();
        }
        PublishAnalysisProgress(force: true);
    }
    void OnSuggestionAgentProgress(CodexProgressEvent e)
    {
        bool changed = false;
        lock (suggestionGate)
        {
            if (suggestionCancellation == null || suggestionCancellation.IsCancellationRequested) return;
            if (e.Kind is "tool.started" or "tool.completed")
            {
                if (e.Kind == "tool.completed") analysisProgress["toolCalls"] = I(analysisProgress, "toolCalls", 0) + 1;
                else
                {
                    analysisActivityItem = "";
                    string title = Text(e.Data["title"]); if (title.Length == 0) title = Text(e.Data["name"]);
                    analysisProgress["activity"] = title.Length > 0 ? "正在核对资料 · " + title : "正在核对资料";
                }
                changed = true;
            }
            else if (e.Kind is "message.delta" or "message.completed" && Text(e.Data["phase"]) == "commentary")
            {
                string id = Text(e.Data["itemId"]);
                string text = e.Kind == "message.completed" ? Text(e.Data["text"]) :
                    (analysisActivityItem == id ? Text(analysisProgress["activity"]) : "") + Text(e.Data["delta"]);
                analysisActivityItem = id; analysisProgress["activity"] = text.Length > 240 ? text[..240] : text;
                changed = true;
            }
            else if (e.Kind == "context.compacting") { analysisProgress["activity"] = "正在整理核对上下文"; changed = true; }
        }
        if (changed) PublishAnalysisProgress(force: e.Kind == "tool.completed");
    }
    void OnAnalysisMaterialsProgress(long messages, int files)
    {
        lock (suggestionGate) { analysisProgress["preparedMessages"] = messages; analysisProgress["preparedFiles"] = files; }
        PublishAnalysisProgress(force: false);
    }
    void PublishAnalysisProgress(bool force)
    {
        bool publish;
        lock (suggestionGate)
        {
            long now = Environment.TickCount64;
            analysisProgress["updatedAt"] = DateTimeOffset.Now.ToString("O");
            publish = force || now - lastAnalysisPublish >= 200;
            if (publish) lastAnalysisPublish = now;
        }
        if (publish) try { AnalysisProgressChanged?.Invoke(); } catch (ObjectDisposedException) { }
    }
}
