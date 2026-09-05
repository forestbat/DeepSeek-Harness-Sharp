using System.Text.Json;
using System.Text.Json.Serialization;
using Dsh.Core;
using Dsh.Llm;

namespace Dsh.Compaction;

[JsonConverter(typeof(BrandJsonConverter<CompactionId>))]
public readonly record struct CompactionId(string Value) : IBrand<CompactionId>
{
    public static CompactionId Create(string value) => new(value);
    public override string ToString() => Value;
}

public enum CompactionTrigger
{
    Pressure,
    ContextOverflow,
}

public enum ManualCompactionErrorCode
{
    Busy,
    Cancelled,
    Changed,
    Summary,
    Commit,
    Persistence,
}

public sealed class ManualCompactionError : Exception
{
    public ManualCompactionErrorCode ErrorCode { get; }

    public ManualCompactionError(ManualCompactionErrorCode code, string message, Exception? innerException = null)
        : base(message, innerException)
    {
        ErrorCode = code;
    }
}

public sealed record ShadowedRange(long Start, long End);

public sealed record CompactionResult(
    CompactionId CompactionId,
    string? SourceCommandId,
    long StartSeq,
    long SummarySeq,
    long EndSeq,
    IReadOnlyList<ContentBlock> Summary,
    ShadowedRange ShadowedRange,
    IReadOnlyList<long> ShadowedSeqs,
    int ShadowedTokenCount);

public sealed record CompactionStartPayload(
    CompactionId CompactionId,
    string? SourceCommandId,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] int? Turn) : SessionEventPayload
{
    public override string Type => CompactionEventTypes.Start;
}

public sealed record CompactionSummaryPayload(
    CompactionId CompactionId,
    string? SourceCommandId,
    IReadOnlyList<ContentBlock> Summary,
    ShadowedRange ShadowedRange,
    IReadOnlyList<long> ShadowedSeqs,
    int ShadowedTokenCount,
    string Provider,
    string Model,
    int? MaxTokens = null,
    TokenUsage? Usage = null,
    IReadOnlyList<ContentBlock>? RawOutput = null,
    bool? LlmStreamCall = null) : SessionEventPayload
{
    public override string Type => CompactionEventTypes.Summary;
}

public sealed record CompactionEndPayload(
    CompactionId CompactionId,
    string? SourceCommandId,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] int? Turn,
    string? Error = null) : SessionEventPayload
{
    public override string Type => CompactionEventTypes.End;
}

public sealed record CompactionPrunePayload(
    ShadowedRange ShadowedRange,
    IReadOnlyList<long> ShadowedSeqs,
    int ShadowedTokenCount) : SessionEventPayload
{
    public override string Type => CompactionEventTypes.Prune;
}

public static class CompactionEventTypes
{
    public const string Start = "compaction/start";
    public const string Summary = "compaction/summary";
    public const string End = "compaction/end";
    public const string Prune = "compaction/prune";

    private static int _registered;

    public static void EnsureRegistered()
    {
        if (Interlocked.Exchange(ref _registered, 1) != 0)
            return;
        SessionEventCodec.Register<CompactionStartPayload>(Start);
        SessionEventCodec.Register<CompactionSummaryPayload>(Summary);
        SessionEventCodec.Register<CompactionEndPayload>(End);
        SessionEventCodec.Register<CompactionPrunePayload>(Prune);
    }
}

public static class CompactionCheckpoint
{
    public const string PluginName = "compact";

    public static PluginMessageSource Source(CompactionId compactionId, string? sourceCommandId = null)
        => new(PluginName, CompactionId: compactionId.Value, SourceCommandId: sourceCommandId);

    public static bool IsCheckpointSource(MessageSource source)
        => source is PluginMessageSource { Plugin: PluginName };
}
