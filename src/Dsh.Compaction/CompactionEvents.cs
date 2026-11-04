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
    /** 同一会话上已有一次压缩在进行(可能是另一个窗口/进程发起的)。 */
    Busy,

    /** 会话里还有回合没结束: 压缩必须落在回合之间, 否则会把正在进行的请求的上下文换掉。 */
    BusyTurn,

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

/**
 * 被压缩/剪枝覆盖的那段历史: Start 是表面上第一个被覆盖节点的 seq, End 是最后一个。
 * 压缩会在表面中间插入新 seq, 之后表面的 seq 不再单调递增, 所以 Start 可能大于 End ——
 * 它们标识"首尾两个节点"而不是"数值区间", 判定范围请按表面位置, 不要比较大小。
 */
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
        SessionEventRelations.Register<CompactionStartPayload>(
            Start,
            payload => [new SessionEventRelation.Opens(new SessionEventCorrelation(Start, payload.CompactionId.Value))]);
        SessionEventRelations.Register<CompactionEndPayload>(
            End,
            payload => [new SessionEventRelation.RefersTo(new SessionEventCorrelation(Start, payload.CompactionId.Value), SessionEventEdgeKind.Completes)]);
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
