using Dsh.Agent;
using Dsh.Core;
using Dsh.Llm;
using Dsh.Runtime;

namespace Dsh.Channels;

public sealed class ExternalChannelRouter : Service, IExternalChannelRouter, IDisposable
{
    private const string BindingsFileName = "bindings.json";
    private const string TriggersFileName = "triggers.json";
    private const string DedupFileName = "dedup.json";
    private const int MaxDedupEntries = 10_000;
    private const string InlineSecretPrefix = "inline:";

    internal sealed record PendingReplyRoute(
        string BindingId,
        string? ThreadId,
        string? ExternalMessageId);

    private readonly TimeProvider _clock;
    private readonly string? _channelsDirectory;
    private readonly Lock _stateLock = new();
    private readonly SemaphoreSlim _persistLock = new(1, 1);
    private readonly Dictionary<string, IExternalChannelAdapter> _adapters = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ChannelBinding> _bindings = new(StringComparer.Ordinal);
    private readonly Dictionary<string, AutomationTriggerRule> _triggers = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _secrets = new(StringComparer.Ordinal);
    private readonly Queue<string> _dedupOrder = new();
    private readonly HashSet<string> _dedupSet = new(StringComparer.Ordinal);
    private readonly Dictionary<string, PendingReplyRoute> _pendingReplies = new(StringComparer.Ordinal);
    private readonly HashSet<string> _forwardedCorrelations = new(StringComparer.Ordinal);
    private readonly AutomationTriggerEngine _triggerEngine;
    private bool _disposed;

    public ExternalChannelRouter(Context ctx, TimeProvider? clock = null, string? channelsDirectory = null)
        : base(ctx, IExternalChannelRouter.ServiceName)
    {
        _clock = clock ?? TimeProvider.System;
        _channelsDirectory = ChannelStorage.ResolveChannelsDirectory(ctx, channelsDirectory);
        _triggerEngine = new AutomationTriggerEngine(ctx, this);
        LoadFromDisk();
    }

    public TimeProvider Clock => _clock;

    public IDisposable RegisterAdapter(IExternalChannelAdapter adapter)
    {
        ArgumentNullException.ThrowIfNull(adapter);
        ArgumentException.ThrowIfNullOrWhiteSpace(adapter.AdapterKind);
        lock (_stateLock)
            _adapters[adapter.AdapterKind] = adapter;
        return new AdapterRegistration(() =>
        {
            lock (_stateLock)
            {
                if (_adapters.TryGetValue(adapter.AdapterKind, out var current) && ReferenceEquals(current, adapter))
                    _adapters.Remove(adapter.AdapterKind);
            }
        });
    }

    public void SetSecret(string secretRef, string secretValue)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(secretRef);
        ArgumentNullException.ThrowIfNull(secretValue);
        lock (_stateLock)
            _secrets[secretRef] = secretValue;
    }

    public string? ResolveSecret(ChannelBinding binding)
    {
        if (!string.IsNullOrWhiteSpace(binding.SecretRef))
        {
            lock (_stateLock)
            {
                if (_secrets.TryGetValue(binding.SecretRef, out var stored))
                    return stored;
            }
            if (binding.SecretRef.StartsWith(InlineSecretPrefix, StringComparison.Ordinal))
                return binding.SecretRef[InlineSecretPrefix.Length..];
        }

        if (binding.Metadata is not null && binding.Metadata.TryGetValue("secret", out var inline) && !string.IsNullOrEmpty(inline))
            return inline;

        return null;
    }

    public string? ResolveBotToken(ChannelBinding binding, string adapterKind)
    {
        if (binding.Metadata is not null && binding.Metadata.TryGetValue("botToken", out var metaToken) && !string.IsNullOrEmpty(metaToken))
            return metaToken;

        lock (_stateLock)
        {
            if (!string.IsNullOrWhiteSpace(binding.SecretRef)
                && _secrets.TryGetValue($"{binding.SecretRef}:botToken", out var scoped))
                return scoped;
            if (_secrets.TryGetValue($"{binding.BindingId}:botToken", out var bindingToken))
                return bindingToken;
            if (_secrets.TryGetValue($"{adapterKind}:botToken", out var adapterToken))
                return adapterToken;
        }
        return null;
    }

    public ChannelBinding Bind(ChannelBinding binding)
    {
        ArgumentNullException.ThrowIfNull(binding);
        ArgumentException.ThrowIfNullOrWhiteSpace(binding.BindingId);
        ArgumentException.ThrowIfNullOrWhiteSpace(binding.AdapterKind);
        ArgumentException.ThrowIfNullOrWhiteSpace(binding.ChannelKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(binding.TargetAgentId.Value);

        var normalized = binding.CreatedAt > 0
            ? binding
            : binding with { CreatedAt = _clock.GetUtcNow().ToUnixTimeMilliseconds() };

        ChannelBindingsDocument snapshot;
        lock (_stateLock)
        {
            _bindings[normalized.BindingId] = normalized;
            snapshot = new ChannelBindingsDocument([.. _bindings.Values]);
        }
        PersistBindingsSync(snapshot);
        _triggerEngine.EnsureSubscribed(normalized.TargetAgentId);
        return normalized;
    }

    public bool Unbind(string bindingId)
    {
        if (string.IsNullOrWhiteSpace(bindingId))
            return false;
        ChannelBindingsDocument? snapshot = null;
        bool removed;
        lock (_stateLock)
        {
            removed = _bindings.Remove(bindingId);
            if (removed)
                snapshot = new ChannelBindingsDocument([.. _bindings.Values]);
        }
        if (snapshot is not null)
            PersistBindingsSync(snapshot);
        return removed;
    }

    public IReadOnlyList<ChannelBinding> ListBindings()
    {
        lock (_stateLock)
            return _bindings.Values.OrderBy(b => b.CreatedAt).ThenBy(b => b.BindingId, StringComparer.Ordinal).ToList();
    }

    public IReadOnlyList<ChannelBinding> FindBindings(string adapterKind, string? channelKey = null, string? bindingId = null)
    {
        lock (_stateLock)
        {
            if (!string.IsNullOrWhiteSpace(bindingId))
            {
                return _bindings.TryGetValue(bindingId, out var exact) && exact.Enabled
                    ? [exact]
                    : [];
            }

            return _bindings.Values
                .Where(b => b.Enabled
                    && AdapterMatches(b.AdapterKind, adapterKind)
                    && (string.IsNullOrWhiteSpace(channelKey)
                        || b.ChannelKey == "*"
                        || string.Equals(b.ChannelKey, channelKey, StringComparison.OrdinalIgnoreCase)))
                .OrderByDescending(b => string.Equals(b.ChannelKey, channelKey, StringComparison.OrdinalIgnoreCase))
                .ThenBy(b => b.CreatedAt)
                .ToList();
        }
    }

    public async Task<AgentDeliveryResult> DispatchInboundAsync(ChannelInboundMessage message, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        var binding = ResolveBindingForInbound(message);
        var envelopeId = $"ch-{Guid.NewGuid():N}";
        if (binding is null || !binding.Enabled)
        {
            return new AgentDeliveryResult(
                envelopeId,
                AgentDeliveryStatus.Rejected,
                binding?.TargetAgentId ?? default,
                Reason: binding is null ? "Channel binding not found." : "Channel binding is disabled.");
        }

        var dedupKey = string.IsNullOrWhiteSpace(message.ExternalMessageId)
            ? null
            : $"{binding.BindingId}:{message.ExternalMessageId}";
        if (dedupKey is not null)
        {
            var duplicate = false;
            ChannelDedupDocument? dedupSnapshot = null;
            lock (_stateLock)
            {
                if (_dedupSet.Contains(dedupKey))
                    duplicate = true;
                else
                {
                    RecordDedupLocked(dedupKey);
                    dedupSnapshot = new ChannelDedupDocument([.. _dedupOrder]);
                }
            }
            if (duplicate)
            {
                return new AgentDeliveryResult(
                    envelopeId,
                    AgentDeliveryStatus.Deduplicated,
                    binding.TargetAgentId,
                    Reason: "Duplicate external message ID.");
            }
            if (dedupSnapshot is not null)
                await PersistDedupAsync(dedupSnapshot, cancellationToken).ConfigureAwait(false);
        }

        if (binding.TargetRoomId is { } roomId)
            await TryBridgeToRoomAsync(roomId, binding, message, cancellationToken).ConfigureAwait(false);

        var bus = Ctx.Get<IAgentMessageBus>(IAgentMessageBus.ServiceName, false);
        if (bus is null)
        {
            return new AgentDeliveryResult(
                envelopeId,
                AgentDeliveryStatus.Rejected,
                binding.TargetAgentId,
                Reason: "IAgentMessageBus is not available.");
        }

        _triggerEngine.EnsureSubscribed(binding.TargetAgentId, bus);

        var correlationId = !string.IsNullOrWhiteSpace(message.ThreadId)
            ? $"channel:{binding.BindingId}:{message.ThreadId}"
            : $"channel:{binding.BindingId}:{message.ExternalMessageId}";
        var trustLevel = ResolveTrustLevel(binding, message);
        var sender = new MessagePrincipal(
            PrincipalKind.ExternalChannel,
            message.SenderId,
            string.IsNullOrWhiteSpace(message.SenderName) ? message.SenderId : message.SenderName,
            ChannelId: binding.BindingId);

        var metadata = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["bindingId"] = binding.BindingId,
            ["adapterKind"] = binding.AdapterKind,
            ["channelKey"] = message.ChannelKey,
            ["externalMessageId"] = message.ExternalMessageId,
        };
        if (!string.IsNullOrWhiteSpace(message.ThreadId))
            metadata["threadId"] = message.ThreadId;
        if (!string.IsNullOrWhiteSpace(message.EventType))
            metadata["eventType"] = message.EventType;

        if (!string.IsNullOrWhiteSpace(binding.OutboundUrl) && !IsAutoReplyDisabled(binding))
        {
            lock (_stateLock)
                _pendingReplies[correlationId] = new PendingReplyRoute(binding.BindingId, message.ThreadId, message.ExternalMessageId);
        }

        var envelope = new AgentEnvelope(
            envelopeId,
            binding.TargetAgentId,
            null,
            sender,
            trustLevel,
            AgentMessageIntent.Task,
            message.Body,
            correlationId,
            dedupKey ?? envelopeId,
            message.Timestamp > 0 ? message.Timestamp : _clock.GetUtcNow().ToUnixTimeMilliseconds(),
            WakeIfSuspended: true,
            metadata);

        var result = await bus.SendAsync(envelope, cancellationToken).ConfigureAwait(false);
        if (result.Status == AgentDeliveryStatus.Rejected)
        {
            lock (_stateLock)
                _pendingReplies.Remove(correlationId);
        }
        return result;
    }

    public async Task SendOutboundAsync(ChannelOutboundMessage message, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        ChannelBinding? binding;
        IExternalChannelAdapter? adapter;
        lock (_stateLock)
        {
            _bindings.TryGetValue(message.BindingId, out binding);
            if (binding is not null)
            {
                if (!_adapters.TryGetValue(binding.AdapterKind, out adapter))
                {
                    var alias = string.Equals(binding.AdapterKind, "webhook", StringComparison.OrdinalIgnoreCase)
                        ? "github"
                        : string.Equals(binding.AdapterKind, "github", StringComparison.OrdinalIgnoreCase)
                            ? "webhook"
                            : null;
                    if (alias is not null)
                        _adapters.TryGetValue(alias, out adapter);
                }
            }
            else
            {
                adapter = null;
            }
        }
        if (binding is null)
            throw new InvalidOperationException($"Channel binding '{message.BindingId}' not found.");
        if (adapter is null)
            throw new InvalidOperationException($"No external channel adapter registered for '{binding.AdapterKind}'.");

        await adapter.SendAsync(binding, message, cancellationToken).ConfigureAwait(false);
    }

    public AutomationTriggerRule AddTrigger(AutomationTriggerRule rule)
    {
        ArgumentNullException.ThrowIfNull(rule);
        ArgumentException.ThrowIfNullOrWhiteSpace(rule.RuleId);
        ArgumentException.ThrowIfNullOrWhiteSpace(rule.SourceKind);
        ArgumentException.ThrowIfNullOrWhiteSpace(rule.EventPattern);
        ArgumentException.ThrowIfNullOrWhiteSpace(rule.TargetAgentId.Value);

        var normalized = rule.CreatedAt > 0
            ? rule
            : rule with { CreatedAt = _clock.GetUtcNow().ToUnixTimeMilliseconds() };

        AutomationTriggersDocument snapshot;
        lock (_stateLock)
        {
            _triggers[normalized.RuleId] = normalized;
            snapshot = new AutomationTriggersDocument([.. _triggers.Values]);
        }
        PersistTriggersSync(snapshot);
        _triggerEngine.EnsureSubscribed(normalized.TargetAgentId);
        return normalized;
    }

    public bool RemoveTrigger(string ruleId)
    {
        if (string.IsNullOrWhiteSpace(ruleId))
            return false;
        AutomationTriggersDocument? snapshot = null;
        bool removed;
        lock (_stateLock)
        {
            removed = _triggers.Remove(ruleId);
            if (removed)
                snapshot = new AutomationTriggersDocument([.. _triggers.Values]);
        }
        if (snapshot is not null)
            PersistTriggersSync(snapshot);
        return removed;
    }

    public IReadOnlyList<AutomationTriggerRule> ListTriggers()
    {
        lock (_stateLock)
            return _triggers.Values.OrderBy(t => t.CreatedAt).ThenBy(t => t.RuleId, StringComparer.Ordinal).ToList();
    }

    public async Task<int> FireTriggerAsync(string sourceKind, string eventName, string payloadSummary, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceKind);
        ArgumentException.ThrowIfNullOrWhiteSpace(eventName);

        var matching = ListTriggers()
            .Where(rule => rule.Enabled
                && AutomationTriggerEngine.MatchesSourceKind(rule.SourceKind, sourceKind, eventName)
                && AutomationTriggerEngine.MatchesEventPattern(rule.EventPattern, eventName))
            .ToList();
        if (matching.Count == 0)
            return 0;

        var bus = Ctx.Get<IAgentMessageBus>(IAgentMessageBus.ServiceName, false);
        if (bus is null)
            return 0;

        var fired = 0;
        var now = _clock.GetUtcNow().ToUnixTimeMilliseconds();
        var trustLevel = IsExternalSource(sourceKind)
            ? MessageTrustLevel.ExternalAgent
            : MessageTrustLevel.DelegatedHuman;

        foreach (var rule in matching)
        {
            _triggerEngine.EnsureSubscribed(rule.TargetAgentId, bus);
            var body = FormatTriggerPrompt(rule.PromptTemplate, sourceKind, eventName, payloadSummary);
            var envelope = new AgentEnvelope(
                $"trg-{Guid.NewGuid():N}",
                rule.TargetAgentId,
                null,
                new MessagePrincipal(PrincipalKind.System, $"trigger:{rule.RuleId}", rule.Name),
                trustLevel,
                AgentMessageIntent.Task,
                body,
                $"trigger:{rule.RuleId}:{Guid.NewGuid():N}",
                null,
                now,
                WakeIfSuspended: rule.WakeAgent,
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["triggerRuleId"] = rule.RuleId,
                    ["sourceKind"] = sourceKind,
                    ["eventName"] = eventName,
                });

            var result = await bus.SendAsync(envelope, cancellationToken).ConfigureAwait(false);
            if (result.Status != AgentDeliveryStatus.Rejected)
                fired++;
        }

        return fired;
    }

    internal async Task TryForwardAgentReplyAsync(AgentRuntimeEvent runtimeEvent, CancellationToken cancellationToken)
    {
        if (runtimeEvent.Kind != AgentRuntimeEventKind.TurnCompleted || string.IsNullOrWhiteSpace(runtimeEvent.CorrelationId))
            return;

        PendingReplyRoute? route;
        lock (_stateLock)
        {
            if (!_pendingReplies.TryGetValue(runtimeEvent.CorrelationId, out route))
                return;
            if (!_forwardedCorrelations.Add(runtimeEvent.CorrelationId))
                return;
            _pendingReplies.Remove(runtimeEvent.CorrelationId);
        }

        var replyText = ExtractReplyText(runtimeEvent);
        if (string.IsNullOrWhiteSpace(replyText))
            return;

        await SendOutboundAsync(
            new ChannelOutboundMessage(route.BindingId, replyText, route.ThreadId, route.ExternalMessageId),
            cancellationToken).ConfigureAwait(false);
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _triggerEngine.Dispose();
        _persistLock.Dispose();
    }

    private string? ExtractReplyText(AgentRuntimeEvent runtimeEvent)
    {
        if (!string.IsNullOrWhiteSpace(runtimeEvent.Detail))
            return runtimeEvent.Detail;

        var registry = Ctx.Get<AgentRegistry>(AgentRegistry.ServiceName, false);
        if (registry is not null)
        {
            var agent = runtimeEvent.SessionId is { } sid
                ? registry.Get(sid)
                : registry.List().FirstOrDefault(candidate => candidate.AgentId == runtimeEvent.AgentId);
            if (agent is not null)
            {
                var assistant = agent.Session.SnapshotEvents()
                    .Select(ev => ev.Data)
                    .OfType<AssistantMessagePayload>()
                    .LastOrDefault();
                if (assistant is not null)
                {
                    var text = MessageText.Flatten(assistant.Message.Content);
                    if (!string.IsNullOrWhiteSpace(text))
                        return text;
                }
            }
        }

        return string.IsNullOrWhiteSpace(runtimeEvent.Summary) ? null : runtimeEvent.Summary;
    }

    private ChannelBinding? ResolveBindingForInbound(ChannelInboundMessage message)
    {
        lock (_stateLock)
        {
            if (!string.IsNullOrWhiteSpace(message.BindingId) && _bindings.TryGetValue(message.BindingId, out var direct))
                return direct;
            return _bindings.Values.FirstOrDefault(b =>
                b.Enabled
                && AdapterMatches(b.AdapterKind, message.AdapterKind)
                && (b.ChannelKey == "*" || string.Equals(b.ChannelKey, message.ChannelKey, StringComparison.OrdinalIgnoreCase)));
        }
    }

    private async Task TryBridgeToRoomAsync(
        RoomId roomId,
        ChannelBinding binding,
        ChannelInboundMessage message,
        CancellationToken cancellationToken)
    {
        var rooms = Ctx.Get<ICollaborationRoomService>(ICollaborationRoomService.ServiceName, false);
        if (rooms?.GetRoom(roomId) is not { } room)
            return;

        var sender = new MessagePrincipal(
            PrincipalKind.ExternalChannel,
            message.SenderId,
            string.IsNullOrWhiteSpace(message.SenderName) ? message.SenderId : message.SenderName,
            ChannelId: binding.BindingId);

        MessagePrincipal? authorizedSender = null;
        if (rooms.CanPerform(roomId, sender.Id, RoomActions.Post))
            authorizedSender = sender;
        else if (binding.Metadata?.TryGetValue("roomActorPrincipalId", out var actorId) == true
            && !string.IsNullOrWhiteSpace(actorId)
            && rooms.CanPerform(roomId, actorId, RoomActions.Post))
            authorizedSender = sender with { Id = actorId };
        else if (binding.Metadata?.GetValueOrDefault("bridgeToRoom") == "true"
            && rooms.CanPerform(roomId, room.OwnerPrincipalId, RoomActions.Post))
            authorizedSender = new MessagePrincipal(PrincipalKind.ExternalChannel, room.OwnerPrincipalId, sender.DisplayName, ChannelId: binding.BindingId);

        if (authorizedSender is null)
            return;

        try
        {
            var notice = $"[{binding.AdapterKind}:{message.ChannelKey}] {sender.DisplayName}: {message.Body}";
            await rooms.PostMessageAsync(roomId, authorizedSender, notice, cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (Exception error)
        {
            Ctx.LoggerFor(IExternalChannelRouter.ServiceName).Warn($"Failed to bridge inbound message to room '{roomId.Value}': {error.Message}");
        }
    }

    private static MessageTrustLevel ResolveTrustLevel(ChannelBinding binding, ChannelInboundMessage message)
    {
        if (binding.Metadata is null)
            return MessageTrustLevel.ExternalAgent;

        var signatureVerified = message.Headers is not null
            && message.Headers.TryGetValue("x-dsh-signature-verified", out var verified)
            && string.Equals(verified, "true", StringComparison.OrdinalIgnoreCase);
        if (!signatureVerified)
            return MessageTrustLevel.ExternalAgent;

        var configuredTrust = binding.Metadata.GetValueOrDefault("trustLevel");
        var trustedOwnerId = binding.Metadata.GetValueOrDefault("trustedOwnerPrincipalId");
        if (string.Equals(configuredTrust, nameof(MessageTrustLevel.OwnerHuman), StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrWhiteSpace(trustedOwnerId)
            && string.Equals(trustedOwnerId, message.SenderId, StringComparison.Ordinal))
        {
            return MessageTrustLevel.OwnerHuman;
        }
        return MessageTrustLevel.ExternalAgent;
    }

    private static bool IsAutoReplyDisabled(ChannelBinding binding)
        => binding.Metadata is not null
            && string.Equals(binding.Metadata.GetValueOrDefault("autoReply"), "false", StringComparison.OrdinalIgnoreCase);

    private static bool AdapterMatches(string bindingKind, string messageKind)
    {
        if (string.Equals(bindingKind, messageKind, StringComparison.OrdinalIgnoreCase))
            return true;
        return (string.Equals(bindingKind, "webhook", StringComparison.OrdinalIgnoreCase)
            && string.Equals(messageKind, "github", StringComparison.OrdinalIgnoreCase))
            || (string.Equals(bindingKind, "github", StringComparison.OrdinalIgnoreCase)
            && string.Equals(messageKind, "webhook", StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsExternalSource(string sourceKind)
        => sourceKind.StartsWith("github", StringComparison.OrdinalIgnoreCase)
            || sourceKind.StartsWith("webhook", StringComparison.OrdinalIgnoreCase)
            || sourceKind.StartsWith("slack", StringComparison.OrdinalIgnoreCase)
            || sourceKind.StartsWith("discord", StringComparison.OrdinalIgnoreCase);

    private static string FormatTriggerPrompt(string template, string sourceKind, string eventName, string payloadSummary)
    {
        if (string.IsNullOrWhiteSpace(template))
            return $"[{sourceKind}:{eventName}] {payloadSummary}";

        var hasPlaceholder = template.Contains("{source}", StringComparison.Ordinal)
            || template.Contains("{event}", StringComparison.Ordinal)
            || template.Contains("{summary}", StringComparison.Ordinal)
            || template.Contains("{payload}", StringComparison.Ordinal);

        var formatted = template
            .Replace("{source}", sourceKind, StringComparison.Ordinal)
            .Replace("{event}", eventName, StringComparison.Ordinal)
            .Replace("{summary}", payloadSummary, StringComparison.Ordinal)
            .Replace("{payload}", payloadSummary, StringComparison.Ordinal);

        return hasPlaceholder || string.IsNullOrWhiteSpace(payloadSummary)
            ? formatted
            : $"{formatted}\n\nEvent [{sourceKind}:{eventName}]: {payloadSummary}";
    }

    private void RecordDedupLocked(string key)
    {
        if (!_dedupSet.Add(key))
            return;
        _dedupOrder.Enqueue(key);
        while (_dedupOrder.Count > MaxDedupEntries)
        {
            var oldest = _dedupOrder.Dequeue();
            _dedupSet.Remove(oldest);
        }
    }

    private void LoadFromDisk()
    {
        if (BindingsFilePath() is { } bindingsPath
            && ChannelStorage.ReadJson(bindingsPath, DshChannelsJsonContext.Default.ChannelBindingsDocument) is { } bindingsDoc)
        {
            lock (_stateLock)
            {
                foreach (var binding in bindingsDoc.Bindings)
                    _bindings[binding.BindingId] = binding;
            }
        }

        if (TriggersFilePath() is { } triggersPath
            && ChannelStorage.ReadJson(triggersPath, DshChannelsJsonContext.Default.AutomationTriggersDocument) is { } triggersDoc)
        {
            lock (_stateLock)
            {
                foreach (var trigger in triggersDoc.Triggers)
                    _triggers[trigger.RuleId] = trigger;
            }
        }

        if (DedupFilePath() is { } dedupPath
            && ChannelStorage.ReadJson(dedupPath, DshChannelsJsonContext.Default.ChannelDedupDocument) is { } dedupDoc)
        {
            lock (_stateLock)
            {
                foreach (var id in dedupDoc.SeenMessageIds)
                    RecordDedupLocked(id);
            }
        }
    }

    private void PersistBindingsSync(ChannelBindingsDocument document)
    {
        if (BindingsFilePath() is not { } path)
            return;
        _persistLock.Wait();
        try
        {
            ChannelStorage.WriteJsonAtomic(path, document, DshChannelsJsonContext.Default.ChannelBindingsDocument);
        }
        finally
        {
            _persistLock.Release();
        }
    }

    private void PersistTriggersSync(AutomationTriggersDocument document)
    {
        if (TriggersFilePath() is not { } path)
            return;
        _persistLock.Wait();
        try
        {
            ChannelStorage.WriteJsonAtomic(path, document, DshChannelsJsonContext.Default.AutomationTriggersDocument);
        }
        finally
        {
            _persistLock.Release();
        }
    }

    private async Task PersistDedupAsync(ChannelDedupDocument document, CancellationToken cancellationToken)
    {
        if (DedupFilePath() is not { } path)
            return;
        await _persistLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await ChannelStorage.WriteJsonAtomicAsync(path, document, DshChannelsJsonContext.Default.ChannelDedupDocument, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _persistLock.Release();
        }
    }

    private string? BindingsFilePath()
        => _channelsDirectory is null ? null : Path.Combine(_channelsDirectory, BindingsFileName);

    private string? TriggersFilePath()
        => _channelsDirectory is null ? null : Path.Combine(_channelsDirectory, TriggersFileName);

    private string? DedupFilePath()
        => _channelsDirectory is null ? null : Path.Combine(_channelsDirectory, DedupFileName);

    private sealed class AdapterRegistration(Action dispose) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
                dispose();
        }
    }
}
