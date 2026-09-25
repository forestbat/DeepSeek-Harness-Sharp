using System.Text.Json.Serialization;

namespace Dsh.Presets;

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(PresetModePayload))]
internal sealed partial class DshPresetsJsonContext : JsonSerializerContext
{
}
