using Dsh.Agent;
using Dsh.Core;
using Dsh.Runtime;
using Dsh.Runtime.Events;

namespace Dsh.Channels;

internal sealed class AutomationTriggerEngine : IDisposable
{
    private const int MaxSeenEvents = 4096;

    private readonly Context _ctx;
    private readonly ExternalChannelRouter _router;
    private readonly Lock _gate = new();
    private readonly HashSet<AgentId> _subscribedAgents = [];
    private readonly List<IDisposable> _busSubscriptions = [];
    private readonly Queue<string> _seenOrder = new();
    private readonly HashSet<string> _seenEventIds = new(StringComparer.Ordinal);
    private readonly Func<bool>? _agentCreatedSubscription;
    private IAgentMessageBus? _subscribedBus;
    private bool _disposed;

    public AutomationTriggerEngine(Context ctx, ExternalChannelRouter router)
    {
        _ctx = ctx;
        _router = router;
        _agentCreatedSubscription = ctx.On<AgentCreatedNotification>(
            notification => EnsureSubscribed(notification.Agent.AgentId),
            new EventOptions { Global = true });

        if (ctx.Get<IAgentMessageBus>(IAgentMessageBus.ServiceName, false) is { } bus)
            AttachBus(bus);
    }

    public void EnsureSubscribed(AgentId agentId, IAgentMessageBus? resolvedBus = null)
    {
        if (_disposed)
            return;
        var bus = resolvedBus ?? _ctx.Get<IAgentMessageBus>(IAgentMessageBus.ServiceName, false);
        if (bus is null)
            return;

        AttachBus(bus);
        if (string.IsNullOrWhiteSpace(agentId.Value))
            return;

        bool shouldSubscribe;
        lock (_gate)
            shouldSubscribe = _subscribedAgents.Add(agentId);

        if (shouldSubscribe)
        {
            var subscription = new AgentEventSubscription(
                $"channels-agent-{agentId.Value}-{Guid.NewGuid():N}",
                agentId,
                null,
                [
                    AgentRuntimeEventKind.TurnCompleted,
                    AgentRuntimeEventKind.TurnFailed,
                    AgentRuntimeEventKind.ApprovalRequested,
                ],
                WakeSubscriber: false);
            var handle = bus.Subscribe(subscription, OnRuntimeEventAsync);
            lock (_gate)
                _busSubscriptions.Add(handle);
        }
    }

    public static bool MatchesSourceKind(string ruleSourceKind, string actualSourceKind, string eventName)
    {
        if (ruleSourceKind == "*")
            return true;
        if (string.Equals(ruleSourceKind, actualSourceKind, StringComparison.OrdinalIgnoreCase))
            return true;
        if (string.Equals(actualSourceKind, "github", StringComparison.OrdinalIgnoreCase)
            && string.Equals(ruleSourceKind, "github.ci", StringComparison.OrdinalIgnoreCase))
        {
            return eventName is "workflow_run" or "check_run" or "check_suite" or "status" or "ci"
                || eventName.StartsWith("ci.", StringComparison.OrdinalIgnoreCase);
        }
        if (string.Equals(actualSourceKind, "agent", StringComparison.OrdinalIgnoreCase)
            && ruleSourceKind is "agent.runtime" or "runtime")
            return true;
        return false;
    }

    public static bool MatchesEventPattern(string pattern, string eventName)
    {
        if (string.IsNullOrWhiteSpace(pattern))
            return false;
        if (pattern == "*")
            return true;
        if (WildcardMatch(pattern, eventName))
            return true;

        var normalizedEvent = eventName.Replace(".", "_", StringComparison.Ordinal);
        if (WildcardMatch(pattern, normalizedEvent))
            return true;

        var pascal = eventName switch
        {
            "turn.completed" => nameof(AgentRuntimeEventKind.TurnCompleted),
            "turn.failed" => nameof(AgentRuntimeEventKind.TurnFailed),
            "approval.requested" => nameof(AgentRuntimeEventKind.ApprovalRequested),
            _ => null,
        };
        return pascal is not null && WildcardMatch(pattern, pascal);
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _agentCreatedSubscription?.Invoke();
        List<IDisposable> handles;
        lock (_gate)
        {
            handles = [.. _busSubscriptions];
            _busSubscriptions.Clear();
            _subscribedAgents.Clear();
            _subscribedBus = null;
        }
        foreach (var handle in handles)
            handle.Dispose();
    }

    private void AttachBus(IAgentMessageBus bus)
    {
        var subscribeWildcard = false;
        List<IDisposable>? stale = null;
        lock (_gate)
        {
            if (!ReferenceEquals(_subscribedBus, bus))
            {
                stale = [.. _busSubscriptions];
                _busSubscriptions.Clear();
                _subscribedAgents.Clear();
                _subscribedBus = bus;
                subscribeWildcard = true;
            }
        }

        if (stale is not null)
        {
            foreach (var handle in stale)
                handle.Dispose();
        }

        if (!subscribeWildcard)
            return;

        var wildcard = new AgentEventSubscription(
            $"channels-wildcard-{Guid.NewGuid():N}",
            default,
            null,
            [
                AgentRuntimeEventKind.TurnCompleted,
                AgentRuntimeEventKind.TurnFailed,
                AgentRuntimeEventKind.ApprovalRequested,
            ],
            WakeSubscriber: false);
        var wildcardHandle = bus.Subscribe(wildcard, OnRuntimeEventAsync);
        lock (_gate)
            _busSubscriptions.Add(wildcardHandle);

        if (_ctx.Get<AgentRegistry>(AgentRegistry.ServiceName, false) is { } registry)
        {
            foreach (var agent in registry.List())
                EnsureSubscribed(agent.AgentId, bus);
        }
        if (_ctx.Get<IAgentDirectoryStore>(IAgentDirectoryStore.ServiceName, false) is { } directory)
        {
            foreach (var profile in directory.List())
                EnsureSubscribed(profile.AgentId, bus);
        }
    }

    private async ValueTask OnRuntimeEventAsync(AgentRuntimeEvent runtimeEvent)
    {
        if (_disposed)
            return;
        if (!MarkSeen(runtimeEvent.EventId))
            return;

        if (runtimeEvent.Kind == AgentRuntimeEventKind.TurnCompleted)
        {
            try
            {
                await _router.TryForwardAgentReplyAsync(runtimeEvent, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception error)
            {
                _ctx.LoggerFor(IExternalChannelRouter.ServiceName).Warn($"Outbound channel reply failed: {error.Message}");
            }
        }

        var eventName = runtimeEvent.Kind switch
        {
            AgentRuntimeEventKind.TurnCompleted => "turn.completed",
            AgentRuntimeEventKind.TurnFailed => "turn.failed",
            AgentRuntimeEventKind.ApprovalRequested => "approval.requested",
            _ => null,
        };
        if (eventName is null)
            return;

        if (!string.IsNullOrWhiteSpace(runtimeEvent.CorrelationId)
            && runtimeEvent.CorrelationId.StartsWith("trigger:", StringComparison.Ordinal))
        {
            return;
        }

        var summary = string.IsNullOrWhiteSpace(runtimeEvent.Detail)
            ? $"{runtimeEvent.AgentId.Value}: {runtimeEvent.Summary}"
            : $"{runtimeEvent.AgentId.Value}: {runtimeEvent.Summary} — {runtimeEvent.Detail}";
        await _router.FireTriggerAsync("agent", eventName, summary).ConfigureAwait(false);
    }

    private bool MarkSeen(string eventId)
    {
        if (string.IsNullOrWhiteSpace(eventId))
            return true;
        lock (_gate)
        {
            if (!_seenEventIds.Add(eventId))
                return false;
            _seenOrder.Enqueue(eventId);
            while (_seenOrder.Count > MaxSeenEvents)
                _seenEventIds.Remove(_seenOrder.Dequeue());
            return true;
        }
    }

    private static bool WildcardMatch(string pattern, string input)
    {
        var p = 0;
        var s = 0;
        var starIndex = -1;
        var matchIndex = 0;

        while (s < input.Length)
        {
            if (p < pattern.Length && char.ToLowerInvariant(pattern[p]) == char.ToLowerInvariant(input[s]))
            {
                p++;
                s++;
            }
            else if (p < pattern.Length && pattern[p] == '*')
            {
                starIndex = p;
                matchIndex = s;
                p++;
            }
            else if (starIndex != -1)
            {
                p = starIndex + 1;
                matchIndex++;
                s = matchIndex;
            }
            else
            {
                return false;
            }
        }

        while (p < pattern.Length && pattern[p] == '*')
            p++;
        return p == pattern.Length;
    }
}
