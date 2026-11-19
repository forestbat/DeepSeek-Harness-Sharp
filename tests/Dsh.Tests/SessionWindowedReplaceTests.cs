using Dsh.Core;
using Dsh.Llm;
using Dsh.Persistence;

namespace Dsh.Tests;

/**
 * 尾窗里的 replace 指向窗口之前的节点(跨边界 replace)时的窗口化打开。
 *
 * 现场形状: 大会话被压缩过, 压缩检查点(一条 replace)落在尾部, 它收掉的却是窗口之前的那批消息。
 * **窗口不再为此退到被替换的节点**: 折叠把"本地要收掉的"取成表头到终点那一段; 起点落在窗口之前的部分
 * 本来就不在 surface 上, 它们由事件自带的 sourceEventSeqs 表达(provenance 要求"阴影 ⊆ 来源")。
 * 于是窗口大小只由"要多少条事件"决定, 与 replace 指向哪里无关。
 */
public sealed class SessionWindowedReplaceTests
{
    private const int EventCount = 12_000;
    private const int TailEvents = 2_000;
    /** 尾窗起点由测试自己定(不去猜帧边界); 打开时的起点只由事件条数决定。 */
    private const long ResidentFrom = 10_000;

    [Fact]
    public void TryOpenWindow_KeepsThePlainTailEvenWhenAReplacePointsBeforeIt()
    {
        var root = NewRoot();
        try
        {
            var header = NewHeader(root);
            var log = BuildLog(header, ResidentFrom);
            WriteLog(root, header, log.Events);

            var warnings = new List<string>();
            using var store = new JsonlSessionPersistence(root, warn: warnings.Add);
            var window = store.TryOpenWindow(header.Id, TailEvents);
            Assert.True(window is not null, $"the tail window was declined: {string.Join("; ", warnings)}");
            // 窗口只按条数取: 被替换的节点在窗口之前, 不会被拉进来。
            Assert.True(
                window.Value.ResidentFrom > log.ShadowStart,
                $"the window was pulled back to {window.Value.ResidentFrom} for the replaced node {log.ShadowStart}");
            Assert.True(window.Value.Resident.Count >= TailEvents, "the window must still hold the requested tail");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /**
     * 这种窗口上折叠必须成立: 摘要节点成为表头, 窗口里那一段被收掉。
     * 同时钉住"能跳回任意久之前": 窗口之前的用户消息仍能按 seq 从日志翻页取回来。
     */
    [Fact]
    public void FromWindow_FoldsReplaceBeforeTheWindow_AndStillReachesOldHistory()
    {
        var root = NewRoot();
        try
        {
            var header = NewHeader(root);
            var log = BuildLog(header, ResidentFrom);
            WriteLog(root, header, log.Events);

            using var store = new JsonlSessionPersistence(root, warn: _ => { });
            var window = store.TryOpenWindow(header.Id, TailEvents);
            Assert.True(window is not null);
            Assert.True(window.Value.ResidentFrom > log.ShadowStart,
                $"precondition: the window starts at {window.Value.ResidentFrom}, the shadow at {log.ShadowStart}");

            var session = Session.FromWindow(header.Id, window.Value, header, 0);
            var nodes = session.SurfaceManager.Nodes;
            Assert.Contains(log.ReplaceSeq, nodes);
            Assert.DoesNotContain(nodes, seq => seq > log.ReplaceSeq);

            // 跳回任意久之前: 窗口之外的用户消息要从日志按页读回来。
            var firstUserMessage = log.Events.First(sessionEvent => sessionEvent.Data is UserMessagePayload).Seq;
            Assert.True(firstUserMessage < window.Value.ResidentFrom, "precondition: the target sits before the window");
            var restored = session.EventAt(firstUserMessage);
            Assert.NotNull(restored);
            Assert.IsType<UserMessagePayload>(restored.Data);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void UnreadableFrameViewStillDeclinesTheWindowSoTheCallerOpensItInFull()
    {
        var root = NewRoot();
        try
        {
            var header = NewHeader(root);
            var log = BuildLog(header, ResidentFrom);
            var path = WriteLog(root, header, log.Events);
            // 末帧被截断(活会话写到一半断电的样子): 帧视图读不出来, 这条失败路径仍然退回全量。
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Write))
                stream.SetLength(stream.Length - 32);

            var warnings = new List<string>();
            using var store = new JsonlSessionPersistence(root, warn: warnings.Add);
            Assert.Null(store.TryOpenWindow(header.Id, TailEvents));
            Assert.NotEmpty(warnings);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private sealed record WindowedLog(IReadOnlyList<SessionEvent> Events, long ReplaceSeq, long ShadowStart);

    /**
     * 造一份尾部带跨边界 replace 的日志: 前缀(seq &lt; residentFrom)的 surface 节点被尾窗里的
     * 一条 replace 整个收掉 —— 压缩过的会话就是这个形状。
     */
    private static WindowedLog BuildLog(SessionHeader header, long residentFrom)
    {
        var session = Session.Create(header.Id, null, header);
        var turn = 1;
        while (session.Seq < EventCount)
        {
            if (turn % 40 == 1)
            {
                session.Append(new RequestHeaderPayload(
                    new EpochHeader(new LlmCallConfig("deepseek-official", "deepseek-v4-flash")),
                    RequestHeaderReasons.Initial));
                session.Append(new RequestContextPayload("deepseek-official", "deepseek-v4-flash"));
            }
            session.Append(new TurnStartPayload(turn));
            session.Append(new StepStartPayload(turn, 1));
            session.Append(new AssistantChunkPayload(turn, 1, new StreamChunk.TextDelta(0, $"chunk {session.Seq} 行内容\n")));
            session.Append(new UserMessagePayload(MessageFactory.CreateUserText($"q{turn}")), new SurfaceOp.Append());
            session.Append(
                new AssistantMessagePayload(
                    turn,
                    1,
                    MessageFactory.CreateAssistantMessage([new TextBlock($"a{turn}")], "deepseek-official", "deepseek-v4-flash")),
                new SurfaceOp.Append(),
                [session.Seq - 2]);
            turn += 1;
        }

        var prefixNodes = session.SurfaceManager.Nodes.Where(seq => seq < residentFrom).ToList();
        var shadowStart = prefixNodes[0];
        var shadowEnd = prefixNodes[^1];
        var checkpoint = session.Append(
            new UserMessagePayload(MessageFactory.CreateUserMessage([new TextBlock("压缩摘要")], new PluginMessageSource("compact"))),
            new SurfaceOp.Replace(shadowStart, shadowEnd),
            prefixNodes);
        return new WindowedLog([.. session.SnapshotEvents()], checkpoint.Seq, shadowStart);
    }

    private static string NewRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), $"dsh-window-replace-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        return root;
    }

    private static SessionHeader NewHeader(string root) => new()
    {
        Version = SessionHeader.SessionFormatVersion,
        Id = SessionId.Create(Guid.NewGuid().ToString("N")),
        Cwd = Path.Combine(root, "workspace"),
        CreatedAt = 0,
        IsSeeded = false,
    };

    private static string WriteLog(string root, SessionHeader header, IReadOnlyList<SessionEvent> events)
    {
        using (var writer = new JsonlSessionPersistence(root).Create(header))
        {
            // 按落盘批次写: 每批一个帧, 现场会话就是这么长的。
            for (var offset = 0; offset < events.Count; offset += 500)
                writer.Append([.. events.Skip(offset).Take(500)]);
        }
        // 日志旁边的帧索引(<日志>.frames)不算日志本身: 它是派生文件, 伤害它不等于伤害日志。
        return Directory
            .EnumerateFiles(root, $"session{JsonlLayout.LogSuffix(JsonlCompression.Zstd)}", SearchOption.AllDirectories)
            .Single();
    }
}