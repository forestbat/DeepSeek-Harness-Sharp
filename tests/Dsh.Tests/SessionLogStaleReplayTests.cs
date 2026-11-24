using Dsh.Core;
using Dsh.Llm;
using Dsh.Persistence;

namespace Dsh.Tests;

/**
 * 两份真实缺陷的回归: ①"被两个写入者交错写过"的日志(序号回退), ②表面折叠的派生缓存。
 *
 * ① 现场: 同一份会话日志先写到 638053, 又被另一个写入者带着更早的游标从 623136 重写了一遍。
 *    整份日志因此不再单调, 按页读会把每一页都判成不可用 —— 压缩要折整条日志时直接卡死。
 *    全量解析早已把这种"落后的整块重放"整块丢弃(SessionLogScanner.IsStaleReplay), 窗口读页必须同样处理。
 * ② 表面是派生状态: 折一遍要读整条日志, 恢复会话不该每次都付这笔钱。
 */
public sealed class SessionLogStaleReplayTests : IDisposable
{
    private const int EventCount = 5000;
    private const int ReplayFrom = 100;
    private const int ReplayTo = 150;
    private const int TailEvents = 400;
    private const int FoldEvents = 6000;
    /** 日志被换成更短的那一份时保留的事件条数。 */
    private const int ShortEvents = 200;

    private readonly List<string> _roots = [];

    [Fact]
    public void WindowedRead_DropsTheStaleReplayBlockAndStillServesEveryPage()
    {
        var (root, header) = WriteAndCorrupt();
        using var store = new JsonlSessionPersistence(root);
        var window = store.TryOpenWindow(header.Id, TailEvents);
        Assert.True(window is not null, "precondition: the log must stay windowed after the corruption");
        var paged = window.Value;
        Assert.True(paged.TotalEvents >= EventCount, $"the window must still be able to count the log (got {paged.TotalEvents})");

        var session = Session.FromWindow(header.Id, paged, header, 0);
        // 逐页取整段: 重放块那一页以前整页读不回来, 现在应当原样给出 100..149 之后的事件。
        var page = paged.ReadPage!.Invoke(ReplayFrom, 60);
        Assert.NotEmpty(page);
        Assert.Equal(ReplayFrom, page[0].Seq);
        for (var index = 0; index < page.Count; index += 1)
            Assert.Equal(ReplayFrom + index, page[index].Seq);
        // 表也照旧: 前缀 + 尾窗拼出整条日志, 表面覆盖全部回合。
        var stored = ReadEvents(root, header.Id);
        Assert.Equal(EventCount, stored.Count);
        Assert.True(session.Seq >= stored.Count, $"the windowed view lost events ({session.Seq} < {stored.Count})");
        Assert.NotEmpty(session.SurfaceManager.Nodes);
        Assert.NotEmpty(session.DeriveMessages());
    }

    /**
     * 派生缓存: 落过一次折叠结果之后, 窗口化恢复不必再读页就能拿到同样的表面。
     */
    [Fact]
    public void SurfaceFoldCache_ResumesTheSurfaceWithoutReadingPages()
    {
        var root = NewRoot();
        var header = NewHeader(root);
        using (var writer = CreatePerEventWriter(root, header))
        {
            writer.Append(BuildEvents(header, FoldEvents));
            writer.Flush();
        }
        var full = ReadEvents(root, header.Id);
        Assert.Equal(FoldEvents, full.Count);

        // 先像正常恢复那样折一遍, 再把折叠结果存成派生缓存。
        IReadOnlyList<long> expected;
        using (var store = new JsonlSessionPersistence(root))
        {
            var window = store.TryOpenWindow(header.Id, TailEvents);
            Assert.True(window is not null, "precondition: the log must be windowed");
            var session = Session.FromWindow(header.Id, window.Value, header, 0);
            expected = session.SurfaceManager.Nodes;
            Assert.NotEmpty(expected);
            Assert.True(session.PageFaults > 0, $"precondition: folding must read the prefix (residentFrom={window.Value.ResidentFrom}, total={window.Value.TotalEvents}, nodes={session.SurfaceManager.Nodes.Count}, faults={session.PageFaults})");

            var fold = session.FoldSnapshot();
            Assert.NotNull(fold);
            store.SaveSurfaceFold(header.Id, fold);
        }

        using var reopened = new JsonlSessionPersistence(root, warn: message => Console.Error.WriteLine($"WARN: {message}"));
        var cached = reopened.TryLoadSurfaceFold(header.Id);
        var logPath = JsonlLayout.LogPath(root, header.Cwd, header.Id, JsonlCompression.Zstd);
        Assert.True(File.Exists($"{logPath}.surface"), $"sidecar missing next to {logPath}: " + string.Join(",", Directory.GetFiles(Path.GetDirectoryName(logPath)!, "*")));
        Assert.NotNull(cached);
        var rewritten = Session.FromWindow(header.Id, reopened.TryOpenWindow(header.Id, TailEvents)!.Value, header, 0, cached);
        // 缓存命中: 表面立刻就是完整的那份, 一条前缀页都不用读。
        Assert.Equal(expected.Count, rewritten.SurfaceManager.Nodes.Count);
        Assert.Equal(0, rewritten.PageFaults);
        Assert.True(rewritten.Seq >= full.Count, $"restored {rewritten.Seq} event(s) < stored {full.Count}");
    }

    /** 回退让日志变短: 旧的折叠快照必须作废, 否则恢复出来的是"回退之前"的表面。 */
    [Fact]
    public void SurfaceFoldCache_IsDroppedWhenTheLogShrinks()
    {
        var root = NewRoot();
        var header = NewHeader(root);
        using (var writer = CreatePerEventWriter(root, header))
            writer.Append(BuildEvents(header, FoldEvents));

        using (var store = new JsonlSessionPersistence(root))
        {
            var window = store.TryOpenWindow(header.Id, TailEvents)!.Value;
            var session = Session.FromWindow(header.Id, window, header, 0);
            _ = session.SurfaceManager.Nodes;
            store.SaveSurfaceFold(header.Id, session.FoldSnapshot()!);
            Assert.NotNull(store.TryLoadSurfaceFold(header.Id));

            // 日志真的变短: 用产品写入路径把前 ShortEvents 条重新编码成一份日志, 覆盖掉原日志。
            // 物理截断已经取消, "变短"只剩存储层这一条来源(打开时的损伤修复会丢掉读不出来的记录),
            // 而"读侧必须容忍日志变短"这条不变量还在, 所以现场得由字节层造。
            ReplaceLogWithAShorterOne(root, header, ShortEvents);
            store.DropSurfaceFold(header.Id);
        }

        using var reopened = new JsonlSessionPersistence(root);
        Assert.Null(reopened.TryLoadSurfaceFold(header.Id));
    }

    /**
     * 把日志换成"只含前 count 条事件"的那一份: 事件由产品写入路径编码(不手写 JSON), 再覆盖回原日志路径。
     * 覆盖之后原来的帧索引与新字节对不上, 一并丢掉(索引是派生数据, 丢了就该重建)。
     */
    private void ReplaceLogWithAShorterOne(string root, SessionHeader header, int count)
    {
        var scratch = NewRoot();
        var shorterHeader = header with { Cwd = Path.Combine(scratch, "workspace") };
        using (var writer = CreatePerEventWriter(scratch, shorterHeader))
            writer.Append(BuildEvents(shorterHeader, count));
        var logPath = JsonlLayout.LogPath(root, header.Cwd, header.Id, JsonlCompression.Zstd);
        File.Copy(JsonlLayout.LogPath(scratch, shorterHeader.Cwd, shorterHeader.Id, JsonlCompression.Zstd), logPath, overwrite: true);
        SessionLogIndex.Delete(logPath);
    }

    /** 每条事件一帧的写句柄: 只有帧足够细, 尾窗才真的"只常驻尾部", 前缀才会走页读。 */
    private static ISessionHandle CreatePerEventWriter(string root, SessionHeader header)
        => new JsonlSessionPersistence(root, writeBatch: new JsonlWriteBatch(MaxEvents: 1, Interval: TimeSpan.FromMilliseconds(1)))
            .Create(header);

    private (string Root, SessionHeader Header) WriteAndCorrupt()
    {
        var root = NewRoot();
        var header = NewHeader(root);
        var logPath = JsonlLayout.LogPath(root, header.Cwd, header.Id, JsonlCompression.Zstd);
        using (var writer = new JsonlSessionPersistence(root, writeBatch: new JsonlWriteBatch(MaxEvents: 1, Interval: TimeSpan.FromMilliseconds(1))).Create(header))
        {
            foreach (var sessionEvent in BuildEvents(header, EventCount))
            {
                writer.Append([sessionEvent]);
                writer.Flush();
            }
        }

        // 造出"落后重放"现场: 前缀(前 ReplayTo 条) + 再抄一遍 [ReplayFrom, ReplayTo) 的帧 + 其余事件。
        var bytes = File.ReadAllBytes(logPath);
        var frames = ZstdFrames.Scan(bytes).Frames;
        Assert.True(frames.Count >= EventCount + 1, $"precondition: one frame per event, got {frames.Count}");
        long Start(int eventIndex) => frames[eventIndex + 1].Start;
        var replayBytes = Start(ReplayTo) - Start(ReplayFrom);
        var corrupted = new byte[bytes.Length + replayBytes];
        var cursor = 0;
        void Copy(long from, long to)
        {
            Array.Copy(bytes, from, corrupted, cursor, to - from);
            cursor += (int)(to - from);
        }
        Copy(0, Start(ReplayTo));
        Copy(Start(ReplayFrom), Start(ReplayTo));
        Copy(Start(ReplayTo), bytes.Length);
        File.WriteAllBytes(logPath, corrupted.AsSpan(0, cursor).ToArray());
        SessionLogIndex.Delete(logPath);
        return (root, header);
    }

    private List<SessionEvent> ReadEvents(string root, SessionId id)
    {
        using var store = new JsonlSessionPersistence(root);
        using var handle = store.Open(id, SessionAccess.Read);
        return [.. handle.Read()];
    }

    private List<SessionEvent> BuildEvents(SessionHeader header, int count)
    {
        var session = Session.Create(header.Id, null, header);
        for (var turn = 1; session.Seq < count; turn += 1)
        {
            session.Append(new TurnStartPayload(turn));
            session.Append(new StepStartPayload(turn, 1));
            session.Append(new UserMessagePayload(MessageFactory.CreateUserText($"turn-{turn}")), new SurfaceOp.Append());
            var assistant = MessageFactory.CreateAssistantMessage([new TextBlock($"answer-{turn}")], "test", "test-model");
            session.Append(new AssistantMessagePayload(turn, 1, assistant), new SurfaceOp.Append());
            session.Append(new StepEndPayload(turn, 1));
            session.Append(new TurnEndPayload(turn, new TurnEndReason.Completed()));
        }
        return [.. session.SnapshotEvents().Take(count)];
    }

    private string NewRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "dsh-stale-replay", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        _roots.Add(root);
        return root;
    }

    private static SessionHeader NewHeader(string root) => new()
    {
        Id = SessionId.Create(Guid.NewGuid().ToString("N")),
        Version = SessionHeader.SessionFormatVersion,
        Cwd = Path.Combine(root, "workspace"),
        CreatedAt = 1_700_000_000_000,
        IsSeeded = false,
    };

    public void Dispose()
    {
        foreach (var root in _roots)
        {
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
            }
        }
    }
}