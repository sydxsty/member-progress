using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using StudentProgress.Assistant;

namespace StudentProgress.Remote;

/// <summary>Small HTTP host sharing the desktop's sole business and assistant instances.</summary>
public sealed class MobileServer : IDisposable
{
    readonly AppService core;
    readonly AssistantService? assistant;
    readonly string webRoot;
    readonly Func<string, string, JsonObject> login;
    readonly Func<string, bool> authorize;
    readonly object hostGate = new(), changeGate = new();
    readonly SemaphoreSlim clients = new(48);
    readonly string sessionVersion = Guid.NewGuid().ToString("N");
    TaskCompletionSource changed = NewSignal();
    long revision;
    TcpListener? listener;
    CancellationTokenSource? lifetime;
    bool disposed;
    public event Action? StateChanged;
    public bool IsRunning { get { lock (hostGate) return listener != null; } }
    public int ListenPort { get; private set; }
    public string LastError { get; private set; } = "";

    public MobileServer(AppService core, AssistantService? assistant, string appRoot,
        Func<string, string, JsonObject> login, Func<string, bool> authorize)
    {
        this.core = core; this.assistant = assistant; this.login = login; this.authorize = authorize;
        webRoot = Path.Combine(Path.GetFullPath(appRoot), "web");
        core.StateChanged += Changed;
        if (assistant != null) assistant.StateChanged += Changed;
    }

    static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    void Changed()
    {
        // Core can publish while holding its lock, so this callback never reads either service.
        TaskCompletionSource old;
        lock (changeGate) { revision++; old = changed; changed = NewSignal(); }
        old.TrySetResult();
    }
    string CurrentVersion() => sessionVersion + ":" + revision.ToString(System.Globalization.CultureInfo.InvariantCulture);
    public void Start(int listenPort)
    {
        if (listenPort is < 1 or > 65535) throw new InvalidOperationException("请选择有效的本机端口。");
        Stop();
        lock (hostGate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            var next = new TcpListener(IPAddress.Any, listenPort);
            try { next.Start(32); }
            catch (SocketException) { LastError = "本机端口无法启用，请选择其他端口。"; throw new InvalidOperationException(LastError); }
            listener = next; ListenPort = listenPort; LastError = "";
            lifetime = new CancellationTokenSource();
            _ = AcceptAsync(next, lifetime.Token);
        }
        PublishStatus();
    }
    public void Stop()
    {
        lock (hostGate)
        {
            lifetime?.Cancel(); listener?.Stop(); listener = null;
            lifetime?.Dispose(); lifetime = null; ListenPort = 0;
        }
        Changed(); PublishStatus();
    }
    void PublishStatus() { try { StateChanged?.Invoke(); } catch { } }
    async Task AcceptAsync(TcpListener host, CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                var client = await host.AcceptTcpClientAsync(token).ConfigureAwait(false);
                if (!clients.Wait(0)) { client.Dispose(); continue; }
                _ = ServeAsync(client, token);
            }
        }
        catch (Exception e) when (e is OperationCanceledException or ObjectDisposedException or SocketException)
        {
            if (!token.IsCancellationRequested)
            {
                lock (hostGate) { if (listener == host) { listener = null; LastError = "手机连接已中断，可重新开启。"; } }
                PublishStatus();
            }
        }
    }
    async Task ServeAsync(TcpClient client, CancellationToken lifetimeToken)
    {
        using (client)
        using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(lifetimeToken))
        {
            deadline.CancelAfter(TimeSpan.FromSeconds(40));
            var token = deadline.Token;
            try
            {
                client.NoDelay = true;
                using var stream = client.GetStream();
                HttpReply reply;
                try
                {
                    var request = await ReadAsync(stream, token).ConfigureAwait(false);
                    reply = await RouteAsync(request, token).ConfigureAwait(false);
                }
                catch (MobileApiException e) { reply = Json(e.Status, new JsonObject { ["error"] = e.Message, ["code"] = e.Code }); }
                catch (JsonException) { reply = Json(400, new JsonObject { ["error"] = "请求内容格式不正确。" }); }
                catch (InvalidOperationException e) { reply = Json(400, new JsonObject { ["error"] = e.Message }); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                { reply = Json(503, new JsonObject { ["error"] = "电脑暂时无法保存，请保留输入并稍后重试。" }); }
                catch (OperationCanceledException) { return; }
                catch { reply = Json(500, new JsonObject { ["error"] = "电脑暂时无法完成操作，请稍后重试。" }); }
                await WriteAsync(stream, reply, token).ConfigureAwait(false);
            }
            catch (Exception e) when (e is IOException or SocketException or OperationCanceledException or ObjectDisposedException) { }
            finally { clients.Release(); }
        }
    }

    async Task<HttpReply> RouteAsync(HttpRequest request, CancellationToken token)
    {
        var path = request.Path;
        if (request.Method == "GET" && (path is "/" or "/mobile" or "/mobile/" || path.StartsWith("/mobile/", StringComparison.Ordinal) || path is "/gantt.js" or "/logo.png"))
            return Static(path);
        if (path == "/api/mobile/login" && request.Method == "POST")
        {
            var body = ParseObject(request.Body); MobileProtocol.Only(body, "password", "deviceName");
            MobileProtocol.StringFields(body, "password", "deviceName");
            var password = MobileProtocol.Text(body, "password"); var name = MobileProtocol.Text(body, "deviceName");
            if (password.Length is 0 or > 4096 || name.Length > 100) throw new MobileApiException(400, "请填写电脑端设置的密码。");
            JsonObject result;
            try { result = login(password, name); }
            catch (MobileApiException) { throw; }
            catch (InvalidOperationException e) { throw new MobileApiException(401, e.Message, "unauthorized"); }
            result["protocolVersion"] = MobileProtocol.Version;
            return Json(200, result);
        }
        if (!path.StartsWith("/api/mobile/", StringComparison.Ordinal)) throw new MobileApiException(404, "页面不存在。", "not_found");
        var auth = request.Headers.GetValueOrDefault("Authorization", "");
        if (!auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) || !authorize(auth[7..].Trim()))
            throw new MobileApiException(401, "连接已过期，请重新输入电脑端密码。", "unauthorized");
        switch (request.Method + " " + path)
        {
            case "GET /api/mobile/state": return Json(200, core.MobileSnapshot());
            case "GET /api/mobile/reminder-plan": return Json(200, core.MobileReminderPlan());
            case "GET /api/mobile/assistant": return Json(200, RequireAssistant().MobileSnapshot());
            case "GET /api/mobile/changes":
            {
                var requestedVersion = QueryValue(request.Query, "version");
                Task pending; bool isChanged;
                lock (changeGate) { isChanged = requestedVersion != CurrentVersion(); pending = changed.Task; }
                if (!isChanged)
                {
                    try { await pending.WaitAsync(TimeSpan.FromSeconds(25), token).ConfigureAwait(false); }
                    catch (TimeoutException) { }
                }
                // Revocation during a long poll takes effect before another response is served.
                if (!authorize(auth[7..].Trim())) throw new MobileApiException(401, "连接已过期，请重新登录。", "unauthorized");
                lock (changeGate) return Json(200, new JsonObject { ["version"] = CurrentVersion(), ["changed"] = requestedVersion != CurrentVersion() });
            }
            case "POST /api/mobile/action":
            {
                var body = ParseObject(request.Body); MobileProtocol.ReadRequest(body);
                var action = MobileProtocol.Text(body, "action");
                return Json(200, action.StartsWith("assistant.", StringComparison.Ordinal)
                    ? await RequireAssistant().HandleMobileAsync(body).ConfigureAwait(false)
                    : core.HandleMobileAction(body));
            }
            default: throw new MobileApiException(404, "手机端不支持此操作。", "not_found");
        }
    }
    AssistantService RequireAssistant() => assistant ?? throw new MobileApiException(503, "电脑端的 GPT 聊天暂时不可用。", "assistant_unavailable");
    static JsonObject ParseObject(byte[] body) => JsonNode.Parse(body, documentOptions: new JsonDocumentOptions { MaxDepth = 24 }) as JsonObject
        ?? throw new MobileApiException(400, "请求内容格式不正确。");
    static string QueryValue(string query, string key)
    {
        foreach (var part in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var split = part.Split('=', 2);
            if (Uri.UnescapeDataString(split[0]) == key) return split.Length > 1 ? Uri.UnescapeDataString(split[1]) : "";
        }
        return "";
    }
    HttpReply Static(string path)
    {
        var relative = path is "/" or "/mobile" or "/mobile/" ? "mobile/index.html" : path.TrimStart('/');
        if (relative.Contains('\\') || relative.Split('/').Any(p => p is "." or "..") || relative.Contains(':') || relative.Contains('\0'))
            throw new MobileApiException(404, "页面不存在。", "not_found");
        var target = Path.GetFullPath(Path.Combine(webRoot, relative.Replace('/', Path.DirectorySeparatorChar)));
        if (!target.StartsWith(webRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || !File.Exists(target))
            throw new MobileApiException(404, "页面暂时不可用，请检查电脑端更新。", "not_found");
        var mime = Path.GetExtension(target).ToLowerInvariant() switch
        {
            ".html" => "text/html; charset=utf-8", ".js" => "text/javascript; charset=utf-8", ".css" => "text/css; charset=utf-8",
            ".png" => "image/png", ".svg" => "image/svg+xml", ".ico" => "image/x-icon", ".apk" => "application/vnd.android.package-archive", _ => ""
        };
        if (mime.Length == 0 || new FileInfo(target).Length > 64 * 1024 * 1024) throw new MobileApiException(404, "页面不存在。", "not_found");
        return new(200, mime, File.ReadAllBytes(target));
    }

    sealed record HttpRequest(string Method, string Path, string Query, Dictionary<string, string> Headers, byte[] Body);
    sealed record HttpReply(int Status, string ContentType, byte[] Body);
    static HttpReply Json(int status, JsonNode? value) => new(status, "application/json; charset=utf-8", Encoding.UTF8.GetBytes(value?.ToJsonString() ?? "null"));

    static async Task<HttpRequest> ReadAsync(NetworkStream stream, CancellationToken token)
    {
        using var header = new MemoryStream(); var one = new byte[1]; int matched = 0;
        // A fixed limit and deadline bound malformed/incomplete headers without URLACL or extra runtimes.
        while (matched < 4)
        {
            if (header.Length >= 16 * 1024) throw new MobileApiException(431, "请求头过长。");
            if (await stream.ReadAsync(one, token).ConfigureAwait(false) == 0) throw new IOException("Connection closed.");
            header.WriteByte(one[0]);
            var expected = matched is 0 or 2 ? (byte)'\r' : (byte)'\n';
            matched = one[0] == expected ? matched + 1 : one[0] == '\r' ? 1 : 0;
        }
        var lines = Encoding.ASCII.GetString(header.ToArray()).Split("\r\n", StringSplitOptions.None);
        var start = lines[0].Split(' ');
        if (start.Length != 3 || start[2] is not ("HTTP/1.1" or "HTTP/1.0") || !start[1].StartsWith('/'))
            throw new MobileApiException(400, "HTTP 请求格式不正确。");
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in lines.Skip(1).Where(x => x.Length > 0))
        {
            var colon = line.IndexOf(':');
            if (colon <= 0 || char.IsWhiteSpace(line[0]) || !headers.TryAdd(line[..colon], line[(colon + 1)..].Trim()))
                throw new MobileApiException(400, "HTTP 请求头格式不正确。");
        }
        if (headers.ContainsKey("Transfer-Encoding")) throw new MobileApiException(400, "此请求传输格式不受支持。");
        var length = 0;
        if (headers.TryGetValue("Content-Length", out var text) && (!int.TryParse(text, out length) || length < 0))
            throw new MobileApiException(400, "请求长度不正确。");
        if (length > 2 * 1024 * 1024) throw new MobileApiException(413, "单次请求内容过长。");
        var body = new byte[length]; var count = 0;
        while (count < length)
        {
            var n = await stream.ReadAsync(body.AsMemory(count), token).ConfigureAwait(false);
            if (n == 0) throw new IOException("Connection closed."); count += n;
        }
        var target = start[1].Split('?', 2);
        string path;
        try { path = Uri.UnescapeDataString(target[0]); }
        catch (UriFormatException) { throw new MobileApiException(400, "请求地址不正确。"); }
        return new(start[0], path, target.Length > 1 ? target[1] : "", headers, body);
    }
    static async Task WriteAsync(NetworkStream stream, HttpReply reply, CancellationToken token)
    {
        var reason = reply.Status switch { 200 => "OK", 400 => "Bad Request", 401 => "Unauthorized", 404 => "Not Found", 409 => "Conflict", 413 => "Content Too Large", 431 => "Request Header Fields Too Large", 503 => "Service Unavailable", _ => "Internal Server Error" };
        var header = Encoding.ASCII.GetBytes($"HTTP/1.1 {reply.Status} {reason}\r\nContent-Type: {reply.ContentType}\r\nContent-Length: {reply.Body.Length}\r\nCache-Control: no-store\r\nX-Content-Type-Options: nosniff\r\nConnection: close\r\n\r\n");
        await stream.WriteAsync(header, token).ConfigureAwait(false);
        await stream.WriteAsync(reply.Body, token).ConfigureAwait(false);
    }
    public void Dispose()
    {
        if (disposed) return;
        disposed = true; core.StateChanged -= Changed;
        if (assistant != null) assistant.StateChanged -= Changed;
        Stop();
        // Existing requests own their semaphore permits until cancellation unwinds.
    }
}
