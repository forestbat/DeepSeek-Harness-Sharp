using System.Text.Json;
using System.Text.Json.Serialization;

namespace Dsh.Core;

/** Dsh.Core 会话事件与相关类型根;多态根由自定义转换器接管,这里只提供元数据。 */
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(SessionEvent))]
[JsonSerializable(typeof(SessionEventPayload))]
[JsonSerializable(typeof(TurnStartPayload))]
[JsonSerializable(typeof(TurnEndPayload))]
[JsonSerializable(typeof(StepStartPayload))]
[JsonSerializable(typeof(StepEndPayload))]
[JsonSerializable(typeof(UserMessagePayload))]
[JsonSerializable(typeof(SystemMessagePayload))]
[JsonSerializable(typeof(AssistantChunkPayload))]
[JsonSerializable(typeof(AssistantMessagePayload))]
[JsonSerializable(typeof(ToolCallPayload))]
[JsonSerializable(typeof(ToolResultPayload))]
[JsonSerializable(typeof(RequestHeaderPayload))]
[JsonSerializable(typeof(RequestContextPayload))]
[JsonSerializable(typeof(SessionEndSeedPayload))]
[JsonSerializable(typeof(UnknownSessionEventPayload))]
[JsonSerializable(typeof(InboxSplicePayload))]
[JsonSerializable(typeof(WorkingDirectoryChangedPayload))]
[JsonSerializable(typeof(ToolResultErrorInfo))]
[JsonSerializable(typeof(EpochHeader))]
[JsonSerializable(typeof(LlmCallConfigAdapterDefaults))]
[JsonSerializable(typeof(SurfaceOp))]
[JsonSerializable(typeof(SurfaceOp.Append))]
[JsonSerializable(typeof(SurfaceOp.Replace))]
[JsonSerializable(typeof(TurnEndReason))]
[JsonSerializable(typeof(TurnEndReason.Completed))]
[JsonSerializable(typeof(TurnEndReason.Aborted))]
[JsonSerializable(typeof(TurnEndReason.Blocked))]
[JsonSerializable(typeof(TurnEndReason.Error))]
[JsonSerializable(typeof(TurnEndReason.MaxTokens))]
[JsonSerializable(typeof(TurnEndReason.Interrupted))]
[JsonSerializable(typeof(TurnEndReason.Unknown))]
[JsonSerializable(typeof(AgentCancelCause))]
[JsonSerializable(typeof(AgentCancelCause.User))]
[JsonSerializable(typeof(AgentCancelCause.Parent))]
[JsonSerializable(typeof(AgentCancelCause.Hook))]
[JsonSerializable(typeof(AgentCancelCause.Disposed))]
[JsonSerializable(typeof(AgentCancelCause.Legacy))]
[JsonSerializable(typeof(SessionHeader))]
[JsonSerializable(typeof(MemoryAuditEntry))]
[JsonSerializable(typeof(IReadOnlyList<long>))]
[JsonSerializable(typeof(JsonElement))]
public sealed partial class DshCoreJsonContext : JsonSerializerContext
{
}

