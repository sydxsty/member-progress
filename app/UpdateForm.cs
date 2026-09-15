using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json.Nodes;

namespace StudentProgress;

// Runs from the staging folder, never overwrites a live program or the user's Data.
internal sealed class UpdateForm : Form
{
    readonly string source, target;
    readonly string? currentMember;
    readonly Label heading = new() { Text = "新版已准备好", AutoSize = true, Font = new Font("Microsoft YaHei UI", 16, FontStyle.Bold), Location = new Point(28, 25) };
    readonly Label detail = new() { AutoSize = false, Location = new Point(28, 78), Size = new Size(480, 118), Text = "请先保存旧版中未发送的内容，再从托盘菜单选择“退出”。\n\n退出后会自动更新并重新打开，已有资料会保留。" };
    readonly Button cancel = new() { Text = "暂不更新", Location = new Point(386, 211), Size = new Size(115, 36) };
    readonly System.Windows.Forms.Timer timer = new() { Interval = 800 };
    bool updating, finished;

    public UpdateForm(string sourceDirectory, string targetDirectory, string? currentMemberId = null)
    {
        currentMember = currentMemberId;
        source = Path.GetFullPath(sourceDirectory).TrimEnd(Path.DirectorySeparatorChar);
        target = Path.GetFullPath(targetDirectory).TrimEnd(Path.DirectorySeparatorChar);
        Text = "成员进度 · 更新";
        Font = new Font("Microsoft YaHei UI", 10);
        ClientSize = new Size(535, 270);
        BackColor = Color.White;
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        Controls.AddRange(new Control[] { heading, detail, cancel });
        cancel.Click += (_, _) => Close();
        FormClosing += (_, e) => { if (updating) e.Cancel = true; };
        FormClosed += (_, _) => timer.Dispose();
        timer.Tick += async (_, _) => await TryUpdateAsync();
        Shown += async (_, _) =>
        {
            try
            {
                ValidateInputs(source, target);
                timer.Start();
                await TryUpdateAsync();
            }
            catch
            {
                heading.Text = "更新文件未准备完整";
                detail.Text = "现有程序和资料没有改变。请保留更新准备文件夹，重新运行更新入口。";
                cancel.Text = "关闭";
            }
        };
    }

    async Task TryUpdateAsync()
    {
        if (updating || finished) return;
        var data = Path.Combine(target, "Data");
        using var appMutex = new Mutex(false, @"Local\StudentProgress-" + Program.DataIdentity(data));
        var acquired = false;
        try
        {
            try { acquired = appMutex.WaitOne(0); }
            catch (AbandonedMutexException) { acquired = true; }
            if (!acquired) return;
            updating = true;
            timer.Stop();
            cancel.Enabled = false;
            heading.Text = "正在更新";
            detail.Text = "正在保存旧版程序并替换应用文件，资料目录保持不变。";
            var backup = await Task.Run(() => ApplyFiles(source, target));
            // Named mutex ownership returns to this UI thread after the await.
            appMutex.ReleaseMutex(); acquired = false;
            var launch = new ProcessStartInfo(Path.Combine(target, "StudentProgress.exe")) { UseShellExecute = true, WorkingDirectory = target };
            if(!string.IsNullOrEmpty(currentMember)) { launch.ArgumentList.Add("--current-member");launch.ArgumentList.Add(currentMember); }
            var launched = Process.Start(launch);
            var receipt = new JsonObject { ["ok"] = true, ["completedAt"] = DateTimeOffset.Now.ToString("O"), ["target"] = target, ["programBackup"] = backup, ["pid"] = launched?.Id, ["dataUntouched"] = true };
            File.WriteAllText(Path.Combine(source, "update-result.json"), receipt.ToJsonString());
            finished = true; updating = false;
            Close();
        }
        catch (Exception ex)
        {
            updating = false; finished = true;
            heading.Text = "更新尚未完成";
            detail.Text = "原有资料已保留。请确认旧版已经从托盘退出，再重新运行更新入口。\n\n错误：" + (ex is IOException ? "应用文件仍被占用或磁盘不可写。" : "请保留更新文件，检查程序是否完整。");
            cancel.Text = "关闭"; cancel.Enabled = true;
            try { File.WriteAllText(Path.Combine(source, "update-result.json"), new JsonObject { ["ok"] = false, ["errorType"] = ex.GetType().Name, ["dataUntouched"] = true }.ToJsonString()); } catch { }
        }
        finally { if (acquired) appMutex.ReleaseMutex(); }
    }

    static List<(string Relative, string Source)> ValidateInputs(string source, string target)
    {
        if (source.Equals(target, StringComparison.OrdinalIgnoreCase) || !File.Exists(Path.Combine(target, "StudentProgress.exe")))
            throw new IOException("Invalid update destination.");
        var manifest = JsonNode.Parse(File.ReadAllText(Path.Combine(source, "update-manifest.json")))!.AsObject();
        var result = new List<(string, string)>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in manifest["files"]!.AsArray())
        {
            var relative = item!["path"]!.GetValue<string>();
            var normalized = relative.Replace('\\', '/');
            if (Path.IsPathRooted(relative) || normalized.Split('/').Any(x => x.Length == 0 || x is "." or "..") || normalized.Split('/')[0].Equals("Data", StringComparison.OrdinalIgnoreCase) || !seen.Add(normalized))
                throw new IOException("Invalid update entry.");
            var file = Path.GetFullPath(Path.Combine(source, relative));
            if (!file.StartsWith(source + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new IOException("Invalid source path.");
            using var sourceStream = File.OpenRead(file);
            var digest = Convert.ToHexString(SHA256.HashData(sourceStream));
            if (!digest.Equals(item["sha256"]!.GetValue<string>(), StringComparison.OrdinalIgnoreCase)) throw new IOException("Update file checksum mismatch.");
            result.Add((relative, file));
        }
        if (!seen.Contains("StudentProgress.exe")) throw new IOException("Missing executable.");
        return result;
    }

    static string ApplyFiles(string source, string target)
    {
        var files = ValidateInputs(source, target);
        var backup = Path.Combine(Directory.GetParent(source)!.FullName, "旧版程序-" + DateTime.Now.ToString("yyyyMMdd-HHmmss-fff"));
        Directory.CreateDirectory(backup);
        var touched = new List<(string Relative, bool Existed)>();
        try
        {
            foreach (var file in files)
            {
                var destination = Path.Combine(target, file.Relative);
                var existed = File.Exists(destination);
                if (existed)
                {
                    var saved = Path.Combine(backup, file.Relative);
                    Directory.CreateDirectory(Path.GetDirectoryName(saved)!);
                    File.Copy(destination, saved);
                }
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                var temp = destination + ".updating";
                File.Copy(file.Source, temp, true);
                // Atomic per-file replacement. Data is absent from the manifest by validation.
                File.Move(temp, destination, true);
                touched.Add((file.Relative, existed));
            }
            return backup;
        }
        catch
        {
            foreach (var file in touched.AsEnumerable().Reverse())
            {
                var destination = Path.Combine(target, file.Relative);
                try
                {
                    if (file.Existed) File.Copy(Path.Combine(backup, file.Relative), destination, true);
                    else File.Delete(destination);
                }
                catch { /* Backup is retained if a third-party lock prevents rollback. */ }
            }
            throw;
        }
    }
}
