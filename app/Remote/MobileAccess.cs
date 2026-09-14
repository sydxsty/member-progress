using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace StudentProgress.Remote;

internal sealed class MobileConnectionSettings
{
    public bool Enabled { get; set; }
    public int ListenPort { get; set; } = 18761;
    public string Password { get; set; } = "";
    public bool FrpEnabled { get; set; }
    public string ServerAddr { get; set; } = "";
    public int ServerPort { get; set; } = 17520;
    public int RemotePort { get; set; } = 27521;
    public string FrpToken { get; set; } = "";
    public List<MobileDevice> Devices { get; set; } = [];
}

internal sealed class MobileDevice
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "手机";
    public string Token { get; set; } = "";
    public string CreatedAt { get; set; } = DateTimeOffset.Now.ToString("O");
}

// Local desktop settings only. The mobile API never receives this object.
internal sealed class MobileAccess
{
    static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true };
    readonly object gate = new();
    readonly string path;
    MobileConnectionSettings settings;
    public event Action? Changed;

    public MobileAccess(string dataRoot)
    {
        path = Path.Combine(dataRoot, "remote", "connection.json");
        settings = File.Exists(path)
            ? JsonSerializer.Deserialize<MobileConnectionSettings>(File.ReadAllText(path), Json) ?? throw new InvalidOperationException("手机连接设置无法读取，原文件已保留。")
            : new();
        Validate(settings);
    }

    public MobileConnectionSettings Configuration()
    {
        lock(gate) return JsonSerializer.Deserialize<MobileConnectionSettings>(JsonSerializer.Serialize(settings, Json), Json)!;
    }

    public JsonObject Snapshot()
    {
        lock(gate) return new JsonObject
        {
            ["enabled"] = settings.Enabled, ["listenPort"] = settings.ListenPort,
            ["password"] = settings.Password, ["frpEnabled"] = settings.FrpEnabled,
            ["serverAddr"] = settings.ServerAddr, ["serverPort"] = settings.ServerPort,
            ["remotePort"] = settings.RemotePort, ["frpTokenConfigured"] = settings.FrpToken.Length > 0,
            ["publicUrl"] = settings.ServerAddr.Length > 0 ? $"http://{settings.ServerAddr}:{settings.RemotePort}" : "",
            ["devices"] = new JsonArray(settings.Devices.Select(d => (JsonNode)new JsonObject
            { ["id"] = d.Id, ["name"] = d.Name, ["createdAt"] = d.CreatedAt }).ToArray())
        };
    }

    public void SaveSettings(JsonObject input)
    {
        lock(gate)
        {
            var next = Configuration();
            next.Enabled = input["enabled"]?.GetValue<bool>() ?? next.Enabled;
            next.ListenPort = input["listenPort"]?.GetValue<int>() ?? next.ListenPort;
            next.Password = input["password"]?.GetValue<string>() ?? next.Password;
            next.FrpEnabled = input["frpEnabled"]?.GetValue<bool>() ?? next.FrpEnabled;
            next.ServerAddr = (input["serverAddr"]?.GetValue<string>() ?? next.ServerAddr).Trim();
            next.ServerPort = input["serverPort"]?.GetValue<int>() ?? next.ServerPort;
            next.RemotePort = input["remotePort"]?.GetValue<int>() ?? next.RemotePort;
            if (input["frpToken"] is JsonValue value && value.TryGetValue<string>(out var token) && !string.IsNullOrEmpty(token)) next.FrpToken = token.Trim();
            Validate(next);
            if (next.Password != settings.Password) next.Devices.Clear();
            Write(next); settings = next;
        }
        Changed?.Invoke();
    }

    public JsonObject Login(string password, string deviceName)
    {
        JsonObject result;
        lock(gate)
        {
            if (!settings.Enabled || settings.Password.Length == 0 || password != settings.Password)
                throw new InvalidOperationException("连接密码不正确，或电脑尚未开启手机连接。");
            var next = Configuration();
            var device = new MobileDevice { Name = string.IsNullOrWhiteSpace(deviceName) ? "手机" : deviceName.Trim()[..Math.Min(deviceName.Trim().Length, 80)], Token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant() };
            next.Devices.Add(device);
            Write(next); settings = next;
            result = new JsonObject { ["token"] = device.Token, ["protocolVersion"] = 1 };
        }
        Changed?.Invoke();
        return result;
    }

    public bool Authorize(string token)
    {
        lock(gate) return settings.Enabled && token.Length > 0 && settings.Devices.Any(d => d.Token == token);
    }

    public void Revoke(string id)
    {
        lock(gate)
        {
            var next = Configuration();
            next.Devices.RemoveAll(d => d.Id == id);
            Write(next); settings = next;
        }
        Changed?.Invoke();
    }

    static void Validate(MobileConnectionSettings s)
    {
        if (s.ListenPort is < 1024 or > 65535 || s.ServerPort is < 1 or > 65535 || s.RemotePort is < 1024 or > 65535)
            throw new InvalidOperationException("请填写有效的连接端口。");
        if (s.Password.Length > 256 || s.FrpToken.Length > 2048)
            throw new InvalidOperationException("连接密码或转发口令过长。");
        if (s.Enabled && string.IsNullOrWhiteSpace(s.Password))
            throw new InvalidOperationException("请先设置手机连接密码。");
        if (s.ServerAddr.Length > 253 || (s.ServerAddr.Length > 0 && Uri.CheckHostName(s.ServerAddr) == UriHostNameType.Unknown))
            throw new InvalidOperationException("服务器地址只填写域名或 IP，不包含协议和端口。");
        if (s.Enabled && s.FrpEnabled && (s.ServerAddr.Length == 0 || s.FrpToken.Length == 0))
            throw new InvalidOperationException("请填写转发服务器和转发口令。");
        s.Devices ??= [];
    }

    void Write(MobileConnectionSettings value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { JsonSerializer.Serialize(stream, value, Json); stream.Flush(true); }
            File.Move(temp, path, overwrite:true);
        }
        finally { if(File.Exists(temp)) File.Delete(temp); }
    }
}
