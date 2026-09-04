using System.Text.Json.Nodes;

namespace Dsh.Llm;

public sealed record ToolSchema(string Name, string Description, JsonObject Parameters);

public sealed record LlmCallConfig(
    string Provider,
    string Model,
    ReasoningEffortId? ReasoningEffort = null,
    double? Temperature = null,
    int? MaxTokens = null,
    IReadOnlyList<string>? Stop = null)
{
    public bool Equals(LlmCallConfig? other)
    {
        if (other is null)
            return false;
        if (Provider != other.Provider
            || Model != other.Model
            || ReasoningEffort != other.ReasoningEffort
            || Temperature != other.Temperature
            || MaxTokens != other.MaxTokens)
            return false;
        if (Stop is null || other.Stop is null)
            return Stop is null == other.Stop is null;
        return Stop.SequenceEqual(other.Stop);
    }

    public override int GetHashCode() => HashCode.Combine(Provider, Model, ReasoningEffort, Temperature, MaxTokens);
}

public class GenerateOptions
{
    public required string Provider { get; init; }
    public required string Model { get; init; }
    public ReasoningEffortId? ReasoningEffort { get; init; }
    public required IReadOnlyList<Message> Messages { get; init; }
    public string? System { get; init; }
    public IReadOnlyList<ToolSchema>? Tools { get; init; }
    public double? Temperature { get; init; }
    public int? MaxTokens { get; init; }
    public IReadOnlyList<string>? Stop { get; init; }
    public SessionId? SessionId { get; init; }
    public GeneratePurpose? Purpose { get; init; }
    public CancellationToken Cancellation { get; init; }
}

public enum GeneratePurpose
{
    Compaction,
    SessionTitle,
}

public sealed record LlmProviderInfo(string Id, string Name);

public sealed record LlmModelInfo(
    string Provider,
    string Id,
    string Name,
    string? Description = null,
    IReadOnlyList<string>? InputModalities = null);

public sealed record LlmResolvedModelInfo(
    string Provider,
    string Id,
    string Name,
    string? Description = null,
    IReadOnlyList<string>? InputModalities = null,
    int? ContextWindow = null,
    int? DefaultMaxTokens = null,
    LlmModelReasoningInfo? Reasoning = null);

public sealed record LlmModelReasoningInfo(IReadOnlyList<LlmReasoningEffortInfo> Efforts, ReasoningEffortId? DefaultEffort = null);

public sealed record LlmReasoningEffortInfo(ReasoningEffortId Id, string Name, string? Description = null);

public abstract class LlmAdapter
{
    public abstract LlmProviderInfo ProviderInfo { get; }

    public abstract ResolvedRetryPolicy ProviderRetryPolicy { get; }

    public abstract IAsyncEnumerable<StreamChunk> Stream(GenerateOptions options, CancellationToken cancellationToken);

    public virtual IReadOnlyList<LlmModelInfo> ListModels() => [];

    public virtual LlmResolvedModelInfo? ResolveModel(string model) => null;

    public virtual PreparedAdapterCall PrepareCall(string model, CancellationToken cancellationToken)
        => new(model, Stream);
}

public delegate IAsyncEnumerable<StreamChunk> AdapterStreamDelegate(GenerateOptions options, CancellationToken cancellationToken);

public sealed record PreparedAdapterCall(string Model, AdapterStreamDelegate Stream);

public static class AgentLoopRequestMarker
{
    private static readonly HashSet<GenerateOptions> Marked = new(ReferenceEqualityComparer.Instance);
    private static readonly Lock Gate = new();

    public static T Mark<T>(T request) where T : GenerateOptions
    {
        lock (Gate)
            Marked.Add(request);
        return request;
    }

    public static bool IsMarked(GenerateOptions request)
    {
        lock (Gate)
            return Marked.Contains(request);
    }
}
