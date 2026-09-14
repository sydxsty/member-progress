using System.Text.Json.Nodes;
using StudentProgress.Remote;

namespace StudentProgress.Assistant;

public sealed partial class AssistantService
{
    public JsonObject MobileSnapshot()
    {
        lock (sync)
        {
            var snapshot = Snapshot();
            snapshot.Remove("runtime");
            snapshot["version"] = MobileProtocol.Fingerprint(new JsonObject
            {
                ["visible"] = snapshot.DeepClone(),
                ["cleared"] = System.Text.Json.JsonSerializer.SerializeToNode(state.ClearedMessages, AssistantJson.Options)
            });
            return snapshot;
        }
    }
    public async Task<JsonObject> HandleMobileAsync(JsonObject request)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        var payload = MobileProtocol.ReadRequest(request);
        var action = MobileProtocol.Text(request, "action");
        if (action == "assistant.send")
        {
            MobileProtocol.Only(payload, "text", "retryMessageId", "includeReminders", "includeRecent", "references");
            MobileProtocol.StringFields(payload, "text", "retryMessageId");
            MobileProtocol.BooleanFields(payload, "includeReminders", "includeRecent");
            if (payload.ContainsKey("references"))
            {
                if (payload["references"] is not JsonArray refs || refs.Count > 40 || refs.Any(x => x is not JsonObject))
                    throw new MobileApiException(400, "归档引用格式不正确。");
                foreach (var reference in refs.OfType<JsonObject>())
                {
                    MobileProtocol.Only(reference, "scopeType", "scopeId", "title");
                    MobileProtocol.StringFields(reference, "scopeType", "scopeId", "title");
                }
            }
            StartRequest(payload, request);
        }
        else
        {
            MobileProtocol.Only(payload);
            switch (action)
            {
                case "assistant.clear": await ClearAsync(request).ConfigureAwait(false); break;
                case "assistant.undo-clear": UndoClear(request); break;
                case "assistant.cancel": await CancelMobileAsync(request).ConfigureAwait(false); break;
                default: throw new MobileApiException(400, "手机端不支持此 GPT 操作。");
            }
        }
        return MobileSnapshot();
    }
    bool ReplayMobile(JsonObject request)
    {
        var id = MobileProtocol.Text(request, "requestId");
        var receipt = state.MobileReceipts.FirstOrDefault(x => x.RequestId == id);
        if (receipt == null) return false;
        if (receipt.Fingerprint != MobileProtocol.Fingerprint(request))
            throw new MobileApiException(409, "这次请求编号对应的内容不同，请重新提交。", "request_mismatch");
        return true;
    }
    void CheckMobileVersion(JsonObject request, bool required = true)
        => MobileProtocol.CheckVersion(request, MobileProtocol.Text(MobileSnapshot(), "version"), required);
    static void AddMobileReceipt(AssistantState next, JsonObject? request)
    {
        if (request != null) next.MobileReceipts.Add(new()
        {
            RequestId = MobileProtocol.Text(request, "requestId"),
            Fingerprint = MobileProtocol.Fingerprint(request)
        });
    }
    async Task<JsonObject> CancelMobileAsync(JsonObject request)
    {
        Task? task;
        lock (sync)
        {
            if (ReplayMobile(request)) return MobileSnapshot();
            CheckMobileVersion(request);
            if (clearing) throw new InvalidOperationException("正在整理会话，请稍候。");
            var next = CopyState(state);
            AddMobileReceipt(next, request);
            // Persist the cancellation intent before acknowledging it. A restart cannot replay it
            // against a newer desktop question, and an interrupted process already stops its child.
            store.SaveHistory(next); state = next;
            currentRequest?.Cancel(); task = activeTask;
        }
        if (task != null) await task.ConfigureAwait(false);
        Notify(); return MobileSnapshot();
    }
}
