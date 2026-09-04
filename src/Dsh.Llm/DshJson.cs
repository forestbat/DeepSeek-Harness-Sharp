using System.Text.Json;
using System.Text.Json.Serialization;

namespace Dsh.Llm;

public static class DshJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };
}
