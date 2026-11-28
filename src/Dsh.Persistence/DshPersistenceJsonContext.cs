using System.Text.Json.Serialization;
using Dsh.Core;
using Dsh.Llm;

namespace Dsh.Persistence;

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(AgentProfile))]
[JsonSerializable(typeof(List<AgentProfile>))]
[JsonSerializable(typeof(AgentEnvelope))]
[JsonSerializable(typeof(List<AgentEnvelope>))]
[JsonSerializable(typeof(AgentId))]
[JsonSerializable(typeof(SessionId))]
[JsonSerializable(typeof(AgentLifecycleState))]
[JsonSerializable(typeof(AgentBackendKind))]
[JsonSerializable(typeof(AgentOptions))]
[JsonSerializable(typeof(PrincipalKind))]
[JsonSerializable(typeof(MessageTrustLevel))]
[JsonSerializable(typeof(AgentMessageIntent))]
[JsonSerializable(typeof(MessagePrincipal))]
[JsonSerializable(typeof(IReadOnlyList<SessionId>))]
[JsonSerializable(typeof(IReadOnlyList<string>))]
[JsonSerializable(typeof(IReadOnlyDictionary<string, string>))]
internal sealed partial class DshPersistenceJsonContext : JsonSerializerContext
{
}
