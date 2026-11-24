using Dsh.Agent;
using Dsh.Core;
using Dsh.Llm;
using Dsh.Persistence;

namespace Dsh.Tests;

/**
 * 折叠检查点的约定: 按读页写, 带上 request/header 与 request/context 的折叠值;
 * 命中检查点才窗口化, 且窗口化与整份恢复必须给出逐条相同的消息(R2/R4)。
 */
public sealed class SessionFoldCheckpointTests : IDisposable
{
    private const string TestCwd = "/tmp/dsh-fold-checkpoint";
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"dsh-fold-{Guid.NewGuid():N}");

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
        }
    }

    [Fact]
    public void Checkpoint_CarriesRequestHeaderAndContext_AndWindowedRestoreMatchesFull()
    {
        using var persistence = new JsonlSessionPersistence(_root, compression: JsonlCompression.Zstd);
        var header = NewHeader();
        using var handle = persistence.Create(header);
        var session = Session.Create(header.Id, header: header);
        session.AttachSequencer(handle.Sequencer);

        var expectedHeader = new EpochHeader(
            new LlmCallConfig(Provider: "deepseek", Model: "deepseek-chat", Temperature: 0.2),
            AdapterDefaults: null,
            Tools: null);
        var expectedContext = new RequestContextPayload("deepseek", "deepseek-chat", 128_000);

        handle.Append([session.Append(new RequestHeaderPayload(expectedHeader, RequestHeaderReasons.Initial))]);
        handle.Append([session.Append(expectedContext)]);
        for (var turn = 1; turn <= 3; turn += 1)
        {
            handle.Append([
                session.Append(new TurnStartPayload(turn)),
                session.Append(new UserMessagePayload(MessageFactory.CreateUserText($"问题 {turn}")), new SurfaceOp.Append()),
                session.Append(new AssistantMessagePayload(1, turn,
                    MessageFactory.CreateAssistantMessage([new TextBlock($"回答 {turn}")], "deepseek", "deepseek-chat")), new SurfaceOp.Append()),
                session.Append(new TurnEndPayload(turn, new TurnEndReason.Completed())),
            ]);
        }
        handle.Flush();

        // 折叠到日志末尾之后才写检查点; 检查点本身带着请求头/上下文。
        var full = session.DeriveMessages();
        var fold = session.FoldSnapshotIfDue();
        Assert.NotNull(fold);
        Assert.NotNull(fold!.RequestHeader);
        Assert.Equal("deepseek-chat", fold.RequestContext?.Model);
        persistence.SaveSurfaceFold(session.Id, fold);

        var adopted = persistence.TryLoadSurfaceFold(session.Id);
        Assert.NotNull(adopted);
        Assert.NotNull(adopted!.RequestHeader);
        Assert.Equal("deepseek-chat", adopted.RequestContext?.Model);

        var paged = persistence.TryOpenWindow(session.Id, tailEvents: 4);
        Assert.NotNull(paged);
        var windowed = Session.FromWindow(session.Id, paged!.Value, header, 0, adopted);
        windowed.AssertFullyFolded();

        // R2: 两条路径的消息逐条一致; R4: 请求头/上下文恢复后可用。
        Assert.Equal(full.Count, windowed.DeriveMessages().Count);
        Assert.True(RequestHeader.Equals(expectedHeader, windowed.RequestHeader()));
        Assert.Equal("deepseek-chat", windowed.RequestContext()?.Model);
    }

    /**
     * 旧检查点(请求头/上下文还没进快照时写的)仍然走窗口化恢复: 这两个字段从日志里补回来。
     *
     * 反过来做(缺头就整份恢复)会把 2 秒的冷启动拖成 5 秒 —— 实测过, 教训记在验收报告里。
     */
    [Fact]
    public void LegacyCheckpointWithoutHeader_StillRestoresWindowed_AndBackfillsFromTheLog()
    {
        using var persistence = new JsonlSessionPersistence(_root, compression: JsonlCompression.Zstd);
        var header = NewHeader();
        using var handle = persistence.Create(header);
        var session = Session.Create(header.Id, header: header);
        session.AttachSequencer(handle.Sequencer);
        var expected = new EpochHeader(new LlmCallConfig(Provider: "deepseek", Model: "deepseek-chat"));
        var context = new RequestContextPayload("deepseek", "deepseek-chat", 128_000);
        handle.Append([session.Append(new RequestHeaderPayload(expected, RequestHeaderReasons.Initial))]);
        handle.Append([session.Append(context)]);
        for (var turn = 1; turn <= 3; turn += 1)
        {
            handle.Append([
                session.Append(new TurnStartPayload(turn)),
                session.Append(new UserMessagePayload(MessageFactory.CreateUserText($"问题 {turn}")), new SurfaceOp.Append()),
                session.Append(new TurnEndPayload(turn, new TurnEndReason.Completed())),
            ]);
        }
        handle.Flush();
        // 与上面那条一样: 先折一遍, 才轮到"该写检查点了"的判定。
        _ = session.DeriveMessages();
        var fold = session.FoldSnapshotIfDue();
        Assert.NotNull(fold);
        var adoptedCandidate = fold! with { RequestHeader = null, RequestContext = null };
        // 模拟旧格式: 表面折叠结果有, 请求头/上下文没有。
        persistence.SaveSurfaceFold(session.Id, adoptedCandidate);

        var adopted = persistence.TryLoadSurfaceFold(session.Id)!;
        Assert.Null(adopted.RequestHeader);
        var paged = persistence.TryOpenWindow(session.Id, tailEvents: 4)!.Value;
        var windowed = Session.FromWindow(session.Id, paged, header, 0, adopted);
        Assert.Null(windowed.RequestHeader());

        // 补回: 与 AgentLoop.BackfillFoldMeta 同一件事(这里直接调产品入口)。
        windowed.AdoptFoldMeta(expected, context);
        Assert.True(RequestHeader.Equals(expected, windowed.RequestHeader()));
        Assert.Equal("deepseek-chat", windowed.RequestContext()?.Model);
    }

    /** 检查点里的页号: 同一个页内不重复写, 跨页才写一次。 */
    [Fact]
    public void FoldSnapshotIfDue_WritesOncePerReadPage()
    {
        var header = NewHeader();
        var session = Session.Create(header.Id, header: header);
        session.Append(new TurnStartPayload(1));
        _ = session.DeriveMessages();
        Assert.NotNull(session.FoldSnapshotIfDue());
        session.Append(new StepStartPayload(1, 1));
        _ = session.DeriveMessages();
        Assert.Null(session.FoldSnapshotIfDue());
    }

    private static SessionHeader NewHeader()
    {
        var id = SessionId.Create(Guid.NewGuid().ToString("N"));
        return new SessionHeader
        {
            Version = SessionHeader.SessionFormatVersion,
            Id = id,
            CreatedAt = 1700000000000,
            Cwd = TestCwd,
            IsSeeded = false
        };
    }
}
