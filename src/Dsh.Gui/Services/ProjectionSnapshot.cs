using System.Buffers.Binary;
using System.Text.Json;
using System.Text.Json.Serialization;
using Dsh.Interaction;
using Dsh.Persistence;

namespace Dsh.Gui.Services;

/**
 * 投影结果的**纯数据**形态: 会话流、轨迹、统计与增量重放用到的指针, 一一对应 GUI 视图模型。
 *
 * 它存在的理由只有一个: 百万条事件的会话, 重新打开时"把日志解析成事件、再重放成消息"要十几秒,
 * 而结果本身只有几万条 —— 把它按 (会话, 修订, 投影格式版本) 存到磁盘, 命中就跳过整段工作。
 *
 * 因此这份 DTO 必须与视图模型**严格等价**: 少一个字段, 表现就是"重开之后少一段/样式不同",
 * 比慢更难排查。等价性由 tests/Dsh.Tests/ProjectionCacheTests.cs 用"缓存恢复 vs 新鲜投影"兜住。
 */
public sealed record ProjectionSnapshot
{
    /**
     * 投影格式版本: 视图模型的字段集变了就 +1, 老缓存自然失效(不做兼容升级)。
     * 只**加**字段(带默认值、读出来是"没设过")不用 +1: 旧文件仍能还原出与当时一致的界面,
     * diff 预览那组字段(LineCount/DiffResultSeq)就是这种加法。
     */
    public const int CurrentFormatVersion = 2;

    public required int FormatVersion { get; init; }

    /** 已投影到的下一条序号(= 最后一条事件的序号 + 1); 恢复之后从这里接着增量。 */
    public required long NextSeq { get; init; }

    /** 与 NextSeq 对应的日志修订: 事件总数与已落盘字节数; 日志只追加, 两者不会变小。 */
    public required long TotalEvents { get; init; }

    public required long LogLength { get; init; }

    /** 投影最后一次处理的那条事件的时刻与类型: 只在"长度变大"时用来确认前缀没被改写。 */
    public long? LastEventTime { get; init; }

    public string? LastEventType { get; init; }

    public required bool RenderedAny { get; init; }

    public required bool IsBusy { get; init; }

    public required bool CompactionBusy { get; init; }

    public LlmHeaderSnapshot? RequestHeader { get; init; }

    public required IReadOnlyList<MessageSnapshot> Messages { get; init; }

    public required IReadOnlyList<TraceSnapshot> Trace { get; init; }

    /** 最近若干条用户消息的序号(时间线预览与"重发上一条"用)。 */
    public required IReadOnlyList<long> LastUserMessageSeqs { get; init; }

    public required IReadOnlyList<PendingToolSnapshot> PendingToolCalls { get; init; }

    public required TokenStatsSnapshot Tokens { get; init; }

    /** 侧栏交付物: 每次"交付"的文件组(折叠规则由 SessionDeliverables 提供, 这里只存输入)。 */
    public required IReadOnlyList<DeliverablePresentationSnapshot> Deliverables { get; init; }

    /** 展开的 diff 卡片只在工具结果里出现; 与 Messages 一一对应, 这里单独放以免嵌套太深。 */
    public required IReadOnlyList<DiffCardSnapshot> Diffs { get; init; }

    /** 界面回合号的计数(按日志先后递增, 见 §8.1): 缓存还原后接着用, 否则增量重放会把回合号从头数。 */
    public int DisplayTurn { get; init; }

    public int CurrentDisplayTurn { get; init; }
}

public sealed record LlmHeaderSnapshot(
    string Provider,
    string Model,
    string? ReasoningEffort,
    double? Temperature,
    int? MaxTokens,
    IReadOnlyList<string>? Stop);

public sealed record MessageSnapshot
{
    public required string Role { get; init; }

    public required int Kind { get; init; }

    public required string Text { get; init; }

    public long? Seq { get; init; }

    public required long RenderSeq { get; init; }

    public long? Time { get; init; }

    public required string Detail { get; init; }

    public required bool IsExpanded { get; init; }

    public required bool IsStreaming { get; init; }

    public required int Feedback { get; init; }

    public required string ToolName { get; init; }

    public required string ArgumentText { get; init; }

    public required string ResultText { get; init; }

    public required bool HasResult { get; init; }

    public required bool IsToolError { get; init; }

    public required bool IsDiffExpanded { get; init; }

    public required bool IsSubagentTool { get; init; }

    public string? SubagentLabel { get; init; }

    public string? SubagentSessionId { get; init; }

    /** 指向 ProjectionSnapshot.Diffs 的下标; null 表示这条消息没有 diff 卡片。 */
    public int? Diff { get; init; }

    /**
     * 卡片正文的来源事件序号(那条工具结果): 缓存里只留了预览, 展开时按它回头取正文。
     * null 表示正文已经在手上(重放路径), 或来自还不知道来源的旧缓存。
     */
    public long? DiffResultSeq { get; init; }
}

/**
 * 一张 diff 卡片在缓存里的形态: **标题/来源 + 前 MaxStoredLines 行的预览**。
 *
 * 不存正文全文的理由是体积: 实测明文里 Diffs 占 69.8MB / 866 张(69–78%), 而卡片展开时看到的
 * 本来只有改动附近几行(见 DiffCardDisplay)。正文在展开时按 MessageSnapshot.DiffResultSeq 回头取
 * 那条工具结果事件、重跑 DiffCardExtractor 还原(与重放路径用的是同一份提取代码, 不另写一套);
 * 取不回来(远端机器/宿主不支持按区间取/那条事件已被回滚)时界面就停在这份预览。
 */
public sealed record DiffCardSnapshot(string Title, string Source, IReadOnlyList<DiffLineSnapshot> Lines)
{
    /** 每张卡片在缓存里最多留多少行(约 20KB): 预览够看出这处改动改了什么, 又不再按整份正文付磁盘。 */
    public const int MaxStoredLines = 200;

    /**
     * 原始卡片的行数; 大于 Lines.Count 说明这份只是预览。
     * 旧缓存(格式版本 2, 存的是完整正文)没有这个字段, 读出来是 0 ⇒ 视为"Lines 就是全部", 照原样显示。
     */
    public int LineCount { get; init; }

    public DiffCard ToCard()
        => new(Title, Source, [.. Lines.Select(line => new DiffLine((DiffLineKind)line.Kind, line.OldLine, line.NewLine, line.Text))])
        {
            TotalLines = LineCount,
        };

    public static DiffCardSnapshot FromCard(DiffCard card)
        => new(card.Title, card.Source, [.. card.Lines.Take(MaxStoredLines).Select(line => new DiffLineSnapshot((int)line.Kind, line.OldLine, line.NewLine, line.Text))])
        {
            // TotalLines 而不是 Lines.Count: 手里已经只是预览的卡片再存一次, 不能把自己说成完整。
            LineCount = card.TotalLines,
        };
}

public sealed record DiffLineSnapshot(int Kind, int? OldLine, int? NewLine, string Text);

public sealed record TraceSnapshot
{
    public required long Seq { get; init; }

    public required int Kind { get; init; }

    public required string Title { get; init; }

    public required string Detail { get; init; }

    public required string Time { get; init; }

    /** 回跳目标在投影里的消息下标; null 表示这条轨迹当时没有对应消息。 */
    public int? Message { get; init; }
}

public sealed record PendingToolSnapshot(string CallId, string Name, string Arguments, int Message);

public sealed record DeliverablePresentationSnapshot(long Seq, IReadOnlyList<PresentedFileSnapshot> Files);

public sealed record PresentedFileSnapshot(string Path, string? Description);

/** 投影覆盖到的日志修订: 与 HostChatSession 打开会话时拿到的快照一一对应。 */
public readonly record struct ProjectionAnchor(
    long NextSeq,
    long TotalEvents,
    long LogLength,
    long? LastEventTime,
    string? LastEventType);

/**
 * 状态行里跨重开仍然成立的量: 累计 token、上下文占用、轮/步。
 * 耗时、首 token 延迟与 tok/s 不在此列 —— 它们度量的是"这次请求跑了多久", 重放一遍也得不到同一个数。
 */
public sealed record TokenStatsSnapshot(
    double InputTokens,
    double OutputTokens,
    double CacheReadTokens,
    int? ContextWindow,
    double LastInputTokens,
    string Rounds);

/** 投影缓存的落盘格式: 头 + zstd 压缩的 JSON。头里的修订让"不匹配"在解压之前就被判掉。 */
public static class ProjectionSnapshotCodec
{
    private static readonly byte[] Magic = "DSHPROJ1"u8.ToArray();

    private const int HeaderBytes = 8 + sizeof(int) + sizeof(long);

    // 不能省略默认值: 这些成员是 required(false/0/空串也算值), 省掉就反序列化不回来。
    private static readonly JsonSerializerOptions Options = new()
    {
        Converters = { new JsonStringEnumConverter() },
    };

    public static byte[] Encode(ProjectionSnapshot snapshot)
    {
        var payload = ZstdFrames.CompressFrame(JsonSerializer.SerializeToUtf8Bytes(snapshot, Options));
        var header = new byte[HeaderBytes];
        Magic.CopyTo(header, 0);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(Magic.Length), ProjectionSnapshot.CurrentFormatVersion);
        BinaryPrimitives.WriteInt64LittleEndian(
            header.AsSpan(Magic.Length + sizeof(int)), DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        return [.. header, .. payload];
    }

    /** 解码: 只要魔数/版本/压缩帧任一不对就返回 null(缓存是"可以没有"的东西, 坏了就当没写)。 */
    public static ProjectionSnapshot? Decode(byte[] bytes)
    {
        if (bytes.Length <= HeaderBytes)
            return null;
        if (!bytes.AsSpan(0, Magic.Length).SequenceEqual(Magic))
            return null;
        var version = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(Magic.Length));
        if (version != ProjectionSnapshot.CurrentFormatVersion)
            return null;
        try
        {
            var json = ZstdFrames.DecompressFrame(bytes, HeaderBytes, bytes.Length - HeaderBytes, HeaderBytes);
            var snapshot = JsonSerializer.Deserialize<ProjectionSnapshot>(json, Options);
            return snapshot?.FormatVersion == ProjectionSnapshot.CurrentFormatVersion ? snapshot : null;
        }
        catch (Exception error) when (error is JsonException or FormatException or IOException)
        {
            return null;
        }
    }
}
