using System.Text.Json.Serialization;
using Dsh.Lsp.Model;

namespace Dsh.Lsp.Protocol;

[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(WorkspaceIndexSnapshot))]
internal sealed partial class DshLspJsonContext : JsonSerializerContext
{
}
