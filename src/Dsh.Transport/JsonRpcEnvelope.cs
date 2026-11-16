using System.Text.Json;
using System.Text.Json.Serialization;

namespace Dsh.Transport;

/**
 * JSON-RPC 2.0 报文与错误对象。
 *
 * 这台机器上唯一还需要"字符串方法名 + JsonElement"的地方是浏览器中继: 浏览器侧协议按方法名调用,
 * 不能也不需要映射到 C# 契约。进程间调用一律走 JsonRpcChannel 的强类型代理。
 */
public sealed class JsonRpcError
{
    [JsonPropertyName("code")]
    public int Code { get; set; }

    [JsonPropertyName("message")]
    public string Message { get; set; } = "";

    [JsonPropertyName("data")]
    public JsonElement? Data { get; set; }
}

/** 一条 JSON-RPC 报文: 有 method 是请求(带 id)或通知(无 id); 无 method 而有 result/error 是响应。 */
public sealed class JsonRpcMessage
{
    [JsonPropertyName("jsonrpc")]
    public string Version { get; set; } = "2.0";

    [JsonPropertyName("id")]
    public JsonElement? Id { get; set; }

    [JsonPropertyName("method")]
    public string? Method { get; set; }

    [JsonPropertyName("params")]
    public JsonElement? Params { get; set; }

    [JsonPropertyName("result")]
    public JsonElement? Result { get; set; }

    [JsonPropertyName("error")]
    public JsonRpcError? Error { get; set; }

    [JsonIgnore]
    public bool IsRequest => Method is not null && Id is not null;

    [JsonIgnore]
    public bool IsNotification => Method is not null && Id is null;

    [JsonIgnore]
    public bool IsResponse => Method is null && (Result is not null || Error is not null);

    /** id 的稳定字符串键(string 原样; number 用原始文本), 用于请求/响应配对。 */
    [JsonIgnore]
    public string? IdKey => Id is { } id
        ? id.ValueKind switch
        {
            JsonValueKind.String => id.GetString(),
            JsonValueKind.Number => id.GetRawText(),
            _ => null,
        }
        : null;
}

/** 浏览器中继自造错误时抛它: 中继把 code 原样回给浏览器。 */
public sealed class JsonRpcException(int code, string message, JsonElement? data = null) : Exception(message)
{
    /** JSON-RPC 标准错误码: 参数非法。 */
    public const int InvalidParams = -32602;

    /** JSON-RPC 标准错误码: 方法不存在。 */
    public const int MethodNotFound = -32601;

    public int Code { get; } = code;

    public JsonElement? ErrorData { get; } = data;
}

/** 源生成的 JSON 上下文(AOT/裁剪安全)。 */
[JsonSourceGenerationOptions(DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(JsonRpcMessage))]
[JsonSerializable(typeof(JsonRpcError))]
[JsonSerializable(typeof(JsonElement))]
[JsonSerializable(typeof(long))]
public partial class JsonRpcJsonContext : JsonSerializerContext
{
}
