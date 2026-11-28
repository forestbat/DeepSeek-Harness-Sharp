using System.Text.Json.Serialization;
using Dsh.Core;
using Dsh.Llm;

namespace Dsh.Schedule;

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(ScheduleChange))]
[JsonSerializable(typeof(ScheduleWakeEntry))]
[JsonSerializable(typeof(List<ScheduleWakeEntry>))]
[JsonSerializable(typeof(AgentId))]
[JsonSerializable(typeof(SessionId))]
internal sealed partial class DshScheduleJsonContext : JsonSerializerContext
{
}
