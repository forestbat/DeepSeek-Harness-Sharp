using System.Text.Json;
using System.Text.Json.Serialization;
using Dsh.Core;
using Dsh.Subprocess;

namespace Dsh.Tools;

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(TodoWritePayload))]
[JsonSerializable(typeof(TodoItem))]
[JsonSerializable(typeof(TodoWriteResult))]
[JsonSerializable(typeof(TodoCounts))]
[JsonSerializable(typeof(BashStreamOutput))]
[JsonSerializable(typeof(BashRunValue))]
[JsonSerializable(typeof(ReadResultValue))]
[JsonSerializable(typeof(ReadFileLine))]
[JsonSerializable(typeof(ReadImageValue))]
[JsonSerializable(typeof(SubprocessOutputRead))]
[JsonSerializable(typeof(WriteResultValue))]
[JsonSerializable(typeof(EditResultValue))]
[JsonSerializable(typeof(GlobResultValue))]
[JsonSerializable(typeof(GrepMatchValue))]
[JsonSerializable(typeof(GrepResultValue))]
[JsonSerializable(typeof(WorkingDirectoryValue))]
[JsonSerializable(typeof(CreatedWorktreeValue))]
[JsonSerializable(typeof(JsonElement))]
internal sealed partial class DshToolsJsonContext : JsonSerializerContext
{
}

