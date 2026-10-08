using System.Text.Json;
using System.Text.Json.Serialization;

namespace Dsh.Transport;

/** JSON-RPC 2.0 错误对象。 */
public sealed class JsonRpcError
{
    [JsonPropertyName("code")]
    public int Code { get; set; }

    [JsonPropertyName("message")]
    public string Message { get; set; } = "";

    [JsonPropertyName("data")]
    public JsonElement? Data { get; set; }
}

/**
 * JSON-RPC 2.0 报文(请求/响应/通知共用一个类型): 按字段存在性区分——
 * 有 method 是请求(带 id)或通知(无 id); 无 method 而有 result/error 是响应。
 * params/result/data 用 JsonElement, 免为每种方法建强类型; 序列化走源生成, AOT/裁剪安全。
 */
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

/** 源生成的 JSON 上下文(AOT/裁剪安全): 不为动态这些类型走反射。 */
[JsonSourceGenerationOptions(DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(JsonRpcMessage))]
[JsonSerializable(typeof(JsonRpcError))]
[JsonSerializable(typeof(JsonElement))]
[JsonSerializable(typeof(long))]
public partial class JsonRpcJsonContext : JsonSerializerContext
{
}
