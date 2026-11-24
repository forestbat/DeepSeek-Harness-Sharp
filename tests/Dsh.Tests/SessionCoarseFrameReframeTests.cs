using Dsh.Core;
using Dsh.Llm;
using Dsh.Persistence;

namespace Dsh.Tests;

/**
 * 帧粒度退化的日志(整段历史一帧)在写打开时按现在的帧预算重写一遍。
 *
 * 现场形状: 15MB 的日志里有一帧 10.1MB 压缩 / 163.7MB 明文, 任何落在它里面的一页都要整帧解压(3–5s),
 * 于是"往上翻更早的历史"根本没法做。重写只改帧边界, 事件一条不差。
 */
public sealed class SessionCoarseFrameReframeTests : IDisposable
{
    /** 一帧要装到 8MB 明文(帧预算 × 8)才算退化: 30k 条事件正好越过这条线。 */
    private const int EventCount = 30_000;
    private readonly List<string> _roots = [];

    [Fact]
    public void WriteOpen_ReframesALogWhoseFramesAreTooCoarseToPage()
    {
        var root = NewRoot();
        var header = NewHeader(root);
        var events = BuildEvents(header, EventCount);
        // 造出"一帧装下整段历史"的老形状: 写批次给一个巨大的明文字节预算。
        var coarse = new JsonlWriteBatch(JsonlWriteBatch.DefaultMaxEvents, TimeSpan.FromMilliseconds(10))
        {
            MaxPlaintextBytes = 256 * 1024 * 1024,
        };
        using (var writer = new JsonlSessionPersistence(root, packChunks: false, writeBatch: coarse).Create(header))
            writer.Append(events);

        var logPath = JsonlLayout.LogPath(root, header.Cwd, header.Id, JsonlCompression.Zstd);
        var before = SessionLogIndex.TryLoad(logPath);
        Assert.NotNull(before);
        var coarseSizes = PlaintextSizes(logPath, before);
        Assert.True(coarseSizes.Skip(1).Any(size => size > JsonlWriteBatch.DefaultMaxPlaintextBytes),
            $"precondition: some frame must exceed the write budget, got [{string.Join(",", coarseSizes.Skip(1))}]");

        var warnings = new List<string>();
        using (var store = new JsonlSessionPersistence(root, warn: warnings.Add))
        {
            using var writer = store.Open(header.Id, SessionAccess.Write);
            Assert.Equal(events.Count, writer.Count);
        }

        Assert.Contains(warnings, warning => warning.Contains("re-framed", StringComparison.Ordinal));
        var after = SessionLogIndex.TryLoad(logPath);
        Assert.NotNull(after);
        Assert.True(after.Frames.Count > before.Frames.Count,
            $"the log must be cut into more frames than before ({before.Frames.Count} -> {after.Frames.Count})");
        // 重写之后每一帧都小到"翻一页只解一帧": 明文回到帧预算以内。
        var sizes = PlaintextSizes(logPath, after);
        foreach (var (size, slot) in sizes.Select((size, slot) => (size, slot)).Skip(1))
            Assert.True(size <= JsonlWriteBatch.DefaultMaxPlaintextBytes,
                $"frame {slot} still holds {size} plaintext bytes");

        // 事件一条不差, 序号也连续。
        using var reopened = new JsonlSessionPersistence(root);
        using var reader = reopened.Open(header.Id, SessionAccess.Read);
        var restored = reader.Read();
        Assert.Equal(events.Count, restored.Count);
        for (var index = 0; index < restored.Count; index += 1)
        {
            Assert.Equal(events[index].Seq, restored[index].Seq);
            Assert.Equal(events[index].Data.GetType(), restored[index].Data.GetType());
        }
    }

    /** 正常的帧粒度不该被重写: 重写会改文件的修改时间, 也会白付一次整份压缩。 */
    [Fact]
    public void WriteOpen_LeavesAWellFramedLogAlone()
    {
        var root = NewRoot();
        var header = NewHeader(root);
        var events = BuildEvents(header, EventCount);
        using (var writer = new JsonlSessionPersistence(root).Create(header))
        {
            for (var offset = 0; offset < events.Count; offset += 500)
                writer.Append([.. events.Skip(offset).Take(500)]);
        }
        var logPath = JsonlLayout.LogPath(root, header.Cwd, header.Id, JsonlCompression.Zstd);
        var stamp = new FileInfo(logPath).LastWriteTimeUtc;

        var warnings = new List<string>();
        using (var store = new JsonlSessionPersistence(root, warn: warnings.Add))
        {
            using var writer = store.Open(header.Id, SessionAccess.Write);
            Assert.Equal(events.Count, writer.Count);
        }

        Assert.DoesNotContain(warnings, warning => warning.Contains("re-framed", StringComparison.Ordinal));
        Assert.Equal(stamp, new FileInfo(logPath).LastWriteTimeUtc);
    }

    /** 每帧解压后的字节数(取自帧头), 只读帧头不解压。 */
    private static long[] PlaintextSizes(string logPath, SessionLogIndex index)
    {
        var ranges = index.Frames.Select(frame => new ZstdFrames.FrameRange((int)frame.Start, (int)frame.End)).ToList();
        var sizes = ZstdFrames.PlaintextSizes(File.ReadAllBytes(logPath), ranges);
        Assert.NotNull(sizes);
        return sizes;
    }

    private string NewRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), $"dsh-reframe-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        _roots.Add(root);
        return root;
    }

    private static SessionHeader NewHeader(string root) => new()
    {
        Version = SessionHeader.SessionFormatVersion,
        Id = SessionId.Create(Guid.NewGuid().ToString("N")),
        CreatedAt = 1_700_000_000_000,
        Cwd = Path.Combine(root, "workspace"),
        IsSeeded = false,
    };

    /** 现场比例: 大量 chunk, 每 40 个回合一次请求头与请求上下文。 */
    private static List<SessionEvent> BuildEvents(SessionHeader header, int count)
    {
        var session = Session.Create(header.Id, null, header);
        var turn = 1;
        while (session.Seq < count)
        {
            if (turn % 40 == 1)
            {
                session.Append(new RequestHeaderPayload(
                    new EpochHeader(new LlmCallConfig("deepseek-official", "deepseek-v4-flash")),
                    RequestHeaderReasons.Initial));
                if (session.Seq >= count) break;
                session.Append(new RequestContextPayload("deepseek-official", "deepseek-v4-flash"));
                if (session.Seq >= count) break;
            }
            session.Append(new TurnStartPayload(turn));
            if (session.Seq >= count) break;
            session.Append(new StepStartPayload(turn, 1));
            if (session.Seq >= count) break;
            session.Append(new AssistantChunkPayload(turn, 1, new StreamChunk.TextDelta(0, $"chunk {session.Seq} 行内容\n")));
            if (session.Seq >= count) break;
            session.Append(new UserMessagePayload(MessageFactory.CreateUserText($"q{turn}")), new SurfaceOp.Append());
            if (session.Seq >= count) break;
            session.Append(
                new AssistantMessagePayload(turn, 1,
                    MessageFactory.CreateAssistantMessage([new TextBlock($"a{turn}")], "deepseek-official", "deepseek-v4-flash")),
                new SurfaceOp.Append(),
                [session.Seq - 2]);
            turn += 1;
        }
        return [.. session.SnapshotEvents()];
    }

    public void Dispose()
    {
        foreach (var root in _roots)
        {
            try { Directory.Delete(root, recursive: true); } catch (IOException) { }
        }
    }
}