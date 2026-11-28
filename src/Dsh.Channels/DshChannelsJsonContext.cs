using System.Text.Json.Serialization;
using Dsh.Core;
using Dsh.Llm;

namespace Dsh.Channels;

internal sealed record ChannelBindingsDocument(List<ChannelBinding> Bindings);

internal sealed record AutomationTriggersDocument(List<AutomationTriggerRule> Triggers);

internal sealed record ChannelDedupDocument(List<string> SeenMessageIds);

internal sealed record SlackOutboundPayload(
    string Channel,
    [property: JsonPropertyName("thread_ts")] string? ThreadTs,
    string Text);

internal sealed record SlackUrlVerificationResponse(string Challenge);

internal sealed record DiscordOutboundPayload(string Content);

internal sealed record DiscordPongResponse(int Type);

internal sealed record WebhookAckResponse(
    bool Ok,
    string? Status = null,
    string? EnvelopeId = null,
    int? TriggersFired = null,
    string? Error = null);

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    UseStringEnumConverter = true)]
[JsonSerializable(typeof(ChannelBindingsDocument))]
[JsonSerializable(typeof(AutomationTriggersDocument))]
[JsonSerializable(typeof(ChannelDedupDocument))]
[JsonSerializable(typeof(SlackOutboundPayload))]
[JsonSerializable(typeof(SlackUrlVerificationResponse))]
[JsonSerializable(typeof(DiscordOutboundPayload))]
[JsonSerializable(typeof(DiscordPongResponse))]
[JsonSerializable(typeof(WebhookAckResponse))]
[JsonSerializable(typeof(ChannelBinding))]
[JsonSerializable(typeof(IReadOnlyList<ChannelBinding>))]
[JsonSerializable(typeof(ChannelInboundMessage))]
[JsonSerializable(typeof(ChannelOutboundMessage))]
[JsonSerializable(typeof(AutomationTriggerRule))]
[JsonSerializable(typeof(IReadOnlyList<AutomationTriggerRule>))]
[JsonSerializable(typeof(AgentId))]
[JsonSerializable(typeof(RoomId))]
[JsonSerializable(typeof(SessionId))]
[JsonSerializable(typeof(IReadOnlyDictionary<string, string>))]
internal sealed partial class DshChannelsJsonContext : JsonSerializerContext
{
}
