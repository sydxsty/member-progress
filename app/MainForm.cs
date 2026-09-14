using System.Diagnostics;
using System.Text.Json.Nodes;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using StudentProgress.Assistant;

namespace StudentProgress;

public sealed class MainForm : Form
{
    private const string AppHost = "student-progress.local";
    private readonly string _dataRoot;
    private readonly AppService _service;
    private readonly AssistantService? _assistant;
    private string? _assistantError;
    private readonly Notifications _notifications;
    private readonly WebView2 _web = new() { Dock = DockStyle.Fill };
    private readonly NotifyIcon _tray;
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 15000 };
    private string? _pendingActivation;
    private bool _exiting, _ready, _polling;

    public MainForm(string dataRoot, string? activation)
    {
        _dataRoot = dataRoot;
        _pendingActivation = activation;
        Text = "成员进度";
        Width = 1440; Height = 920; MinimumSize = new Size(960, 660);
        StartPosition = FormStartPosition.CenterScreen;
        var iconPath = Path.Combine(AppContext.BaseDirectory, "app.ico");
        Icon = File.Exists(iconPath) ? new System.Drawing.Icon(iconPath) : SystemIcons.Application;
        _service = new AppService(dataRoot);
        _service.RegisterBackupFiles(new[] { "assistant/history.json", "assistant/settings.json" });
        try
        {
            _assistant = new AssistantService(dataRoot, AppContext.BaseDirectory, _service.BuildAssistantContext, _service.GetAssistantConnection, _service.CreateAssistantReminders);
            _assistant.StateChanged += OnAssistantStateChanged;
        }
        catch
        {
            _assistantError = "GPT 聊天模块暂时无法读取本机记录，原文件已保留；成员和任务功能可继续使用。";
        }
        _notifications = new Notifications(dataRoot);
        Controls.Add(_web);

        var menu = new ContextMenuStrip();
        menu.Items.Add("打开成员进度", null, (_, _) => ShowApp());
        menu.Items.Add("退出", null, (_, _) => { _exiting = true; Close(); });
        _tray = new NotifyIcon { Icon = Icon, Text = "成员进度 · 关闭窗口后继续提醒", ContextMenuStrip = menu, Visible = true };
        _tray.DoubleClick += (_, _) => ShowApp();
        _service.StateChanged += OnStateChanged;
        _notifications.StatusChanged += OnStateChanged;
        _timer.Tick += async (_, _) =>
        {
            await PollRemindersAsync();
            if (_assistant != null)
                try { await _assistant.InitializeAsync(); } catch { /* Optional runtime updates must not interrupt reminders. */ }
        };
        Shown += async (_, _) => await InitializeAsync();
        FormClosing += (_, e) =>
        {
            if (!_exiting && e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; Hide(); }
        };
        FormClosed += (_, _) =>
        {
            _timer.Stop(); _timer.Dispose(); _tray.Visible = false; _tray.Dispose();
            _service.StateChanged -= OnStateChanged;
            _notifications.StatusChanged -= OnStateChanged;
            if (_assistant != null) { _assistant.StateChanged -= OnAssistantStateChanged; _assistant.Dispose(); }
            _notifications.Dispose(); _service.Dispose();
        };
    }

    private async Task InitializeAsync()
    {
        try
        {
            var environment = await CoreWebView2Environment.CreateAsync(null, Path.Combine(_dataRoot, "WebViewCache"));
            await _web.EnsureCoreWebView2Async(environment);
            var core = _web.CoreWebView2;
            core.SetVirtualHostNameToFolderMapping(AppHost, Path.Combine(AppContext.BaseDirectory, "web"), CoreWebView2HostResourceAccessKind.DenyCors);
            core.Settings.IsStatusBarEnabled = false;
            core.Settings.AreDevToolsEnabled = false;
            core.Settings.AreBrowserAcceleratorKeysEnabled = false;
            core.Settings.IsPasswordAutosaveEnabled = false;
            core.Settings.IsGeneralAutofillEnabled = false;
            core.NavigationStarting += (_, e) => { if (!IsTrusted(e.Uri)) e.Cancel = true; };
            core.NewWindowRequested += (_, e) => e.Handled = true;
            core.PermissionRequested += (_, e) => e.State = CoreWebView2PermissionState.Deny;
            core.DownloadStarting += (_, e) => e.Cancel = true;
            core.WebMessageReceived += OnWebMessage;
            core.NavigationCompleted += (_, e) =>
            {
                _ready = e.IsSuccess;
                if (_ready) { PushState(); PushAssistantState(); ActivateFromNotification(_pendingActivation, false); _pendingActivation = null; }
            };
            core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All);
            core.WebResourceRequested += (_, e) =>
            {
                if (Uri.TryCreate(e.Request.Uri, UriKind.Absolute, out var uri) &&
                    (uri.Scheme == "http" || uri.Scheme == "https") && !IsTrusted(uri.AbsoluteUri))
                    e.Response = environment.CreateWebResourceResponse(Stream.Null, 403, "Forbidden", "Content-Type: text/plain");
            };
            core.Navigate("https://" + AppHost + "/index.html?build=" + typeof(MainForm).Module.ModuleVersionId.ToString("N"));
            _timer.Start();
            if (_assistant != null)
            {
                try { await _assistant.InitializeAsync(); }
                catch { _assistantError = "GPT 内核更新检查暂未完成，可在设置中重试。"; PushAssistantState(); }
            }
            await PollRemindersAsync();
        }
        catch (WebView2RuntimeNotFoundException)
        {
            ShowStartupFailure("此电脑缺少 Microsoft Edge WebView2 运行时。请安装微软 WebView2 Evergreen Runtime 后重新打开；已有资料会保留。");
        }
        catch
        {
            ShowStartupFailure("界面未能启动。请确认程序文件完整、资料目录可写，且 Microsoft Edge WebView2 运行时可用。已有资料会保留。");
        }
    }

    private void ShowStartupFailure(string text)
    {
        MessageBox.Show(this, text, "成员进度", MessageBoxButtons.OK, MessageBoxIcon.Error);
        _exiting = true; Close();
    }

    private static bool IsTrusted(string source) => Uri.TryCreate(source, UriKind.Absolute, out var uri) &&
        uri.Scheme == "https" && uri.Host.Equals(AppHost, StringComparison.OrdinalIgnoreCase) && uri.IsDefaultPort;

    private async void OnWebMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        if (!IsTrusted(e.Source)) return;
        JsonNode? id = null;
        try
        {
            var request = JsonNode.Parse(e.WebMessageAsJson)?.AsObject() ?? throw new InvalidOperationException();
            id = request["id"]?.DeepClone();
            var action = request["action"]?.GetValue<string>() ?? "";
            var payload = request["payload"] as JsonObject ?? new JsonObject();
            JsonNode? result;
            if (action.StartsWith("assistant.", StringComparison.Ordinal))
            {
                if (_assistant == null) throw new InvalidOperationException(_assistantError ?? "GPT 聊天模块暂时不可用。");
                result = await _assistant.HandleAsync(action, payload);
            }
            else if (action == "app.get") result = Snapshot();
            else if (action == "file.open")
            {
                var path = _service.ResolveAttachment(payload["id"]?.GetValue<string>() ?? "");
                if (path is null || !File.Exists(path)) throw new InvalidOperationException("附件不存在或已移动。");
                var blocked = new[] { ".exe", ".com", ".bat", ".cmd", ".ps1", ".vbs", ".js", ".jse", ".wsf", ".wsh", ".hta", ".lnk", ".url", ".scr", ".msi", ".msp", ".reg", ".dll", ".sys", ".cpl", ".msc" };
                if (blocked.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase))
                    throw new InvalidOperationException("该附件属于程序或脚本，不能从档案中直接运行。");
                Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
                result = new JsonObject { ["opened"] = true };
            }
            else if (action == "backup.export")
            {
                using var dialog = new SaveFileDialog { Title = "导出成员进度备份", Filter = "成员进度备份 (*.zip)|*.zip", FileName = "成员进度备份-" + DateTime.Now.ToString("yyyyMMdd-HHmm") + ".zip", DefaultExt = "zip", AddExtension = true };
                result = dialog.ShowDialog(this) == DialogResult.OK
                    ? await _service.HandleAsync(action, new JsonObject { ["path"] = dialog.FileName })
                    : new JsonObject { ["cancelled"] = true };
            }
            else if (action == "backup.import")
            {
                using var dialog = new OpenFileDialog { Title = "恢复成员进度备份", Filter = "成员进度备份 (*.zip)|*.zip", CheckFileExists = true };
                var confirmed = dialog.ShowDialog(this) == DialogResult.OK && MessageBox.Show(this,
                    "将从备份恢复成员、任务和归档。当前资料会先保存为恢复副本。继续恢复？", "恢复备份", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) == DialogResult.OK
                    ;
                if (confirmed)
                {
                    if (_assistant != null) await _assistant.HandleAsync("assistant.cancel", new JsonObject());
                    result = await _service.HandleAsync(action, new JsonObject { ["path"] = dialog.FileName });
                    try { _assistant?.Reload(); _assistantError = null; }
                    catch { _assistantError = "成员资料已恢复，但 GPT 聊天记录暂时无法读取；原文件已保留。"; }
                    PushAssistantState();
                }
                else result = new JsonObject { ["cancelled"] = true };
            }
            else result = await _service.HandleAsync(action, payload);
            if (result is JsonObject state && state["students"] is JsonArray)
                DecorateStatus(state);
            Post(new JsonObject { ["id"] = id, ["result"] = result, ["error"] = null });
        }
        catch (Exception ex)
        {
            // Business validation uses short Chinese messages. Unknown runtime errors must not leak paths, credentials or remote responses.
            var text = ex is InvalidOperationException && ex.Message.Length <= 120 && ex.Message.Any(c => c >= '\u4e00' && c <= '\u9fff')
                ? ex.Message : "操作未完成，请检查输入后重试。已保存的内容会保留。";
            Post(new JsonObject { ["id"] = id?.DeepClone(), ["result"] = null, ["error"] = text });
        }
    }

    private JsonObject Snapshot()
    {
        var state = _service.Snapshot();
        DecorateStatus(state);
        return state;
    }

    private void DecorateStatus(JsonObject state)
    {
        if (state["status"] is not JsonObject status) state["status"] = status = new JsonObject();
        status["notificationStatus"] = _notifications.Status;
    }

    private void OnStateChanged()
    {
        if (IsDisposed || !IsHandleCreated) return;
        // Queue even on the UI thread: backend events may be raised before its mutation gate is released.
        try { BeginInvoke(new Action(PushState)); } catch (InvalidOperationException) { }
    }

    private void PushState() => Post(new JsonObject { ["type"] = "state", ["state"] = Snapshot() });
    private void OnAssistantStateChanged()
    {
        if (IsDisposed || !IsHandleCreated) return;
        try { BeginInvoke(new Action(PushAssistantState)); } catch (InvalidOperationException) { }
    }
    private void PushAssistantState()
    {
        var snapshot = _assistant?.Snapshot() ?? new JsonObject
        {
            ["messages"] = new JsonArray(), ["busy"] = false,
            ["runtime"] = new JsonObject { ["available"] = false, ["version"] = "", ["autoUpdate"] = false }
        };
        if (_assistantError != null) snapshot["error"] = _assistantError;
        Post(new JsonObject { ["type"] = "assistant", ["state"] = snapshot });
    }
    private void Post(JsonObject message)
    {
        if (!_ready || IsDisposed) return;
        try { _web.CoreWebView2.PostWebMessageAsJson(message.ToJsonString()); } catch (InvalidOperationException) { }
    }

    private async Task PollRemindersAsync()
    {
        if (_polling || _exiting) return;
        _polling = true;
        try
        {
            _service.RefreshStudentGrades();
            if (!_notifications.CanSend()) return;
            var reminders = await _service.HandleAsync("reminder.poll", new JsonObject { ["now"] = DateTimeOffset.Now.ToString("O") });
            if (reminders is JsonArray list)
                foreach (var item in list.OfType<JsonObject>())
                    _notifications.Show(item["title"]?.GetValue<string>() ?? "成员进度提醒",
                        item["scopeType"]?.GetValue<string>() ?? "global", item["scopeId"]?.GetValue<string>() ?? "", item["id"]?.GetValue<string>() ?? "");
        }
        catch { /* Retry next timer tick; service persistence errors must not terminate the tray. */ }
        finally { _polling = false; }
    }

    internal async void ActivateFromNotification(string? activation, bool restoreWindow = true)
    {
        if (string.IsNullOrWhiteSpace(activation)) { if (restoreWindow) ShowApp(); return; }
        if (!_ready) { _pendingActivation = activation; return; }
        if (!Uri.TryCreate(activation, UriKind.Absolute, out var uri) || uri.Scheme != _notifications.ProtocolName) return;
        var query = new Dictionary<string, string>();
        foreach (var part in uri.Query.TrimStart('?').Split('&'))
        {
            var pair = part.Split('=', 2);
            if (pair.Length == 2) query[pair[0]] = Uri.UnescapeDataString(pair[1]);
        }
        if (query.GetValueOrDefault("action") == "snooze" && query.TryGetValue("reminderId", out var reminderId))
        {
            try
            {
                await _service.HandleAsync("reminder.snooze", new JsonObject { ["id"] = reminderId, ["minutes"] = 10 });
                if (!restoreWindow) Hide();
            }
            catch { MessageBox.Show(this, "未能延后提醒，请在闹钟列表中重新安排。", "成员进度"); }
            return;
        }
        if (restoreWindow) ShowApp();
        var type = query.GetValueOrDefault("scopeType", "global");
        if (type is not ("student" or "project" or "global")) return;
        Post(new JsonObject { ["type"] = "navigate", ["scopeType"] = type, ["scopeId"] = query.GetValueOrDefault("scopeId", "") });
    }

    private void ShowApp()
    {
        Show(); if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
        Activate(); BringToFront();
    }
}
