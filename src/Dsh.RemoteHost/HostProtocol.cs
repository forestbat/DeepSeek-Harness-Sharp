using System.Text.Json;
using System.Text.Json.Serialization;

namespace Dsh.RemoteHost;

/**
 * 远端工作区协议(§14): 本地 GUI/CLI 与远端 `dsh host --serve` 之间的握手与能力协商。
 * 只定义协议常量与握手/信息 DTO; 具体能力方法在后续切片补入。
 */
public static class HostProtocol
{
    /** 协议版本: 不匹配时握手显式拒绝, 避免跨版本静默半功能/解析错位 (§14 §6)。 */
    public const int Version = 1;

    public const string MethodHello = "host.hello";
    public const string MethodPing = "host.ping";
    public const string MethodInfo = "host.info";
}

/** 握手请求: 客户端声明的协议版本与 token。 */
public sealed record HostHelloRequest(string? Token, int ProtocolVersion);

/** 握手响应: 服务端版本; Mismatch 非空即拒绝(附原因)。 */
public sealed record HostHelloResponse(bool Ok, int ProtocolVersion, string HostVersion, string? Mismatch);

/** 服务端信息。 */
public sealed record HostInfo(int ProtocolVersion, string HostVersion, string Platform, string HomeRoot);

/** 源生成的 JSON 上下文(AOT/裁剪安全)。 */
[JsonSourceGenerationOptions(DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(HostHelloRequest))]
[JsonSerializable(typeof(HostHelloResponse))]
[JsonSerializable(typeof(HostInfo))]
[JsonSerializable(typeof(long))]
public partial class HostProtocolJsonContext : JsonSerializerContext;
