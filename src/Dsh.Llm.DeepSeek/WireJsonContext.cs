using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Dsh.Llm.DeepSeek;

[JsonSourceGenerationOptions(
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(WireRequest))]
[JsonSerializable(typeof(WireMessage))]
[JsonSerializable(typeof(WireMessage.System))]
[JsonSerializable(typeof(WireMessage.User))]
[JsonSerializable(typeof(WireMessage.Assistant))]
[JsonSerializable(typeof(WireMessage.Tool))]
[JsonSerializable(typeof(WireToolCall))]
[JsonSerializable(typeof(WireTool))]
[JsonSerializable(typeof(WireChunk))]
[JsonSerializable(typeof(WireChoice))]
[JsonSerializable(typeof(WireDelta))]
[JsonSerializable(typeof(WireToolCallDelta))]
[JsonSerializable(typeof(WireToolCallDeltaFunction))]
[JsonSerializable(typeof(WireUsage))]
[JsonSerializable(typeof(WirePromptTokensDetails))]
[JsonSerializable(typeof(WireCompletionTokensDetails))]
[JsonSerializable(typeof(WireError))]
[JsonSerializable(typeof(WireErrorBody))]
[JsonSerializable(typeof(JsonObject))]
[JsonSerializable(typeof(JsonNode))]
public sealed partial class DeepSeekWireJsonContext : JsonSerializerContext;
