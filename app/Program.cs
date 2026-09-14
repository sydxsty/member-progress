using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace StudentProgress;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        var updateTarget = Argument(args, "--update-target");
        if (updateTarget != null)
        {
            using var updateMutex = new Mutex(true, @"Local\StudentProgress-Update-" + DataIdentity(updateTarget), out var firstUpdater);
            if (!firstUpdater) { MessageBox.Show("更新窗口已经打开，请在该窗口查看进度。", "成员进度"); return; }
            try { Application.Run(new UpdateForm(AppContext.BaseDirectory, updateTarget)); }
            finally { updateMutex.ReleaseMutex(); }
            return;
        }
        var dataRoot = Path.GetFullPath(Argument(args, "--data-dir") ?? Path.Combine(AppContext.BaseDirectory, "Data"));
        if (args.Contains("--assistant-actions-test"))
        {
            Environment.Exit(Assistant.AssistantActionChecks.Run(dataRoot));return;
        }
        if (args.Contains("--assistant-reminders-test"))
        {
            Environment.Exit(AssistantReminderChecks.Run(dataRoot));return;
        }
        if (args.Contains("--assistant-reminder-live-test"))
        {
            Environment.Exit(AssistantReminderLiveCheck.Run(dataRoot));return;
        }
        if (args.Contains("--v4-test"))
        {
            Environment.Exit(V4Checks.Run(dataRoot));
            return;
        }
        if (args.Contains("--assistant-test"))
        {
            Environment.Exit(Assistant.AssistantChecks.Run(dataRoot));
            return;
        }
        if (args.Contains("--assistant-live-test"))
        {
            Environment.Exit(AssistantLiveCheck.Run(dataRoot));
            return;
        }
        if (args.Contains("--monthly-test"))
        {
            Environment.Exit(MonthlyChecks.Run(dataRoot));
            return;
        }
        if (args.Contains("--revision-test"))
        {
            Environment.Exit(RevisionChecks.Run(dataRoot));
            return;
        }
        if (args.Contains("--smoke-test"))
        {
            Environment.Exit(SmokeChecks.Run(dataRoot));
            return;
        }
        if (args.Contains("--notification-diagnose"))
        {
            Directory.CreateDirectory(dataRoot);
            using var notifications = new Notifications(dataRoot);
            File.WriteAllText(Path.Combine(dataRoot, "notification-status.txt"), notifications.Status);
            return;
        }
        if (args.Contains("--notification-test"))
        {
            Directory.CreateDirectory(dataRoot);
            using var notifications = new Notifications(dataRoot);
            var accepted = notifications.Show("成员进度 · 通知已就绪", "global", "", "native-check");
            File.WriteAllText(Path.Combine(dataRoot, "notification-check.json"), JsonSerializer.Serialize(new
            {
                acceptedByWindows = accepted,
                status = notifications.Status,
                actualBannerVisibility = "需人工查看通知中心；系统免打扰可能隐藏横幅",
                silent = true,
                checkedAt = DateTimeOffset.Now,
                notifications.AppId,
                notifications.ShortcutPath,
                notifications.ProtocolName
            }, new JsonSerializerOptions { WriteIndented = true }));
            Environment.Exit(accepted ? 0 : 1);
            return;
        }

        var identity = DataIdentity(dataRoot);
        using var mutex = new Mutex(true, @"Local\StudentProgress-" + identity, out var firstInstance);
        if (!firstInstance)
        {
            try
            {
                using var pipe = new NamedPipeClientStream(".", "StudentProgress-" + identity, PipeDirection.Out);
                pipe.Connect(3500);
                using var writer = new StreamWriter(pipe, new UTF8Encoding(false));
                writer.WriteLine(Argument(args, "--notification") ?? "");
            }
            catch { MessageBox.Show("成员进度已经在运行，请从任务栏或托盘打开。", "成员进度"); }
            return;
        }

        try
        {
            Directory.CreateDirectory(dataRoot);
            Application.ThreadException += (_, _) => MessageBox.Show("操作未完成，请重试。已保存的资料会保留。", "成员进度");
            using var form = new MainForm(dataRoot, Argument(args, "--notification"));
            _ = form.Handle;
            using var cancel = new CancellationTokenSource();
            _ = ListenForActivationAsync(identity, form, cancel.Token);
            Application.Run(form);
            cancel.Cancel();
        }
        catch
        {
            MessageBox.Show("无法启动成员进度。请确认程序所在位置和资料目录可写，然后重试。", "成员进度", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally { mutex.ReleaseMutex(); }
    }

    internal static string DataIdentity(string path) => Convert.ToHexString(SHA256.HashData(
        Encoding.UTF8.GetBytes(Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).ToUpperInvariant())))[..20].ToLowerInvariant();

    private static string? Argument(string[] args, string name)
    {
        var at = Array.IndexOf(args, name);
        return at >= 0 && at + 1 < args.Length ? args[at + 1] : null;
    }

    private static async Task ListenForActivationAsync(string identity, MainForm form, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                using var pipe = new NamedPipeServerStream("StudentProgress-" + identity, PipeDirection.In, 1,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await pipe.WaitForConnectionAsync(cancellationToken);
                using var reader = new StreamReader(pipe, Encoding.UTF8);
                var value = await reader.ReadLineAsync(cancellationToken);
                if (value?.Length > 8192) continue;
                if (!form.IsDisposed && form.IsHandleCreated)
                    form.BeginInvoke(new Action(() => form.ActivateFromNotification(value)));
            }
            catch (OperationCanceledException) { break; }
            catch { if (cancellationToken.IsCancellationRequested) break; }
        }
    }
}
