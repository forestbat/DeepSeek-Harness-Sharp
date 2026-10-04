using Dsh.Llm;
using Dsh.Runtime.Events;

namespace Dsh.Core;

public sealed record AgentCreatedNotification(IAgent Agent) : INotification
{
    public static string EventName => "agent/created";
}

public sealed record AgentDisposedNotification(IAgent Agent) : INotification
{
    public static string EventName => "agent/disposed";
}

public sealed record AgentStatusNotification(IAgent Agent, AgentStatus Status) : INotification
{
    public static string EventName => "agent/status";
}

public sealed record AgentInboxInsertedNotification(IAgent Agent, UserMessage Message) : INotification
{
    public static string EventName => "agent/inbox/inserted";
}

public sealed record AgentInboxDiscardedNotification(IAgent Agent, UserMessage Message) : INotification
{
    public static string EventName => "agent/inbox/discarded";
}

public sealed record AgentInboxClaimedNotification(IAgent Agent, UserMessage Message, int Turn) : INotification
{
    public static string EventName => "agent/inbox/claimed";
}

public sealed record AgentSessionStartNotification(IAgent Agent, string Source) : INotification
{
    public static string EventName => "agent/session-start";
}

public sealed record AgentErrorNotification(IAgent Agent, int Turn, int Step, Exception Error) : INotification
{
    public static string EventName => "agent/error";
}

public sealed record AgentPreStepNotification(PreStepPayload Payload) : INotification
{
    public static string EventName => "agent/pre-step";
}

public sealed record AgentRequestNotification(AgentRequestPayload Payload) : INotification
{
    public static string EventName => "agent/request";
}

public sealed record AgentRequestErrorNotification(AgentRequestErrorPayload Payload) : INotification
{
    public static string EventName => "agent/request-error";
}

/** 一次请求因流中断/瞬时失败即将重试(Attempt 从 1 起, MaxAttempts 含首次), 供 UI 与遥测观测。 */
public sealed record AgentStreamInterruptedNotification(
    IAgent Agent,
    int Turn,
    int Step,
    string Provider,
    LlmFailure Failure,
    int Attempt,
    int MaxAttempts) : INotification
{
    public static string EventName => "agent/stream-interrupted";
}

public sealed record AgentTurnStoppingNotification(AgentTurnStoppingPayload Payload) : INotification
{
    public static string EventName => "agent/turn-stopping";
}
