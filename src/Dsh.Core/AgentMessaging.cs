using System.Text.Json.Serialization;
using Dsh.Llm;

namespace Dsh.Core;

[JsonConverter(typeof(JsonStringEnumConverter<PrincipalKind>))]
public enum PrincipalKind
{
    Human,
    Agent,
    ExternalChannel,
    System,
    RemoteUser,
}

[JsonConverter(typeof(JsonStringEnumConverter<MessageTrustLevel>))]
public enum MessageTrustLevel
{
    OwnerHuman,
    DelegatedHuman,
    TrustedPeerAgent,
    ExternalAgent,
}

[JsonConverter(typeof(JsonStringEnumConverter<AgentMessageIntent>))]
public enum AgentMessageIntent
{
    Notify,
    Task,
    Steer,
    Control,
}

[JsonConverter(typeof(JsonStringEnumConverter<AgentDeliveryStatus>))]
public enum AgentDeliveryStatus
{
    DeliveredLive,
    QueuedOffline,
    WokeAndDelivered,
    Deduplicated,
    Rejected,
}

[JsonConverter(typeof(JsonStringEnumConverter<AgentRuntimeEventKind>))]
public enum AgentRuntimeEventKind
{
    TurnStarted,
    TurnCompleted,
    TurnFailed,
    ApprovalRequested,
    AgentWoken,
    AgentSuspended,
    MessageDelivered,
    Custom,
}

public sealed record MessagePrincipal(
    PrincipalKind Kind,
    string Id,
    string? DisplayName = null,
    string? MachineId = null,
    string? ChannelId = null);

public sealed record MessageAttribution(
    MessagePrincipal Sender,
    MessageTrustLevel TrustLevel,
    AgentMessageIntent Intent,
    string? EnvelopeId = null,
    string? CorrelationId = null);

public sealed record AgentEnvelope(
    string EnvelopeId,
    AgentId TargetAgentId,
    SessionId? TargetSessionId,
    MessagePrincipal Sender,
    MessageTrustLevel TrustLevel,
    AgentMessageIntent Intent,
    string Body,
    string? CorrelationId = null,
    string? IdempotencyKey = null,
    long CreatedAt = 0,
    bool WakeIfSuspended = true,
    IReadOnlyDictionary<string, string>? Metadata = null);

public sealed record AgentDeliveryResult(
    string EnvelopeId,
    AgentDeliveryStatus Status,
    AgentId TargetAgentId,
    SessionId? DeliveredSessionId = null,
    string? Reason = null);

public sealed record AgentRuntimeEvent(
    string EventId,
    AgentId AgentId,
    SessionId? SessionId,
    AgentRuntimeEventKind Kind,
    string Summary,
    long Timestamp,
    string? CorrelationId = null,
    string? Detail = null);

public sealed record AgentEventSubscription(
    string SubscriptionId,
    AgentId PublisherAgentId,
    AgentId? SubscriberAgentId,
    IReadOnlyList<AgentRuntimeEventKind>? Kinds = null,
    bool WakeSubscriber = true);

public interface IOfflineMailboxStore
{
    const string ServiceName = "offlineMailbox";

    bool Enqueue(AgentEnvelope envelope);
    IReadOnlyList<AgentEnvelope> Peek(AgentId agentId);
    IReadOnlyList<AgentEnvelope> Drain(AgentId agentId);
    bool Remove(AgentId agentId, string envelopeId);
}

public interface IAgentMessageBus
{
    const string ServiceName = "agentBus";

    Task<AgentDeliveryResult> SendAsync(AgentEnvelope envelope, CancellationToken cancellationToken = default);
    Task<int> FlushOfflineAsync(AgentId agentId, CancellationToken cancellationToken = default);
    IReadOnlyList<AgentEnvelope> PeekOffline(AgentId agentId);
    IDisposable Subscribe(AgentEventSubscription subscription, Func<AgentRuntimeEvent, ValueTask>? handler = null);
    IReadOnlyList<AgentEventSubscription> ListSubscriptions(AgentId? publisher = null);
    Task PublishEventAsync(AgentRuntimeEvent runtimeEvent, CancellationToken cancellationToken = default);
}

/** 供当前正在执行回合的 Agent 与安全边界读取本回合消息的来源与信任等级。 */
public static class AgentTurnAttribution
{
    private static readonly AsyncLocal<MessageAttribution?> CurrentSlot = new();

    public static MessageAttribution? Current => CurrentSlot.Value;

    public static IDisposable Enter(MessageAttribution? attribution)
    {
        var previous = CurrentSlot.Value;
        CurrentSlot.Value = attribution;
        return new Scope(() => CurrentSlot.Value = previous);
    }

    private sealed class Scope(Action restore) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
                restore();
        }
    }
}
