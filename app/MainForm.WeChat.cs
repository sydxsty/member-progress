using System.Text.Json.Nodes;
using StudentProgress.Assistant;
using StudentProgress.WeChat;

namespace StudentProgress;

public sealed partial class MainForm
{
    WeChatService? _wechat;
    string? _wechatError;

    void InitializeWeChat()
    {
        _service.RegisterBackupFiles(new[] { "wechat/config.json", "wechat/suggestions.json" });
        _service.RegisterBackupValidator("wechat/config.json", WeChatService.ValidateSettingsBackup);
        _service.RegisterBackupValidator("wechat/suggestions.json", WeChatService.ValidateSuggestionsBackup);
        try
        {
            _wechat = new WeChatService(_dataRoot, AppContext.BaseDirectory, _service.Snapshot, _service.ImportWeChatArchive);
            _wechat.ConfigureAgent(() => _service.BuildAssistantContext(new JsonObject { ["includeReminders"] = true, ["includeRecent"] = false }),
                _service.GetAssistantConnection, _service.CreateAssistantReminders, () => _assistant?.Snapshot()["busy"]?.GetValue<bool>() == true);
            _wechat.StateChanged += OnWeChatStateChanged;
            _wechat.ProgressChanged += OnWeChatProgressChanged;
            _wechat.AnalysisProgressChanged += OnWeChatAnalysisProgressChanged;
        }
        catch
        {
            _wechat?.Dispose(); _wechat = null;
            _wechatError = "微信模块暂时不可用，已有微信资料和成员数据均已保留。";
        }
    }

    void OnWeChatProgressChanged()
    {
        if(IsDisposed || !IsHandleCreated)return;
        try { BeginInvoke(new Action(() => Post(new JsonObject { ["type"]="wechat.progress",["progress"]=_wechat?.ProgressSnapshot() }))); }
        catch(InvalidOperationException) { }
    }

    void OnWeChatAnalysisProgressChanged()
    {
        if (IsDisposed || !IsHandleCreated) return;
        try { BeginInvoke(new Action(() => Post(new JsonObject { ["type"] = "wechat.analysis.progress", ["progress"] = _wechat?.AnalysisProgressSnapshot() }))); }
        catch (InvalidOperationException) { }
    }

    void OnWeChatStateChanged()
    {
        if (IsDisposed || !IsHandleCreated) return;
        try { BeginInvoke(new Action(PushWeChatState)); } catch (InvalidOperationException) { }
    }
    void PushWeChatState() => Post(new JsonObject { ["type"] = "wechat", ["state"] = _wechat?.Snapshot() ?? new JsonObject
    { ["available"] = false, ["status"] = new JsonObject { ["error"] = _wechatError ?? "微信模块尚未初始化。" }, ["conversations"] = new JsonArray(), ["suggestions"] = new JsonArray() } });

    async Task<JsonObject> HandleWeChatActionAsync(string action, JsonObject payload)
    {
        if (_wechat == null) throw new InvalidOperationException(_wechatError ?? "微信模块暂时不可用。");
        if (action == "wechat.export")
        {
            using var dialog = new SaveFileDialog { Title = "导出微信资料", Filter = "微信资料 (*.zip)|*.zip", FileName = "微信资料-" + DateTime.Now.ToString("yyyyMMdd-HHmm") + ".zip", DefaultExt = "zip", AddExtension = true };
            if (dialog.ShowDialog(this) != DialogResult.OK) return new JsonObject { ["cancelled"] = true };
            return await _wechat.HandleAsync(action, new JsonObject { ["path"] = dialog.FileName });
        }
        if (action == "wechat.import")
        {
            using var dialog = new OpenFileDialog { Title = "恢复微信资料", Filter = "微信资料 (*.zip)|*.zip", CheckFileExists = true };
            if (dialog.ShowDialog(this) != DialogResult.OK) return new JsonObject { ["cancelled"] = true };
            if (MessageBox.Show(this, "将恢复微信聊天和文件，现有微信资料会先保留恢复副本。继续恢复？", "恢复微信资料", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK)
                return new JsonObject { ["cancelled"] = true };
            await _wechat.CancelSuggestionsAsync();
            var result = await _wechat.HandleAsync(action, new JsonObject { ["path"] = dialog.FileName });
            PushWeChatState(); return result;
        }
        return await _wechat.HandleAsync(action, payload);
    }

    JsonObject BuildAssistantContextWithWeChat(JsonObject selection)
    {
        var ownSelection = selection.DeepClone().AsObject();
        ownSelection["references"] = new JsonArray((selection["references"] as JsonArray ?? new()).OfType<JsonObject>()
            .Where(r => AssistantJson.Text(r["scopeType"]) != "wechat").Select(r => r.DeepClone()).ToArray());
        var context = _service.BuildAssistantContext(ownSelection);
        foreach (var reference in (selection["references"] as JsonArray ?? new()).OfType<JsonObject>().Where(r => AssistantJson.Text(r["scopeType"]) == "wechat"))
        {
            if (_wechat == null) throw new InvalidOperationException("引用的微信资料暂时不可用。");
            context["referencedArchives"]!.AsArray().Add(new JsonObject { ["scopeType"] = "wechat", ["scopeId"] = reference["scopeId"]?.DeepClone(),
                ["title"] = reference["title"]?.DeepClone(), ["messages"] = new JsonArray() });
        }
        return context;
    }

    async Task<JsonObject> PrepareAssistantMaterialsAsync(JsonObject selection, string workspace, CancellationToken cancellation)
    {
        var context = BuildAssistantContextWithWeChat(selection);
        var references = await _service.WriteBusinessReferenceMaterialsAsync(selection, workspace, cancellation).ConfigureAwait(false);
        var wx = new JsonArray((selection["references"] as JsonArray ?? new()).OfType<JsonObject>().Where(r => AssistantJson.Text(r["scopeType"]) == "wechat").Select(r => r.DeepClone()).ToArray());
        JsonObject? wechatMaterial = null;
        if (wx.Count > 0)
        {
            if (_wechat == null) throw new InvalidOperationException("微信模块暂时无法读取引用资料。");
            wechatMaterial = await _wechat.WriteReferenceWorkspaceAsync(wx, workspace, cancellation).ConfigureAwait(false);
        }
        // The complete data is accessible as files, including material too large for the chat prompt.
        AssistantJson.WriteAtomic(Path.Combine(workspace, "application-context.json"), context);
        var manifest = new JsonObject { ["references"] = references, ["wechat"] = wechatMaterial,
            ["applicationContextFile"] = "application-context.json", ["conversationFile"] = "chat-history.json" };
        AssistantJson.WriteAtomic(Path.Combine(workspace, "materials.json"), manifest);
        return manifest;
    }
}
