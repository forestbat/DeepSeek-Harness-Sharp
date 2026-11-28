using System.Text.Json.Serialization;
using Dsh.Core;
using Dsh.Llm;

namespace Dsh.Agent;

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(AgentTargetsToolResult))]
[JsonSerializable(typeof(AgentWakeToolResult))]
[JsonSerializable(typeof(AgentSuspendToolResult))]
[JsonSerializable(typeof(RemoteExecutionTarget))]
[JsonSerializable(typeof(IReadOnlyList<RemoteExecutionTarget>))]
[JsonSerializable(typeof(AgentProfile))]
[JsonSerializable(typeof(IReadOnlyList<AgentProfile>))]
[JsonSerializable(typeof(AgentExecutionSnapshot))]
[JsonSerializable(typeof(AgentDeliveryResult))]
[JsonSerializable(typeof(AgentEventSubscription))]
[JsonSerializable(typeof(AgentDispatchRequest))]
[JsonSerializable(typeof(AgentEnvelope))]
[JsonSerializable(typeof(AgentRuntimeEvent))]
[JsonSerializable(typeof(AgentId))]
[JsonSerializable(typeof(SessionId))]
[JsonSerializable(typeof(AgentBackendKind))]
[JsonSerializable(typeof(AgentLifecycleState))]
[JsonSerializable(typeof(AgentDeliveryStatus))]
[JsonSerializable(typeof(AgentMessageIntent))]
[JsonSerializable(typeof(MessageTrustLevel))]
[JsonSerializable(typeof(PrincipalKind))]
[JsonSerializable(typeof(AgentRuntimeEventKind))]
[JsonSerializable(typeof(MessagePrincipal))]
[JsonSerializable(typeof(MessageAttribution))]
[JsonSerializable(typeof(AgentOptions))]
[JsonSerializable(typeof(IReadOnlyList<AgentRuntimeEventKind>))]
[JsonSerializable(typeof(IReadOnlyList<SessionId>))]
[JsonSerializable(typeof(IReadOnlyList<string>))]
[JsonSerializable(typeof(IReadOnlyDictionary<string, string>))]
internal sealed partial class DshAgentJsonContext : JsonSerializerContext
{
}
