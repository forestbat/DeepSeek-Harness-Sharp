using System.Text.Json.Serialization;

namespace Dsh.Account;

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(AccountGrant))]
[JsonSerializable(typeof(AccountDevice))]
internal sealed partial class DshAccountJsonContext : JsonSerializerContext
{
}
