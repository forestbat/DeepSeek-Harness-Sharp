using System.Text.Json.Serialization;

namespace Dsh.PtyTerminal;

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(TerminalSessionStatus))]
[JsonSerializable(typeof(TerminalSessionSnapshot))]
[JsonSerializable(typeof(List<TerminalSessionSnapshot>))]
[JsonSerializable(typeof(IReadOnlyList<TerminalSessionSnapshot>))]
[JsonSerializable(typeof(TerminalSpawnResult))]
[JsonSerializable(typeof(TerminalReadResult))]
[JsonSerializable(typeof(TerminalSignalResult))]
[JsonSerializable(typeof(TerminalSendBackgroundResult))]
[JsonSerializable(typeof(TerminalSendForegroundResult))]
[JsonSerializable(typeof(TerminalCloseResult))]
internal sealed partial class DshTerminalJsonContext : JsonSerializerContext
{
}
