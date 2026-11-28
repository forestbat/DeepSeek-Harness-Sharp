using System.Collections.Concurrent;
using Dsh.Core;
using Dsh.Llm;
using Dsh.Runtime;

namespace Dsh.Agent;

/** Routes agent-to-agent envelopes (live steer/followup, wake-on-demand, offline mailbox) and runtime event subscriptions. */
public sealed class AgentMessageBus(Context ctx) : Service(ctx, IAgentMessageBus.ServiceName), IAgentMessageBus
{
    public const string MessagePluginName = "agent-bus";

    private sealed record SubscriptionEntry(
        AgentEventSubscription Subscription,
        Func<AgentRuntimeEvent, ValueTask>? Handler);

    private readonly Lock _subscriptionGate = new();
    private readonly Lock _fallbackMailboxGate = new();
    private readonly List<SubscriptionEntry> _subscriptions = [];
    private readonly Dictionary<AgentId, List<AgentEnvelope>> _fallbackMailbox = [];
    private readonly ConcurrentDictionary<(AgentId Agent, string EnvelopeId), byte> _seenEnvelopeIds = [];
    private readonly ConcurrentDictionary<(AgentId Agent, string Key), byte> _seenIdempotencyKeys = [];
    private readonly ConcurrentDictionary<MessageId, MessageAttribution> _attributions = [];

    internal MessageAttribution? PeekAttribution(MessageId messageId)
        => _attributions.TryGetValue(messageId, out var attribution) ? attribution : null;

    internal MessageAttribution? TakeAttribution(MessageId messageId)
        => _attributions.TryRemove(messageId, out var attribution) ? attribution : null;

    public async Task<AgentDeliveryResult> SendAsync(AgentEnvelope envelope, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        cancellationToken.ThrowIfCancellationRequested();

        if (envelope.TrustLevel == MessageTrustLevel.ExternalAgent && envelope.Intent == AgentMessageIntent.Control)
        {
            return new AgentDeliveryResult(
                envelope.EnvelopeId,
                AgentDeliveryStatus.Rejected,
                envelope.TargetAgentId,
                envelope.TargetSessionId,
                "ExternalAgent principals are not permitted to send Control messages.");
        }

        if (IsDuplicate(envelope))
        {
            return new AgentDeliveryResult(
                envelope.EnvelopeId,
                AgentDeliveryStatus.Deduplicated,
                envelope.TargetAgentId,
                envelope.TargetSessionId,
                "Duplicate envelope or idempotency key.");
        }

        var liveTarget = ResolveLiveTarget(envelope.TargetAgentId, envelope.TargetSessionId);
        if (liveTarget is not null)
        {
            MarkSeen(envelope);
            DeliverToLiveAgent(liveTarget, envelope, AgentDeliveryStatus.DeliveredLive);
            await PublishDeliveryEventAsync(envelope, liveTarget.Id, AgentDeliveryStatus.DeliveredLive, cancellationToken)
                .ConfigureAwait(false);
            return new AgentDeliveryResult(
                envelope.EnvelopeId,
                AgentDeliveryStatus.DeliveredLive,
                envelope.TargetAgentId,
                liveTarget.Id);
        }

        var manager = Ctx.Get<IPersistentAgentManager>(IPersistentAgentManager.ServiceName, false);
        if (envelope.WakeIfSuspended && manager?.Get(envelope.TargetAgentId) is { State: not AgentLifecycleState.Disposed })
        {
            MarkSeen(envelope);
            var woken = await manager.WakeAsync(envelope.TargetAgentId, cancellationToken).ConfigureAwait(false);
            DeliverToLiveAgent(woken, envelope, AgentDeliveryStatus.WokeAndDelivered);
            await PublishDeliveryEventAsync(envelope, woken.Id, AgentDeliveryStatus.WokeAndDelivered, cancellationToken)
                .ConfigureAwait(false);
            return new AgentDeliveryResult(
                envelope.EnvelopeId,
                AgentDeliveryStatus.WokeAndDelivered,
                envelope.TargetAgentId,
                woken.Id);
        }

        if (!EnqueueOffline(envelope))
        {
            MarkSeen(envelope);
            return new AgentDeliveryResult(
                envelope.EnvelopeId,
                AgentDeliveryStatus.Deduplicated,
                envelope.TargetAgentId,
                envelope.TargetSessionId,
                "Duplicate offline envelope or idempotency key.");
        }
        MarkSeen(envelope);
        return new AgentDeliveryResult(
            envelope.EnvelopeId,
            AgentDeliveryStatus.QueuedOffline,
            envelope.TargetAgentId,
            envelope.TargetSessionId,
            "Target agent is offline or suspended.");
    }

    public async Task<int> FlushOfflineAsync(AgentId agentId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var queued = DrainOffline(agentId);
        if (queued.Count == 0)
            return 0;

        var target = ResolveLiveTarget(agentId, null);
        if (target is null)
        {
            foreach (var envelope in queued)
                EnqueueOffline(envelope);
            return 0;
        }

        var delivered = 0;
        foreach (var envelope in queued)
        {
            MarkSeen(envelope);
            DeliverToLiveAgent(target, envelope, AgentDeliveryStatus.WokeAndDelivered);
            await PublishDeliveryEventAsync(envelope, target.Id, AgentDeliveryStatus.WokeAndDelivered, cancellationToken)
                .ConfigureAwait(false);
            delivered++;
        }
        return delivered;
    }

    public IReadOnlyList<AgentEnvelope> PeekOffline(AgentId agentId)
    {
        if (Ctx.Get<IOfflineMailboxStore>(IOfflineMailboxStore.ServiceName, false) is { } store)
            return store.Peek(agentId);
        lock (_fallbackMailboxGate)
            return _fallbackMailbox.TryGetValue(agentId, out var list) ? [.. list] : [];
    }

    public IDisposable Subscribe(AgentEventSubscription subscription, Func<AgentRuntimeEvent, ValueTask>? handler = null)
    {
        ArgumentNullException.ThrowIfNull(subscription);
        var entry = new SubscriptionEntry(subscription, handler);
        lock (_subscriptionGate)
            _subscriptions.Add(entry);
        return new SubscriptionHandle(() =>
        {
            lock (_subscriptionGate)
                _subscriptions.Remove(entry);
        });
    }

    public IReadOnlyList<AgentEventSubscription> ListSubscriptions(AgentId? publisher = null)
    {
        lock (_subscriptionGate)
        {
            return publisher is { } publisherId
                ? [.. _subscriptions.Where(entry => entry.Subscription.PublisherAgentId == publisherId).Select(entry => entry.Subscription)]
                : [.. _subscriptions.Select(entry => entry.Subscription)];
        }
    }

    public async Task PublishEventAsync(AgentRuntimeEvent runtimeEvent, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(runtimeEvent);
        cancellationToken.ThrowIfCancellationRequested();

        List<SubscriptionEntry> matched;
        lock (_subscriptionGate)
        {
            matched =
            [
                .. _subscriptions.Where(entry =>
                    entry.Subscription.PublisherAgentId == runtimeEvent.AgentId
                    && (entry.Subscription.Kinds is null
                        || entry.Subscription.Kinds.Count == 0
                        || entry.Subscription.Kinds.Contains(runtimeEvent.Kind))),
            ];
        }

        foreach (var entry in matched)
        {
            if (entry.Handler is not null)
            {
                try
                {
                    await entry.Handler(runtimeEvent).ConfigureAwait(false);
                }
                catch (Exception error)
                {
                    Ctx.LoggerFor("agentBus").Warn(
                        $"event handler failed for subscription \"{entry.Subscription.SubscriptionId}\": {error.Message}");
                }
            }

            if (entry.Subscription.WakeSubscriber && entry.Subscription.SubscriberAgentId is { } subscriberId)
            {
                if (subscriberId == runtimeEvent.AgentId
                    && runtimeEvent.Kind is AgentRuntimeEventKind.MessageDelivered or AgentRuntimeEventKind.AgentWoken)
                {
                    continue;
                }
                var detail = runtimeEvent.Detail is { Length: > 0 } ? $"\n{runtimeEvent.Detail}" : string.Empty;
                var notifyEnvelope = new AgentEnvelope(
                    $"env-{Guid.NewGuid():N}",
                    subscriberId,
                    null,
                    new MessagePrincipal(PrincipalKind.System, "agent-bus", "AgentMessageBus"),
                    MessageTrustLevel.TrustedPeerAgent,
                    AgentMessageIntent.Notify,
                    $"[AGENT EVENT {runtimeEvent.Kind}] publisher={runtimeEvent.AgentId} session={runtimeEvent.SessionId}: {runtimeEvent.Summary}{detail}",
                    runtimeEvent.CorrelationId ?? runtimeEvent.EventId,
                    $"sub:{entry.Subscription.SubscriptionId}:{runtimeEvent.EventId}",
                    runtimeEvent.Timestamp,
                    WakeIfSuspended: true);
                await SendAsync(notifyEnvelope, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private IAgent? ResolveLiveTarget(AgentId agentId, SessionId? targetSessionId)
    {
        var agents = Ctx.Get<AgentRegistry>(AgentRegistry.ServiceName, false);
        if (agents is null)
            return null;
        if (targetSessionId is { } explicitSessionId)
            return agents.Get(explicitSessionId);
        var manager = Ctx.Get<IPersistentAgentManager>(IPersistentAgentManager.ServiceName, false);
        if (manager?.Get(agentId)?.ActiveSessionId is { } activeSessionId
            && agents.Get(activeSessionId) is { } activeAgent)
            return activeAgent;
        return agents.GetByAgent(agentId);
    }

    private void DeliverToLiveAgent(IAgent target, AgentEnvelope envelope, AgentDeliveryStatus status)
    {
        target.Session.Append(new AgentEnvelopePayload(envelope, status));
        var userMessage = CreateEnvelopeUserMessage(envelope);
        var attribution = new MessageAttribution(
            envelope.Sender,
            envelope.TrustLevel,
            envelope.Intent,
            envelope.EnvelopeId,
            envelope.CorrelationId);
        _attributions[userMessage.Id] = attribution;
        if (target is AgentLoopAgent loopAgent)
            loopAgent.SetMessageAttribution(userMessage.Id, attribution);

        if (envelope.Intent == AgentMessageIntent.Steer)
            target.Steer(userMessage);
        else if (envelope.WakeIfSuspended)
            target.Followup(userMessage);
        else
            target.Send(userMessage, InboxTargets.NextTurn, wakeup: false);
    }

    private static UserMessage CreateEnvelopeUserMessage(AgentEnvelope envelope)
    {
        var senderLabel = envelope.Sender.DisplayName ?? envelope.Sender.Id;
        var text = $"[AGENT MESSAGE from {senderLabel} ({envelope.Sender.Kind}, trust={envelope.TrustLevel}, intent={envelope.Intent})]\n{envelope.Body}";
        return MessageFactory.CreateUserText(
            text,
            new PluginMessageSource(MessagePluginName, envelope.Intent.ToString().ToLowerInvariant()));
    }

    private Task PublishDeliveryEventAsync(
        AgentEnvelope envelope,
        SessionId sessionId,
        AgentDeliveryStatus status,
        CancellationToken cancellationToken)
        => PublishEventAsync(
            new AgentRuntimeEvent(
                $"evt-{Guid.NewGuid():N}",
                envelope.TargetAgentId,
                sessionId,
                AgentRuntimeEventKind.MessageDelivered,
                $"Envelope {envelope.EnvelopeId} ({envelope.Intent}) -> {status}",
                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                envelope.CorrelationId),
            cancellationToken);

    private bool IsDuplicate(AgentEnvelope envelope)
    {
        if (_seenEnvelopeIds.ContainsKey((envelope.TargetAgentId, envelope.EnvelopeId)))
            return true;
        if (envelope.IdempotencyKey is { Length: > 0 } key
            && _seenIdempotencyKeys.ContainsKey((envelope.TargetAgentId, key)))
            return true;

        foreach (var existing in PeekOffline(envelope.TargetAgentId))
        {
            if (string.Equals(existing.EnvelopeId, envelope.EnvelopeId, StringComparison.Ordinal))
                return true;
            if (envelope.IdempotencyKey is { Length: > 0 } offlineKey
                && string.Equals(existing.IdempotencyKey, offlineKey, StringComparison.Ordinal))
                return true;
        }
        return false;
    }

    private void MarkSeen(AgentEnvelope envelope)
    {
        _seenEnvelopeIds[(envelope.TargetAgentId, envelope.EnvelopeId)] = 0;
        if (envelope.IdempotencyKey is { Length: > 0 } key)
            _seenIdempotencyKeys[(envelope.TargetAgentId, key)] = 0;
    }

    private bool EnqueueOffline(AgentEnvelope envelope)
    {
        if (Ctx.Get<IOfflineMailboxStore>(IOfflineMailboxStore.ServiceName, false) is { } store)
            return store.Enqueue(envelope);
        lock (_fallbackMailboxGate)
        {
            if (!_fallbackMailbox.TryGetValue(envelope.TargetAgentId, out var queue))
            {
                queue = [];
                _fallbackMailbox[envelope.TargetAgentId] = queue;
            }
            if (queue.Any(item =>
                string.Equals(item.EnvelopeId, envelope.EnvelopeId, StringComparison.Ordinal)
                || (envelope.IdempotencyKey is { Length: > 0 } key && string.Equals(item.IdempotencyKey, key, StringComparison.Ordinal))))
            {
                return false;
            }
            queue.Add(envelope);
            return true;
        }
    }

    private IReadOnlyList<AgentEnvelope> DrainOffline(AgentId agentId)
    {
        if (Ctx.Get<IOfflineMailboxStore>(IOfflineMailboxStore.ServiceName, false) is { } store)
            return store.Drain(agentId);
        lock (_fallbackMailboxGate)
        {
            if (!_fallbackMailbox.TryGetValue(agentId, out var queue) || queue.Count == 0)
                return [];
            List<AgentEnvelope> drained = [.. queue];
            queue.Clear();
            return drained;
        }
    }

    private sealed class SubscriptionHandle(Action dispose) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
                dispose();
        }
    }
}
