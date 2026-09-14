using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace StudentProgress.Remote;

internal sealed class FrpClient : IDisposable
{
    readonly string root, appRoot;
    readonly object gate = new();
    Process? process;
    bool disposed;
    string status = "未启用外网转发";
    public event Action? StateChanged;
    public string Status { get { lock(gate) return status; } }
    public bool Connected { get; private set; }

    public FrpClient(string dataRoot, string appDirectory)
    { root = Path.Combine(dataRoot, "remote"); appRoot = appDirectory; }

    public void Start(MobileConnectionSettings s)
    {
        Stop();
        if (!s.Enabled || !s.FrpEnabled) return;
        try
        {
            var bundledRoot = Path.Combine(appRoot, "runtime", "frp");
            var meta = JsonNode.Parse(File.ReadAllText(Path.Combine(bundledRoot, "bundled.json")))!.AsObject();
            var relative = meta["executableRelative"]!.GetValue<string>();
            var executable = Path.GetFullPath(Path.Combine(bundledRoot, relative));
            if (!executable.StartsWith(Path.GetFullPath(bundledRoot) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || !File.Exists(executable))
                throw new IOException("Missing client.");
            Directory.CreateDirectory(root);
            // JSON-quoted strings are also valid TOML basic strings; no shell is involved.
            var proxyName = "member-progress-" + Program.DataIdentity(root)[..12];
            var config = $"serverAddr = {JsonSerializer.Serialize(s.ServerAddr)}\nserverPort = {s.ServerPort}\nloginFailExit = false\nauth.method = \"token\"\nauth.token = {JsonSerializer.Serialize(s.FrpToken)}\nlog.to = \"console\"\nlog.level = \"info\"\nlog.disablePrintColor = true\n\n[[proxies]]\nname = {JsonSerializer.Serialize(proxyName)}\ntype = \"tcp\"\nlocalIP = \"127.0.0.1\"\nlocalPort = {s.ListenPort}\nremotePort = {s.RemotePort}\n";
            var configPath = Path.Combine(root, "frpc.toml");
            File.WriteAllText(configPath, config, new UTF8Encoding(false));
            var info = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = root };
            info.ArgumentList.Add("-c"); info.ArgumentList.Add(configPath);
            var child = new Process { StartInfo = info, EnableRaisingEvents = true };
            child.OutputDataReceived += (_, e) => ReadStatus(child, e.Data);
            child.ErrorDataReceived += (_, e) => ReadStatus(child, e.Data);
            child.Exited += (_, _) =>
            {
                lock(gate) { if(process != child || disposed) return; Connected = false; status = "外网转发已停止，可重新连接"; }
                StateChanged?.Invoke();
            };
            lock(gate) { if(disposed) { child.Dispose(); return; } process = child; status = "正在连接转发服务器…"; Connected = false; }
            child.Start(); child.BeginOutputReadLine(); child.BeginErrorReadLine();
        }
        catch
        {
            lock(gate) { Connected = false; status = "转发未启动，请检查组件和连接设置"; }
        }
        StateChanged?.Invoke();
    }

    void ReadStatus(Process child, string? line)
    {
        if (string.IsNullOrEmpty(line)) return;
        lock(gate)
        {
            if(process != child || disposed) return;
            if(line.Contains("start proxy success", StringComparison.OrdinalIgnoreCase))
            { Connected = true; status = "外网转发已连接"; }
            else if(line.Contains("port already used", StringComparison.OrdinalIgnoreCase) || line.Contains("port not allowed", StringComparison.OrdinalIgnoreCase))
            { Connected = false; status = "转发端口不可用，请换一个业务端口"; }
            else if(line.Contains("token", StringComparison.OrdinalIgnoreCase) && (line.Contains("fail", StringComparison.OrdinalIgnoreCase) || line.Contains("mismatch", StringComparison.OrdinalIgnoreCase)))
            { Connected = false; status = "转发口令不匹配，请检查设置"; }
            else if(line.Contains("login to server failed", StringComparison.OrdinalIgnoreCase) || line.Contains("reconnect", StringComparison.OrdinalIgnoreCase))
            { Connected = false; status = "转发暂未连通，正在重试…"; }
            else return;
        }
        StateChanged?.Invoke();
    }

    public void Stop()
    {
        Process? child;
        lock(gate) { child = process; process = null; Connected = false; status = "未启用外网转发"; }
        if(child != null)
        {
            try { if(!child.HasExited) child.Kill(entireProcessTree:true); } catch(InvalidOperationException) { } catch(System.ComponentModel.Win32Exception) { }
            child.Dispose();
        }
        StateChanged?.Invoke();
    }

    public void Dispose() { lock(gate) disposed = true; Stop(); }
}
