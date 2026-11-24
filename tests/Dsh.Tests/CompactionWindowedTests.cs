using Dsh.Core;
using Dsh.Compaction;
using Dsh.Llm;
using Dsh.Runtime;

namespace Dsh.Tests;

/**
 * 窗口化会话(重启后只常驻尾部一段)的上下文与压缩语义。
 *
 * 现场故障: /compact 先报 "token-meter surface does not match the current session surface",
 * 修掉之后又报 "tool-pairing balance: tool/result ... has no matching tool-call"。
 * 根因不是"表面坏了", 而是**窗口化恢复把表面也一起缩短了**: 会话的 surface 只折常驻窗口,
 * 而 surface 就是发给模型的那份消息序列(Session.DeriveMessages ← SurfaceManager.Nodes,
 * AgentLoopAgent.BuildRequest 用的正是它)。于是恢复之后模型丢掉窗口之前的全部上下文,
 * 压缩也只能压窗口内那一段。
 *
 * 正确语义: 窗口只决定"哪些事件对象常驻内存", **表面始终覆盖整条日志**(前缀按页取);
 * 压缩把被折叠的那一段在表面上替换成一个摘要节点, 所以反复压缩不会让上下文膨胀。
 */
public sealed class CompactionWindowedTests
{
    [Fact]
    public void WindowedResume_KeepsTheWholeContextInTheRequest()
    {
        var ctx = new Context();
        var meter = new TokenMeter(ctx);
        var full = BuildSession("windowed-context", turns: 6);
        var events = full.SnapshotEvents();
        // 常驻窗口从第 4 回合起: 第 1–3 回合只在磁盘上, 靠页读取。
        var residentFrom = events.First(e => e.Data is TurnStartPayload { Turn: 4 }).Seq;
        var windowed = Session.FromWindow(
            full.Id,
            new SessionWindow(residentFrom, events.Count, [.. events.Skip((int)residentFrom)],
                (offset, count) => [.. events.Skip((int)offset).Take(count)]),
            full.Header,
            0);

        // 表面覆盖整条日志, 与整段恢复得到的表面一致。
        Assert.Equal(full.SurfaceManager.Nodes, windowed.SurfaceManager.Nodes);
        // 请求里的消息也必须包含窗口之前的回合 —— 少一条就是模型丢了上下文。
        var messages = windowed.DeriveMessages();
        Assert.Equal(full.DeriveMessages().Count, messages.Count);
        Assert.Contains(messages, message => TextOf(message).Contains("turn-1", StringComparison.Ordinal));
        Assert.Contains(messages, message => TextOf(message).Contains("turn-6", StringComparison.Ordinal));

        // 计量与选区都工作在同一份完整表面上。
        var measurement = meter.Measure(windowed);
        Assert.Equal(windowed.SurfaceManager.Nodes, measurement.Nodes.Select(node => node.Seq));
        var range = CompactionRegion.SelectCompactableRange(windowed, measurement, retainTokens: 1, tailTurns: 1);
        Assert.NotNull(range);
        Assert.True(range.Start <= residentFrom,
            $"the compactable span must reach into the prefix (start {range.Start}, window {residentFrom})");
    }

    /**
     * 会话被**回退**之后(表面收掉一段、条数变少), 计量器必须自愈: 以会话表面为准重建回放状态。
     *
     * 现场故障: /compact 报 "compaction: token-meter surface does not match the current session surface" ——
     * 计量器的节点表过期了, 它只是缓存, 缓存过期不该让用户按不了压缩。
     */
    [Fact]
    public void Measure_RebuildsItsSurface_AfterTheSessionWasReverted()
    {
        var ctx = new Context();
        var meter = new TokenMeter(ctx);
        var session = BuildSession("meter-truncate", turns: 6);
        var before = meter.Measure(session);
        Assert.Equal(session.SurfaceManager.Nodes, before.Nodes.Select(node => node.Seq));

        // 回退到第 4 回合开始: 表面被收掉一段, 计量器的回放状态因此过期。
        var boundary = session.SnapshotEvents().First(sessionEvent => sessionEvent.Data is TurnStartPayload { Turn: 4 }).Seq;
        Assert.NotNull(session.Revert(boundary));

        var after = meter.Measure(session);
        Assert.Equal(session.SurfaceManager.Nodes, after.Nodes.Select(node => node.Seq));
        Assert.NotNull(CompactionRegion.SelectCompactableRange(session, after, retainTokens: 1, tailTurns: 1));
    }

    /**
     * 反复压缩不会让上下文膨胀: 每一次压缩都把它覆盖范围内的节点(包括上一次的摘要)替换成一个新摘要节点。
     */
    [Fact]
    public void RepeatedCompactions_CollapseEarlierSummariesIntoTheLatestOne()
    {
        var session = BuildSession("repeated-compaction", turns: 6);
        var surface = session.SurfaceManager.Nodes;
        Assert.Equal(12, surface.Count);

        // 第一次压缩: 折叠第 1–2 回合(4 个表面节点) → 12 - 4 + 1 = 9
        var firstSummary = AppendSummary(session, surface[0], surface[3]);
        var afterFirst = session.SurfaceManager.Nodes;
        Assert.Equal(9, afterFirst.Count);
        Assert.Equal(firstSummary, afterFirst[0]);

        // 第二次压缩: 把上一次的摘要连同第 3 回合一起折叠 → 9 - 3 + 1 = 7
        var secondSummary = AppendSummary(session, afterFirst[0], afterFirst[2]);
        var afterSecond = session.SurfaceManager.Nodes;
        Assert.Equal(7, afterSecond.Count);
        Assert.Equal(secondSummary, afterSecond[0]);
        Assert.DoesNotContain(firstSummary, afterSecond);

        // 上下文里只剩最新摘要 + 第 4–6 回合的原文, 被折叠的原文不再进入请求。
        var messages = session.DeriveMessages();
        Assert.Equal(7, messages.Count);
        Assert.Contains(messages, message => TextOf(message).Contains("summary", StringComparison.Ordinal));
        Assert.DoesNotContain(messages, message => TextOf(message).Contains("turn-1", StringComparison.Ordinal));
        Assert.DoesNotContain(messages, message => TextOf(message).Contains("turn-2", StringComparison.Ordinal));
        Assert.Contains(messages, message => TextOf(message).Contains("turn-4", StringComparison.Ordinal));
    }

    /** 追加一条覆盖 [start, end] 的摘要节点(与压缩提交的事件形态一致: replace + 来源序号)。 */
    private static long AppendSummary(Session session, long start, long end)
    {
        var shadowed = session.SurfaceManager.Nodes
            .SkipWhile(seq => seq != start)
            .TakeWhile(seq => seq != end)
            .Append(end)
            .ToList();
        var message = MessageFactory.CreateUserText($"summary of {shadowed.Count} node(s)");
        return session.Append(new UserMessagePayload(message), new SurfaceOp.Replace(start, end), shadowed).Seq;
    }

    private static string TextOf(Llm.Message message)
        => string.Join("\n", message.Content.OfType<TextBlock>().Select(block => block.Text));

    /** 造一个带回合结构的会话: 每回合 1 个用户消息 + 1 条助手消息。 */
    private static Session BuildSession(string id, int turns)
    {
        var session = NewSession(id);
        for (var turn = 1; turn <= turns; turn += 1)
        {
            session.Append(new TurnStartPayload(turn));
            session.Append(new StepStartPayload(turn, 1));
            session.Append(new UserMessagePayload(MessageFactory.CreateUserText($"turn-{turn}")), new SurfaceOp.Append());
            var assistant = MessageFactory.CreateAssistantMessage([new TextBlock($"answer-{turn}")], "test", "test-model");
            session.Append(new AssistantMessagePayload(turn, 1, assistant), new SurfaceOp.Append());
            session.Append(new StepEndPayload(turn, 1));
            session.Append(new TurnEndPayload(turn, new TurnEndReason.Completed()));
        }
        return session;
    }

    private static Session NewSession(string id)
    {
        var header = new SessionHeader
        {
            Id = SessionId.Create(id),
            Version = SessionHeader.SessionFormatVersion,
            Cwd = Environment.CurrentDirectory,
            CreatedAt = 1_700_000_000_000,
            IsSeeded = false,
        };
        return Session.Create(header.Id, null, header);
    }
}