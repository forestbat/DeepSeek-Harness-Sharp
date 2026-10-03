using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using System.Threading.Channels;

namespace Dsh.A2A;

public static class A2aMethods
{
    public const string MessageSend = "message/send";
    public const string MessageStream = "message/stream";
    public const string TasksGet = "tasks/get";
    public const string TasksList = "tasks/list";
    public const string TasksCancel = "tasks/cancel";
    public const string TasksResubscribe = "tasks/resubscribe";
    public const string GetExtendedAgentCard = "agent/getExtendedAgentCard";

    public static readonly IReadOnlySet<string> PushNotificationConfig = new HashSet<string>
    {
        "tasks/pushNotificationConfig/set",
        "tasks/pushNotificationConfig/get",
        "tasks/pushNotificationConfig/list",
        "tasks/pushNotificationConfig/delete",
    };
}

public static class A2aErrorCodes
{
    public const int ParseError = -32700;
    public const int InvalidRequest = -32600;
    public const int MethodNotFound = -32601;
    public const int InvalidParams = -32602;
    public const int InternalError = -32603;
    public const int TaskNotFound = -32001;
    public const int TaskNotCancelable = -32002;
    public const int PushNotificationNotSupported = -32003;
    public const int UnsupportedOperation = -32004;
    public const int ContentTypeNotSupported = -32005;
    public const int InvalidAgentResponse = -32006;
    public const int AuthenticatedExtendedCardNotConfigured = -32007;
}

public sealed class A2aException(int code, string message) : Exception(message)
{
    public int Code { get; } = code;
}

/** 一次 SSE 流的全部素材：先播 backlog（任务快照 + 已存事件），live 非空时续播实时事件直到终态。 */
public sealed record A2aStreamHandle(string TaskId, IReadOnlyList<string> Backlog, ChannelReader<string>? Live);

public abstract record A2aDispatchOutcome
{
    public sealed record Value(string? ResultJson) : A2aDispatchOutcome;

    public sealed record Stream(A2aStreamHandle Handle) : A2aDispatchOutcome;
}

public sealed class A2aJsonRpc(A2aSessionBridge bridge)
{
    public async Task<A2aDispatchOutcome> DispatchAsync(string method, JsonElement? parameters, CancellationToken signal)
    {
        switch (method)
        {
            case A2aMethods.MessageSend:
                return await SendAsync(parameters, streaming: false, signal);
            case A2aMethods.MessageStream:
                return await SendAsync(parameters, streaming: true, signal);
            case A2aMethods.TasksGet:
                return new A2aDispatchOutcome.Value(A2aWire.TaskJson(bridge.GetTask(Deserialize(parameters, A2aJsonContext.Default.A2aGetTaskParams))));
            case A2aMethods.TasksList:
                return new A2aDispatchOutcome.Value(A2aWire.ListResultJson(bridge.ListTasks(Deserialize(parameters, A2aJsonContext.Default.A2aListTasksParams))));
            case A2aMethods.TasksCancel:
                return new A2aDispatchOutcome.Value(A2aWire.TaskJson(await bridge.CancelTaskAsync(Deserialize(parameters, A2aJsonContext.Default.A2aTaskIdParams))));
            case A2aMethods.TasksResubscribe:
                return new A2aDispatchOutcome.Stream(bridge.Resubscribe(Deserialize(parameters, A2aJsonContext.Default.A2aTaskIdParams)));
            case A2aMethods.GetExtendedAgentCard:
                throw new A2aException(A2aErrorCodes.AuthenticatedExtendedCardNotConfigured,
                    "this agent does not provide an authenticated extended agent card");
            default:
                if (A2aMethods.PushNotificationConfig.Contains(method))
                {
                    throw new A2aException(A2aErrorCodes.PushNotificationNotSupported,
                        "push notifications are not supported by this agent");
                }
                throw new A2aException(A2aErrorCodes.MethodNotFound, $"unknown A2A method: {method}");
        }
    }

    private async Task<A2aDispatchOutcome> SendAsync(JsonElement? parameters, bool streaming, CancellationToken signal)
    {
        if (parameters is not { } element)
            throw new A2aException(A2aErrorCodes.InvalidParams, "missing message/send params");
        A2aSendMessageParams send;
        try
        {
            send = element.Deserialize(A2aJsonContext.Default.A2aSendMessageParams)
                ?? throw new A2aException(A2aErrorCodes.InvalidParams, "invalid message/send params");
        }
        catch (JsonException error) when (error.Path?.Contains("parts", StringComparison.Ordinal) == true)
        {
            throw new A2aException(A2aErrorCodes.ContentTypeNotSupported, $"unsupported message part: {error.Message}");
        }
        catch (JsonException error)
        {
            throw new A2aException(A2aErrorCodes.InvalidParams, $"invalid message/send params: {error.Message}");
        }
        var (task, stream) = await bridge.SendAsync(send, streaming, signal);
        return stream is not null
            ? new A2aDispatchOutcome.Stream(stream)
            : new A2aDispatchOutcome.Value(A2aWire.TaskJson(task));
    }

    private static T Deserialize<T>(JsonElement? parameters, JsonTypeInfo<T> typeInfo) where T : class
    {
        if (parameters is not { } element)
            throw new A2aException(A2aErrorCodes.InvalidParams, $"missing {typeof(T).Name} params");
        try
        {
            return element.Deserialize(typeInfo)
                ?? throw new A2aException(A2aErrorCodes.InvalidParams, $"invalid {typeof(T).Name} params");
        }
        catch (JsonException error)
        {
            throw new A2aException(A2aErrorCodes.InvalidParams, $"invalid {typeof(T).Name} params: {error.Message}");
        }
    }
}
