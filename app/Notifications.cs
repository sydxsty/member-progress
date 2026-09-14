using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;
using Microsoft.Win32;
using Windows.Data.Xml.Dom;
using Windows.UI.Notifications;

namespace StudentProgress;

/// <summary>Unpackaged desktop toasts with silent audio and reliable protocol activation.</summary>
internal sealed class Notifications : IDisposable
{
    private ToastNotifier? _notifier;
    private readonly string _dataRoot;
    private readonly HashSet<string> _diagnosedErrors = new();
    public string AppId { get; }
    public string ProtocolName { get; }
    public string ShortcutPath { get; }
    public string Status { get; private set; } = "正在检查 Windows 通知";
    public event Action? StatusChanged;

    public Notifications(string dataRoot)
    {
        _dataRoot = Path.GetFullPath(dataRoot);
        var identity = Program.DataIdentity(_dataRoot);
        AppId = "StudentProgress." + identity;
        ProtocolName = "studentprogress-" + identity;
        ShortcutPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "成员进度", "成员进度-" + identity + ".lnk");
        try
        {
            RegisterIdentity();
            _notifier = ToastNotificationManager.CreateToastNotifier(AppId);
            CanSend();
        }
        catch (Exception ex) { Diagnose("identity", ex); SetStatus("Windows 通知尚不可用；请确认系统通知设置和程序所在位置可访问"); }
    }

    public bool CanSend()
    {
        if (_notifier is null) return false;
        try
        {
            var setting = _notifier.Setting;
            SetStatus(setting switch
            {
                NotificationSetting.Enabled => "Windows 静音通知已启用；横幅显示受系统免打扰设置影响",
                NotificationSetting.DisabledForApplication => "此应用的 Windows 通知已关闭；请在系统设置中启用，待提醒事项会保留",
                NotificationSetting.DisabledForUser => "Windows 通知已关闭；启用后补发到期提醒",
                NotificationSetting.DisabledByGroupPolicy => "Windows 通知被系统策略关闭；待提醒事项会保留",
                _ => "Windows 暂不允许显示通知；待提醒事项会保留"
            });
            return setting == NotificationSetting.Enabled;
        }
        catch (COMException ex) when (ex.HResult == unchecked((int)0x80070490))
        {
            // Windows does not expose Setting for an unpackaged app until its first notification.
            // ToastNotifierCompat in Microsoft's WindowsCommunityToolkit handles this by pre-registering a toast.
            // Defer that registration to the first actual reminder so app startup never sends a test notification.
            SetStatus("Windows 静音通知等待首次提醒初始化");
            return true;
        }
        catch (Exception ex) { Diagnose("setting", ex); SetStatus("暂时无法读取 Windows 通知状态"); return false; }
    }

    public bool Show(string title, string scopeType, string scopeId, string reminderId)
    {
        if (!CanSend()) return false;
        try
        {
            var launch = ProtocolName + "://open?scopeType=" + Uri.EscapeDataString(scopeType) + "&scopeId=" + Uri.EscapeDataString(scopeId);
            var toastElement = new XElement("toast", new XAttribute("activationType", "protocol"), new XAttribute("launch", launch),
                new XElement("visual", new XElement("binding", new XAttribute("template", "ToastGeneric"),
                    new XElement("text", "成员进度"),
                    new XElement("text", title.Length > 500 ? title[..500] : title),
                    new XElement("text", scopeType switch { "student" => "点击打开该成员的归档", "project" => "点击打开该项目的归档", _ => "点击打开成员进度" }))),
                new XElement("audio", new XAttribute("silent", "true")));
            if (!string.IsNullOrEmpty(reminderId) && reminderId != "native-check")
            {
                var snooze = ProtocolName + "://open?action=snooze&reminderId=" + Uri.EscapeDataString(reminderId);
                toastElement.Add(new XElement("actions", new XElement("action",
                    new XAttribute("content", "延后 10 分钟"), new XAttribute("activationType", "protocol"), new XAttribute("arguments", snooze))));
            }
            var document = new XmlDocument();
            document.LoadXml(toastElement.ToString(SaveOptions.DisableFormatting));
            var toast = new ToastNotification(document)
            {
                Tag = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(reminderId)))[..16],
                Group = "reminders",
                ExpirationTime = DateTimeOffset.Now.AddDays(7)
            };
            toast.Failed += (_, _) => SetStatus("Windows 未能显示一条到期提醒，请检查通知设置并查看闹钟列表");
            _notifier!.Show(toast);
            CanSend();
            return true;
        }
        catch (Exception ex) { Diagnose("show", ex); SetStatus("Windows 未接受提醒，请检查通知设置并查看闹钟列表"); return false; }
    }

    private void Diagnose(string stage, Exception exception)
    {
        try
        {
            lock (_diagnosedErrors)
                if (!_diagnosedErrors.Add(stage + ":" + exception.HResult)) return;
            // OS notification diagnostics only; no backend objects, requests, credentials or stack traces.
            var message = exception.Message;
            if (message.Length > 500) message = message[..500];
            File.AppendAllText(Path.Combine(_dataRoot, "notification-diagnostic.jsonl"), System.Text.Json.JsonSerializer.Serialize(new
            {
                stage, type = exception.GetType().FullName, hresult = "0x" + exception.HResult.ToString("X8"), message
            }) + Environment.NewLine);
        }
        catch { }
    }

    private void SetStatus(string text)
    {
        if (Status == text) return;
        Status = text;
        StatusChanged?.Invoke();
    }

    private void RegisterIdentity()
    {
        var executable = Environment.ProcessPath ?? throw new InvalidOperationException();
        // The distributed Windows apphost is required so protocol activation can launch without dotnet arguments.
        if (!Path.GetFileNameWithoutExtension(executable).Equals("StudentProgress", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException();
        Marshal.ThrowExceptionForHR(SetCurrentProcessExplicitAppUserModelID(AppId));
        using (var protocol = Registry.CurrentUser.CreateSubKey(@"Software\Classes\" + ProtocolName))
        {
            protocol.SetValue("", "URL:成员进度提醒");
            protocol.SetValue("URL Protocol", "");
            using var command = protocol.CreateSubKey(@"shell\open\command");
            command.SetValue("", Quote(executable) + " --data-dir " + Quote(_dataRoot) + " --notification \"%1\"");
        }
        using (var app = Registry.CurrentUser.CreateSubKey(@"Software\Classes\AppUserModelId\" + AppId))
        {
            app.SetValue("DisplayName", "成员进度");
            app.SetValue("IconUri", executable);
            app.SetValue("ShowInSettings", 1, RegistryValueKind.DWord);
        }
        Directory.CreateDirectory(Path.GetDirectoryName(ShortcutPath)!);
        var shortcut = (IShellLinkW)new ShellLink();
        try
        {
            shortcut.SetPath(executable);
            shortcut.SetArguments("--data-dir " + Quote(_dataRoot));
            shortcut.SetWorkingDirectory(AppContext.BaseDirectory);
            shortcut.SetDescription("成员任务、归档与静音提醒");
            shortcut.SetIconLocation(executable, 0);
            var store = (IPropertyStore)shortcut;
            var propertyKey = new PropertyKey { FormatId = new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"), PropertyId = 5 };
            using var appIdValue = new PropVariant(AppId);
            var value = appIdValue;
            Marshal.ThrowExceptionForHR(store.SetValue(ref propertyKey, ref value));
            Marshal.ThrowExceptionForHR(store.Commit());
            ((IPersistFile)shortcut).Save(ShortcutPath, true);
            // Replace only this application's generated legacy shortcut for the same Data directory.
            var legacy = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "学生进度", "学生进度-" + Program.DataIdentity(_dataRoot) + ".lnk");
            try { if (File.Exists(legacy)) File.Delete(legacy); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
        finally { Marshal.FinalReleaseComObject(shortcut); }
    }

    // data roots are local paths, so a trailing directory separator is removed before quoting.
    private static string Quote(string value) => "\"" + value.TrimEnd('\\', '/').Replace("\"", "") + "\"";
    public void Dispose() { _notifier = null; }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SetCurrentProcessExplicitAppUserModelID(string appID);

    [ComImport, Guid("00021401-0000-0000-C000-000000000046")]
    private class ShellLink { }

    [ComImport, Guid("000214F9-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder file, int maxPath, IntPtr data, uint flags);
        void GetIDList(out IntPtr idList);
        void SetIDList(IntPtr idList);
        void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder name, int maxName);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string name);
        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder directory, int maxPath);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string directory);
        void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder arguments, int maxPath);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string arguments);
        void GetHotkey(out short hotkey);
        void SetHotkey(short hotkey);
        void GetShowCmd(out int showCommand);
        void SetShowCmd(int showCommand);
        void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder iconPath, int maxPath, out int iconIndex);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string iconPath, int iconIndex);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string path, uint reserved);
        void Resolve(IntPtr window, uint flags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string path);
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct PropertyKey { public Guid FormatId; public uint PropertyId; }

    [StructLayout(LayoutKind.Explicit, Size = 24)]
    private struct PropVariant : IDisposable
    {
        [FieldOffset(0)] private ushort _type;
        [FieldOffset(8)] private IntPtr _pointer;
        public PropVariant(string value) { _type = 31; _pointer = Marshal.StringToCoTaskMemUni(value); }
        public void Dispose() { if (_pointer != IntPtr.Zero) { Marshal.FreeCoTaskMem(_pointer); _pointer = IntPtr.Zero; } }
    }

    [ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyStore
    {
        [PreserveSig] int GetCount(out uint propertyCount);
        [PreserveSig] int GetAt(uint propertyIndex, out PropertyKey key);
        [PreserveSig] int GetValue(ref PropertyKey key, out PropVariant value);
        [PreserveSig] int SetValue(ref PropertyKey key, ref PropVariant value);
        [PreserveSig] int Commit();
    }
}
