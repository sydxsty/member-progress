using System.Diagnostics;
using System.Formats.Tar;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace StudentProgress.Assistant;

internal sealed class CodexRuntime : IDisposable
{
    private sealed class RuntimeState
    {
        public string ActiveVersion { get; set; } = "";
        public string ExecutableRelative { get; set; } = "";
        public string LatestVersion { get; set; } = "";
        public string LastCheckedAt { get; set; } = "";
    }
    private static readonly Regex StableVersion = new(@"^\d+\.\d+\.\d+$", RegexOptions.CultureInvariant);
    private readonly string runtimeRoot, statePath, bundledRoot;
    private readonly object sync = new();
    private readonly HttpClient http;
    private RuntimeState state;
    private string executable = "", version = "", updateStatus = "";
    internal event Action? StateChanged;
    internal CodexRuntime(string moduleRoot, string appRoot)
    {
        runtimeRoot = Path.Combine(moduleRoot, "runtime");
        statePath = Path.Combine(runtimeRoot, "state.json");
        bundledRoot = Path.Combine(appRoot, "runtime", "codex");
        http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromMinutes(6) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("StudentProgress/1.0");
        try { state = File.Exists(statePath) ? JsonSerializer.Deserialize<RuntimeState>(File.ReadAllText(statePath), AssistantJson.Options) ?? new() : new(); }
        catch (Exception e) when (e is JsonException or IOException) { state = new(); }
        if (TryExecutable(runtimeRoot, state.ActiveVersion, state.ExecutableRelative, out var cached))
        { executable = cached; version = state.ActiveVersion; }
        else
        {
            try
            {
                var bundled = JsonNode.Parse(File.ReadAllText(Path.Combine(bundledRoot, "bundled.json"))) as JsonObject;
                var bundledVersion = AssistantJson.Text(bundled?["version"]);
                if (TryExecutable(bundledRoot, bundledVersion, AssistantJson.Text(bundled?["executableRelative"]), out var path))
                { executable = path; version = bundledVersion; }
            }
            catch (Exception e) when (e is IOException or JsonException) { }
        }
        if (string.IsNullOrEmpty(executable)) updateStatus = "内置组件未找到，可检查更新或重新解压应用。";
    }
    internal string Executable { get { lock(sync) return executable; } }
    internal bool CheckedToday
    {
        get { lock(sync) return DateTimeOffset.TryParse(state.LastCheckedAt, out var last) && last.ToLocalTime().Date == DateTimeOffset.Now.Date; }
    }
    internal bool HasUpdate { get { lock(sync) return IsNewer(state.LatestVersion, version); } }
    internal JsonObject Snapshot(bool autoUpdate)
    {
        lock(sync) return AssistantJson.Object(new {available = File.Exists(executable), version, latestVersion = state.LatestVersion, autoUpdate, updateStatus});
    }
    private void Status(string text)
    { lock(sync) updateStatus = text; StateChanged?.Invoke(); }
    private void Save() => AssistantJson.WriteAtomic(statePath, state);
    internal async Task CheckAsync(CancellationToken cancellation)
    {
        Status("正在检查组件更新…");
        try
        {
            var latest = await GetJsonAsync(new Uri("https://registry.npmjs.org/@openai/codex/latest"), cancellation).ConfigureAwait(false);
            string candidate = AssistantJson.Text(latest["version"]);
            if (!StableVersion.IsMatch(candidate)) throw new InvalidDataException("没有找到稳定版组件。");
            lock(sync)
            {
                state.LatestVersion = candidate;
                state.LastCheckedAt = DateTimeOffset.Now.ToString("O");
                Save();
            }
            Status(HasUpdate ? $"有新版本 {candidate} 可更新。" : "已是最新稳定版。");
        }
        catch (OperationCanceledException) { Status("组件检查已取消。"); throw; }
        catch (Exception e) when (e is HttpRequestException or IOException or JsonException or InvalidDataException)
        {
            lock(sync) { state.LastCheckedAt = DateTimeOffset.Now.ToString("O"); Save(); }
            Status("暂时无法检查更新，现有组件可继续使用。");
            throw new InvalidOperationException("暂时无法检查组件更新，请稍后重试。");
        }
    }
    internal async Task UpdateAsync(CancellationToken cancellation)
    {
        string candidate;
        lock(sync) candidate = state.LatestVersion;
        if (!StableVersion.IsMatch(candidate))
        {
            await CheckAsync(cancellation).ConfigureAwait(false);
            lock(sync) candidate = state.LatestVersion;
        }
        if (!HasUpdate && File.Exists(Executable)) { Status("已是最新稳定版。"); return; }
        string staging = Path.Combine(runtimeRoot, ".staging-" + Guid.NewGuid().ToString("N"));
        string package = Path.Combine(runtimeRoot, ".download-" + Guid.NewGuid().ToString("N") + ".tgz");
        Directory.CreateDirectory(runtimeRoot);
        try
        {
            Status($"正在下载组件 {candidate}…");
            var meta = await GetJsonAsync(new Uri($"https://registry.npmjs.org/@openai/codex/{candidate}-win32-x64"), cancellation).ConfigureAwait(false);
            if (AssistantJson.Text(meta["name"]) != "@openai/codex" || AssistantJson.Text(meta["version"]) != candidate + "-win32-x64")
                throw new InvalidDataException("组件元数据版本不符。");
            string integrity = AssistantJson.Text(meta["dist"]?["integrity"]);
            var tarball = new Uri(AssistantJson.Text(meta["dist"]?["tarball"]));
            ValidateTarball(tarball, candidate);
            var sha = ParseIntegrity(integrity);
            await DownloadAsync(tarball, package, sha, cancellation).ConfigureAwait(false);
            Status($"正在校验并准备组件 {candidate}…");
            Directory.CreateDirectory(staging);
            await ExtractSafeAsync(package, staging, cancellation).ConfigureAwait(false);
            string relativeInside = File.Exists(Path.Combine(staging, "vendor", "x86_64-pc-windows-msvc", "bin", "codex.exe"))
                ? "vendor/x86_64-pc-windows-msvc/bin/codex.exe" : "vendor/x86_64-pc-windows-msvc/codex/codex.exe";
            string stageExecutable = Path.Combine(staging, relativeInside.Replace('/', Path.DirectorySeparatorChar));
            if (!await VerifyVersionAsync(stageExecutable, candidate, cancellation).ConfigureAwait(false))
                throw new InvalidDataException("组件版本验证未通过。");
            string final = Path.Combine(runtimeRoot, candidate);
            if (Directory.Exists(final))
            {
                if (!await VerifyVersionAsync(Path.Combine(final, relativeInside.Replace('/', Path.DirectorySeparatorChar)), candidate, cancellation).ConfigureAwait(false))
                    throw new InvalidDataException("已有的组件目录无法验证，原版本继续保留。");
            }
            else Directory.Move(staging, final);
            lock(sync)
            {
                state.ActiveVersion = candidate;
                state.ExecutableRelative = candidate + "/" + relativeInside;
                Save();
                executable = Path.Combine(final, relativeInside.Replace('/', Path.DirectorySeparatorChar));
                version = candidate;
            }
            Status($"已更新至 {candidate}，旧版组件已保留。");
        }
        catch (OperationCanceledException) { Status("更新已取消，原组件继续保留。"); throw; }
        catch (Exception e) when (e is HttpRequestException or IOException or JsonException or InvalidDataException or UriFormatException or CryptographicException or System.ComponentModel.Win32Exception)
        {
            Status("更新未完成，原组件继续保留。");
            throw new InvalidOperationException("组件下载或验证未完成，原版本已保留，请稍后重试。");
        }
        finally
        {
            try { if (File.Exists(package)) File.Delete(package); } catch (IOException) { }
            try { if (Directory.Exists(staging)) Directory.Delete(staging, recursive:true); } catch (IOException) { }
        }
    }
    internal static bool IsNewer(string candidate, string current) => Version.TryParse(candidate, out var next) && (!Version.TryParse(current, out var now) || next > now);
    private static bool TryExecutable(string root, string version, string relative, out string result)
    {
        result = "";
        if (!StableVersion.IsMatch(version)) return false;
        string normalized = relative.Replace('\\', '/');
        if (normalized != $"{version}/vendor/x86_64-pc-windows-msvc/bin/codex.exe" && normalized != $"{version}/vendor/x86_64-pc-windows-msvc/codex/codex.exe") return false;
        result = Path.GetFullPath(Path.Combine(root, normalized.Replace('/', Path.DirectorySeparatorChar)));
        return File.Exists(result);
    }
    internal static void ValidateTarball(Uri uri, string version)
    {
        if (uri.Scheme != "https" || uri.Host != "registry.npmjs.org" || !uri.IsDefaultPort || !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment) || uri.AbsolutePath != $"/@openai/codex/-/codex-{version}-win32-x64.tgz")
            throw new InvalidDataException("组件必须来自官方 npm 发布地址。");
    }
    internal static byte[] ParseIntegrity(string integrity)
    {
        string? part = integrity.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault(s=>s.StartsWith("sha512-", StringComparison.Ordinal));
        if (part == null) throw new InvalidDataException("组件缺少 SHA512 校验信息。");
        try { var hash = Convert.FromBase64String(part[7..]); if (hash.Length != 64) throw new FormatException(); return hash; }
        catch(FormatException) { throw new InvalidDataException("组件 SHA512 校验信息无效。"); }
    }
    private async Task<JsonObject> GetJsonAsync(Uri uri, CancellationToken cancellation)
    {
        using var response = await http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellation).ConfigureAwait(false);
        if ((int)response.StatusCode is >= 300 and < 400) throw new InvalidDataException("组件地址不允许重定向。");
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength is > 2_000_000) throw new InvalidDataException("组件元数据过长。");
        using var input = await response.Content.ReadAsStreamAsync(cancellation).ConfigureAwait(false);
        using var memory = new MemoryStream();
        byte[] buffer = new byte[8192]; int read;
        while ((read = await input.ReadAsync(buffer, cancellation).ConfigureAwait(false)) > 0)
        {
            if (memory.Length + read > 2_000_000) throw new InvalidDataException("组件元数据过长。");
            memory.Write(buffer, 0, read);
        }
        return JsonNode.Parse(memory.ToArray()) as JsonObject ?? throw new InvalidDataException("组件元数据无效。");
    }
    private async Task DownloadAsync(Uri uri, string target, byte[] expected, CancellationToken cancellation)
    {
        using var response = await http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellation).ConfigureAwait(false);
        if ((int)response.StatusCode is >= 300 and < 400) throw new InvalidDataException("组件地址不允许重定向。");
        response.EnsureSuccessStatusCode();
        const long max = 350L * 1024 * 1024;
        if (response.Content.Headers.ContentLength > max) throw new InvalidDataException("组件包超过大小限制。");
        using var input = await response.Content.ReadAsStreamAsync(cancellation).ConfigureAwait(false);
        await using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync:true);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA512);
        byte[] buffer = new byte[81920]; int read; long count = 0;
        while ((read = await input.ReadAsync(buffer, cancellation).ConfigureAwait(false)) > 0)
        {
            count += read; if (count > max) throw new InvalidDataException("组件包超过大小限制。");
            hash.AppendData(buffer, 0, read);
            await output.WriteAsync(buffer.AsMemory(0, read), cancellation).ConfigureAwait(false);
        }
        if (!CryptographicOperations.FixedTimeEquals(hash.GetHashAndReset(), expected)) throw new CryptographicException("组件完整性校验失败。");
    }
    internal static string SafeEntryPath(string staging, string name)
    {
        string clean = name.Replace('\\', '/');
        if (clean.StartsWith("package/", StringComparison.Ordinal)) clean = clean[8..];
        var parts = clean.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (clean.StartsWith('/') || parts.Length == 0 || parts.Any(p=>p is "." or ".." || p.Contains(':') || p.EndsWith('.') || p.EndsWith(' ') || Regex.IsMatch(p, @"^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(?:\.|$)", RegexOptions.IgnoreCase)))
            throw new InvalidDataException("组件包含无效的文件路径。");
        var root = Path.GetFullPath(staging) + Path.DirectorySeparatorChar;
        var destination = Path.GetFullPath(Path.Combine(staging, Path.Combine(parts)));
        if (!destination.StartsWith(root, OperatingSystem.IsWindows()?StringComparison.OrdinalIgnoreCase:StringComparison.Ordinal))
            throw new InvalidDataException("组件文件路径越界。");
        return destination;
    }
    private static async Task ExtractSafeAsync(string archive, string staging, CancellationToken cancellation)
    {
        using var input = File.OpenRead(archive);
        using var gzip = new GZipStream(input, CompressionMode.Decompress);
        using var tar = new TarReader(gzip);
        long bytes = 0; int entries = 0;
        while (await tar.GetNextEntryAsync(copyData:false, cancellationToken:cancellation).ConfigureAwait(false) is { } entry)
        {
            if (++entries > 1000) throw new InvalidDataException("组件文件数超过限制。");
            if (entry.Name.TrimEnd('/') == "package" && entry.EntryType == TarEntryType.Directory) continue;
            string path = SafeEntryPath(staging, entry.Name);
            if (entry.EntryType == TarEntryType.Directory) { Directory.CreateDirectory(path); continue; }
            if (entry.EntryType is not (TarEntryType.RegularFile or TarEntryType.V7RegularFile)) throw new InvalidDataException("组件包含不允许的链接或特殊文件。");
            bytes += entry.Length;
            if (bytes > 700L * 1024 * 1024 || entry.Length > 400L * 1024 * 1024) throw new InvalidDataException("组件解压内容超过限制。");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync:true);
            if (entry.DataStream != null) await entry.DataStream.CopyToAsync(output, cancellation).ConfigureAwait(false);
        }
    }
    private static async Task<bool> VerifyVersionAsync(string executable, string version, CancellationToken cancellation)
    {
        if (!File.Exists(executable)) return false;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellation, timeout.Token);
        var start = new ProcessStartInfo(executable) { UseShellExecute=false, CreateNoWindow=true, RedirectStandardOutput=true, RedirectStandardError=true };
        start.ArgumentList.Add("--version");
        using var process = Process.Start(start);
        if (process == null) return false;
        try
        {
            var output = process.StandardOutput.ReadToEndAsync(linked.Token);
            var error = process.StandardError.ReadToEndAsync(linked.Token);
            await process.WaitForExitAsync(linked.Token).ConfigureAwait(false);
            await Task.WhenAll(output, error).ConfigureAwait(false);
            return process.ExitCode == 0 && Regex.IsMatch(output.Result.Trim(), @"^codex(?:-cli)?\s+" + Regex.Escape(version) + @"(?:\s|$)");
        }
        finally { try { if (!process.HasExited) process.Kill(entireProcessTree:true); } catch (InvalidOperationException) { } }
    }
    public void Dispose() => http.Dispose();
}
