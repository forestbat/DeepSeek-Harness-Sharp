using System.Text.Json.Serialization;

namespace Dsh.RemoteHost;

/**
 * 远端工作区协议: 本地 GUI/CLI 与远端 `dsharp host --serve` 之间的握手、能力与推送方法名。
 * 帧格式由 Dsh.Transport(JsonRpcPeer) 提供; 这里只定义方法名与 DTO。
 */
public static class HostProtocol
{
    /** 协议版本: 不匹配时握手显式拒绝, 避免跨版本静默半功能/解析错位。 */
    public const int Version = 1;

    public const string MethodHello = "host.hello";
    public const string MethodPing = "host.ping";
    public const string MethodInfo = "host.info";

    public const string MethodSessionList = "host.session.list";
    public const string MethodSessionCreate = "host.session.create";
    public const string MethodSessionResume = "host.session.resume";
    public const string MethodSessionSubscribe = "host.session.subscribe";
    public const string MethodSessionUnsubscribe = "host.session.unsubscribe";
    public const string MethodSessionMessage = "host.session.message";
    public const string MethodSessionInterrupt = "host.session.interrupt";

    public const string MethodToolsList = "host.tools.list";
    public const string MethodApprovalRespond = "host.approval.respond";
    public const string MethodQuestionRespond = "host.question.respond";

    public const string MethodFileRead = "host.file.read";
    public const string MethodFileWrite = "host.file.write";
    public const string MethodFileList = "host.file.list";

    public const string MethodPtyStart = "host.pty.start";
    public const string MethodPtyWrite = "host.pty.write";
    public const string MethodPtyStop = "host.pty.stop";
    public const string MethodPtyAttach = "host.pty.attach";

    /** 服务端 -> 客户端 推送。 */
    public const string NotificationEvent = "host.event";
    public const string NotificationApproval = "host.approval";
    public const string NotificationQuestion = "host.question";
    public const string NotificationPtyOutput = "host.pty.output";
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
[JsonSerializable(typeof(bool))]
[JsonSerializable(typeof(RemoteSessionInfo))]
[JsonSerializable(typeof(RemoteEventInfo))]
[JsonSerializable(typeof(RemoteApprovalRequest))]
[JsonSerializable(typeof(RemoteApprovalResponse))]
[JsonSerializable(typeof(RemoteQuestionRequest))]
[JsonSerializable(typeof(RemoteQuestionItem))]
[JsonSerializable(typeof(RemoteQuestionOption))]
[JsonSerializable(typeof(RemoteQuestionResponse))]
[JsonSerializable(typeof(RemoteQuestionAnswerItem))]
[JsonSerializable(typeof(IReadOnlyList<RemoteQuestionItem>))]
[JsonSerializable(typeof(IReadOnlyList<RemoteQuestionAnswerItem>))]
[JsonSerializable(typeof(IReadOnlyList<RemoteQuestionOption>))]
[JsonSerializable(typeof(RemoteSessionCreateRequest))]
[JsonSerializable(typeof(RemoteSessionRef))]
[JsonSerializable(typeof(RemoteSubscribeRequest))]
[JsonSerializable(typeof(RemoteMessageRequest))]
[JsonSerializable(typeof(RemoteImageBlock))]
[JsonSerializable(typeof(IReadOnlyList<RemoteImageBlock>))]
[JsonSerializable(typeof(RemoteFilePath))]
[JsonSerializable(typeof(RemoteFileContent))]
[JsonSerializable(typeof(RemoteDirectoryEntry))]
[JsonSerializable(typeof(RemoteDirectoryListing))]
[JsonSerializable(typeof(RemotePtyStartRequest))]
[JsonSerializable(typeof(RemotePtyRef))]
[JsonSerializable(typeof(RemotePtyInput))]
[JsonSerializable(typeof(RemotePtyOutput))]
[JsonSerializable(typeof(IReadOnlyList<RemoteSessionInfo>))]
[JsonSerializable(typeof(IReadOnlyList<RemoteDirectoryEntry>))]
[JsonSerializable(typeof(IReadOnlyList<string>))]
public partial class HostProtocolJsonContext : JsonSerializerContext;
