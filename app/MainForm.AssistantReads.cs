using System.Text.Json.Nodes;
using StudentProgress.Assistant;
using StudentProgress.WeChat;

namespace StudentProgress;

public sealed partial class MainForm
{
    void InitializeAssistantReads()
    {
        var descriptions = AppService.DescribeAssistantReads();
        foreach (var item in WeChatService.DescribeAssistantReads()) descriptions.Add(item!.DeepClone());
        descriptions.Add(new JsonObject { ["name"] = "assistant.history", ["description"] = "查询当前未清空的 GPT 会话完整历史，按时间从旧到新分页。",
            ["parameters"] = new JsonObject { ["query"] = "可选，文字包含匹配", ["offset"] = "可选，从0开始", ["limit"] = "可选，默认30，最多100" } });
        if (_assistant != null)
        {
            _assistant.ReadApiDescription = descriptions;
            _assistant.ReadDataAsync = ReadAssistantDataAsync;
        }
        AssistantReadSession Open(string workspace, CancellationToken cancellation) => new(workspace, descriptions, ReadAssistantDataAsync, cancellation);
        _service.OpenReadSession = Open;
        if (_wechat != null) _wechat.OpenReadSession = Open;
    }

    Task<JsonObject> ReadAssistantDataAsync(string operation, JsonObject args, string workspace, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        if (operation == "assistant.history")
            return Task.FromResult(_assistant?.ReadConversation(args) ?? new JsonObject { ["available"] = false });
        if (operation.StartsWith("app.", StringComparison.Ordinal))
            return _service.ReadAssistantDataAsync(operation, args, workspace, cancellation);
        if (operation.StartsWith("wechat.", StringComparison.Ordinal))
            return _wechat != null ? _wechat.ReadAssistantDataAsync(operation, args, workspace, cancellation)
                : Task.FromResult(new JsonObject { ["available"] = false, ["reason"] = "微信模块暂时不可用，尚不能查询微信资料。" });
        throw new InvalidOperationException("只支持接口列表中的只读查询。");
    }
}
