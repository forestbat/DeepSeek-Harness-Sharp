using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Dsh.A2A;

[JsonConverter(typeof(JsonStringEnumConverter<A2aTaskState>))]
public enum A2aTaskState
{
    [JsonStringEnumMemberName("submitted")] Submitted,
    [JsonStringEnumMemberName("working")] Working,
    [JsonStringEnumMemberName("input-required")] InputRequired,
    [JsonStringEnumMemberName("completed")] Completed,
    [JsonStringEnumMemberName("failed")] Failed,
    [JsonStringEnumMemberName("canceled")] Canceled,
    [JsonStringEnumMemberName("rejected")] Rejected,
}

[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(A2aTextPart), "text")]
[JsonDerivedType(typeof(A2aFilePart), "file")]
[JsonDerivedType(typeof(A2aDataPart), "data")]
public abstract record A2aPart;

public sealed record A2aTextPart(string Text) : A2aPart;

public sealed record A2aFilePart(JsonElement File) : A2aPart;

public sealed record A2aDataPart(JsonElement Data) : A2aPart;

public static class A2aRoles
{
    public const string User = "user";
    public const string Agent = "agent";
}

public sealed record A2aMessage(
    [property: JsonRequired] string MessageId,
    [property: JsonRequired] string Role,
    [property: JsonRequired] IReadOnlyList<A2aPart> Parts,
    string? ContextId = null,
    string? TaskId = null)
{
    public string Kind => "message";
}

public sealed record A2aTaskStatus(A2aTaskState State, A2aMessage? Message = null, string? Timestamp = null);

public sealed record A2aArtifact(
    string ArtifactId,
    IReadOnlyList<A2aPart> Parts,
    string? Name = null,
    string? Description = null);

public sealed record A2aTask(
    string Id,
    string ContextId,
    A2aTaskStatus Status,
    IReadOnlyList<A2aArtifact>? Artifacts = null,
    IReadOnlyList<A2aMessage>? History = null)
{
    public string Kind => "task";
}

public sealed record A2aTaskStatusUpdateEvent(
    string TaskId,
    string ContextId,
    A2aTaskStatus Status,
    bool Final)
{
    public string Kind => "status-update";
}

public sealed record A2aTaskArtifactUpdateEvent(
    string TaskId,
    string ContextId,
    A2aArtifact Artifact,
    bool Append = false,
    bool LastChunk = false)
{
    public string Kind => "artifact-update";
}

public sealed record A2aSendMessageParams([property: JsonRequired] A2aMessage Message, JsonElement? Configuration = null);

public sealed record A2aTaskIdParams([property: JsonRequired] string Id);

public sealed record A2aGetTaskParams([property: JsonRequired] string Id, int? HistoryLength = null);

public sealed record A2aListTasksParams(
    string? ContextId = null,
    A2aTaskState? Status = null,
    int? PageSize = null,
    string? PageToken = null);

public sealed record A2aListTasksResult(
    IReadOnlyList<A2aTask> Tasks,
    string NextPageToken,
    int PageSize,
    int TotalSize);

public sealed record A2aAgentCapabilities(
    bool Streaming,
    bool PushNotifications,
    bool StateTransitionHistory = false);

public sealed record A2aAgentSkill(
    string Id,
    string Name,
    string Description,
    IReadOnlyList<string> Tags,
    IReadOnlyList<string>? Examples = null);

public sealed record A2aHttpSecurityScheme(string Type, string Scheme, string? BearerFormat = null);

public sealed record A2aAgentCard(
    string ProtocolVersion,
    string Name,
    string Description,
    string Url,
    string Version,
    string PreferredTransport,
    A2aAgentCapabilities Capabilities,
    IReadOnlyList<string> DefaultInputModes,
    IReadOnlyList<string> DefaultOutputModes,
    IReadOnlyList<A2aAgentSkill> Skills,
    IReadOnlyDictionary<string, A2aHttpSecurityScheme>? SecuritySchemes = null,
    IReadOnlyList<IReadOnlyDictionary<string, IReadOnlyList<string>>>? Security = null,
    bool SupportsAuthenticatedExtendedCard = false);

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(A2aMessage))]
[JsonSerializable(typeof(A2aPart))]
[JsonSerializable(typeof(A2aTextPart))]
[JsonSerializable(typeof(A2aFilePart))]
[JsonSerializable(typeof(A2aDataPart))]
[JsonSerializable(typeof(A2aTask))]
[JsonSerializable(typeof(A2aTaskStatus))]
[JsonSerializable(typeof(A2aArtifact))]
[JsonSerializable(typeof(A2aTaskStatusUpdateEvent))]
[JsonSerializable(typeof(A2aTaskArtifactUpdateEvent))]
[JsonSerializable(typeof(A2aSendMessageParams))]
[JsonSerializable(typeof(A2aTaskIdParams))]
[JsonSerializable(typeof(A2aGetTaskParams))]
[JsonSerializable(typeof(A2aListTasksParams))]
[JsonSerializable(typeof(A2aListTasksResult))]
[JsonSerializable(typeof(A2aAgentCard))]
[JsonSerializable(typeof(A2aAgentCapabilities))]
[JsonSerializable(typeof(A2aAgentSkill))]
[JsonSerializable(typeof(A2aHttpSecurityScheme))]
[JsonSerializable(typeof(JsonElement))]
internal sealed partial class A2aJsonContext : JsonSerializerContext
{
}

/** 线路转换：A2A 记录 → 序列化 JSON 文本。存字符串而不是 JsonNode，同一帧可广播给多个 SSE 订阅者（JsonNode 单父节点限制）。 */
public static class A2aWire
{
    public static string Now() => DateTimeOffset.UtcNow.ToString("o");

    public static string TaskJson(A2aTask task) => JsonSerializer.Serialize(task, A2aJsonContext.Default.A2aTask);

    public static string StatusEventJson(A2aTaskStatusUpdateEvent statusEvent)
        => JsonSerializer.Serialize(statusEvent, A2aJsonContext.Default.A2aTaskStatusUpdateEvent);

    public static string ArtifactEventJson(A2aTaskArtifactUpdateEvent artifactEvent)
        => JsonSerializer.Serialize(artifactEvent, A2aJsonContext.Default.A2aTaskArtifactUpdateEvent);

    public static string ListResultJson(A2aListTasksResult result)
        => JsonSerializer.Serialize(result, A2aJsonContext.Default.A2aListTasksResult);

    public static string CardJson(A2aAgentCard card) => JsonSerializer.Serialize(card, A2aJsonContext.Default.A2aAgentCard);

    public static JsonElement DataElement(JsonNode node)
        => JsonDocument.Parse(node.ToJsonString()).RootElement.Clone();
}
