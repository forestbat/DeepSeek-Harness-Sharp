namespace Dsh.Core;

public sealed record ChannelBinding(
    string BindingId,
    string AdapterKind,
    string ChannelKey,
    AgentId TargetAgentId,
    RoomId? TargetRoomId = null,
    string? SecretRef = null,
    string? OutboundUrl = null,
    bool Enabled = true,
    long CreatedAt = 0,
    IReadOnlyDictionary<string, string>? Metadata = null);

public sealed record ChannelInboundMessage(
    string BindingId,
    string AdapterKind,
    string ChannelKey,
    string ExternalMessageId,
    string SenderId,
    string SenderName,
    string Body,
    long Timestamp,
    string? ThreadId = null,
    string? EventType = null,
    IReadOnlyDictionary<string, string>? Headers = null);

public sealed record ChannelOutboundMessage(
    string BindingId,
    string Body,
    string? ThreadId = null,
    string? ReplyToMessageId = null);

public sealed record AutomationTriggerRule(
    string RuleId,
    string Name,
    string SourceKind,
    string EventPattern,
    AgentId TargetAgentId,
    string PromptTemplate,
    bool Enabled = true,
    bool WakeAgent = true,
    long CreatedAt = 0);

public interface IExternalChannelAdapter
{
    string AdapterKind { get; }

    Task SendAsync(ChannelBinding binding, ChannelOutboundMessage message, CancellationToken cancellationToken = default);
}

public interface IExternalChannelRouter
{
    const string ServiceName = "externalChannels";

    IDisposable RegisterAdapter(IExternalChannelAdapter adapter);
    ChannelBinding Bind(ChannelBinding binding);
    bool Unbind(string bindingId);
    IReadOnlyList<ChannelBinding> ListBindings();
    Task<AgentDeliveryResult> DispatchInboundAsync(ChannelInboundMessage message, CancellationToken cancellationToken = default);
    Task SendOutboundAsync(ChannelOutboundMessage message, CancellationToken cancellationToken = default);
    AutomationTriggerRule AddTrigger(AutomationTriggerRule rule);
    bool RemoveTrigger(string ruleId);
    IReadOnlyList<AutomationTriggerRule> ListTriggers();
    Task<int> FireTriggerAsync(string sourceKind, string eventName, string payloadSummary, CancellationToken cancellationToken = default);
}
