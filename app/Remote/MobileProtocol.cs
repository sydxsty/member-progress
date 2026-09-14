using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace StudentProgress.Remote;

public sealed class MobileApiException : InvalidOperationException
{
    public int Status { get; }
    public string Code { get; }
    public MobileApiException(int status, string message, string code = "invalid_request") : base(message)
    { Status = status; Code = code; }
}

public static class MobileProtocol
{
    public const int Version = 1;
    public static string Text(JsonObject value, string key) => value[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s : "";
    public static string Required(JsonObject value, string key, int max = 128)
    {
        var text = Text(value, key).Trim();
        if (text.Length == 0 || text.Length > max) throw new MobileApiException(400, "请求内容不完整或过长。");
        return text;
    }
    public static string Fingerprint(JsonNode? node)
    {
        // Canonical property order makes a retried JSON request independent of serializer order.
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer)) WriteCanonical(writer, node);
        return Convert.ToHexString(SHA256.HashData(buffer.ToArray())).ToLowerInvariant();
    }
    static void WriteCanonical(Utf8JsonWriter writer, JsonNode? node)
    {
        if (node is JsonObject obj)
        {
            writer.WriteStartObject();
            foreach (var pair in obj.OrderBy(x => x.Key, StringComparer.Ordinal))
            { writer.WritePropertyName(pair.Key); WriteCanonical(writer, pair.Value); }
            writer.WriteEndObject();
        }
        else if (node is JsonArray array)
        { writer.WriteStartArray(); foreach (var value in array) WriteCanonical(writer, value); writer.WriteEndArray(); }
        else if (node == null) writer.WriteNullValue();
        else node.WriteTo(writer);
    }
    public static void Only(JsonObject value, params string[] fields)
    {
        var allowed = fields.ToHashSet(StringComparer.Ordinal);
        if (value.Any(x => !allowed.Contains(x.Key))) throw new MobileApiException(400, "手机端不支持修改这些内容。");
    }
    public static void StringFields(JsonObject value, params string[] fields)
    {
        foreach (var field in fields)
            if (value.ContainsKey(field) && value[field] != null && (value[field] is not JsonValue v || !v.TryGetValue<string>(out _)))
                throw new MobileApiException(400, "文字内容格式不正确。");
    }
    public static void BooleanFields(JsonObject value, params string[] fields)
    {
        foreach (var field in fields)
            if (value.ContainsKey(field) && (value[field] is not JsonValue v || !v.TryGetValue<bool>(out _)))
                throw new MobileApiException(400, "请选择有效的开关状态。");
    }
    public static void StringArray(JsonObject value, string field, int max)
    {
        if (!value.ContainsKey(field)) return;
        if (value[field] is not JsonArray a || a.Count > max || a.Any(x => x is not JsonValue v || !v.TryGetValue<string>(out _)))
            throw new MobileApiException(400, "列表内容格式不正确。");
    }
    public static void CheckVersion(JsonObject request, string actual, bool required = true)
    {
        var expected = Text(request, "expectedVersion");
        if ((required || expected.Length > 0) && !string.Equals(expected, actual, StringComparison.Ordinal))
            throw new MobileApiException(409, "电脑端的内容已更新；你的输入已保留，请刷新后再保存。", "conflict");
    }
    public static JsonObject ReadRequest(JsonObject request)
    {
        Only(request, "action", "payload", "requestId", "expectedVersion");
        Required(request, "action", 80); Required(request, "requestId", 128);
        StringFields(request, "action", "requestId", "expectedVersion");
        return request["payload"] as JsonObject ?? throw new MobileApiException(400, "请求内容格式不正确。");
    }
}
