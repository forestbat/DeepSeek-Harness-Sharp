using Dsh.Agent;
using Dsh.Core;
using Dsh.Llm;

namespace Dsh.Tests;

/**
 * 恢复时的"中断轮收尾"与请求投影的工具调用配对。
 *
 * 现场故障(会话 33bd85ac, 2026-11-22): 落盘在 23:52 停写, 磁盘尾部是一条悬空 `tool/call`;
 * 00:34 恢复时窗口从 268766 起, 而该回合的 `turn/start` 在 237626 —— 落在窗口之外。
 * 旧实现以"窗口里看得到 turn/start"为前提判断未闭合回合, 于是**一条合成结果都不补**,
 * 请求以"assistant 带 tool_calls、后面没有 tool 消息"结尾, 供应商回
 * `INVALID_REQUEST: An assistant message with 'tool_calls' must be followed by tool messages…`。
 *
 * 这里的用例把这条链定死: 回合起点在窗口外也要收尾, 且请求里必须配对成立。
 */
public sealed class InterruptedTailRepairTests
{
    [Fact]
    public void WindowedResume_PastTheTurnStart_ClosesTheDanglingToolCall()
    {
        var full = BuildSessionWithDanglingCall("interrupted-tail");
        var events = full.SnapshotEvents();
        // 窗口从"最后一步的助手消息"起: turn/start、step/start 都在窗口之外。
        var residentFrom = events.Last(sessionEvent => sessionEvent.Data is AssistantMessagePayload).Seq;
        var resident = events.Skip((int)residentFrom).ToList();
        Assert.DoesNotContain(resident, sessionEvent => sessionEvent.Data is TurnStartPayload);

        var closers = SessionRepair.InterruptedTurnClosers(resident);

        var toolResult = Assert.Single(closers, sessionEvent => sessionEvent.Type == SessionEventTypes.ToolResult);
        var result = Assert.IsType<ToolResultPayload>(toolResult.Data);
        Assert.Equal(CallId, result.Message.ToolSource.CallId.Value);
        Assert.Equal(2, result.Turn);
        Assert.Equal(SessionRepair.ToolOutcomeUnknown, result.Error?.Code);
        var turnEnd = Assert.Single(closers, sessionEvent => sessionEvent.Type == SessionEventTypes.TurnEnd);
        Assert.Equal(new TurnEndReason.Interrupted(), Assert.IsType<TurnEndPayload>(turnEnd.Data).Reason);
        Assert.Equal(2, Assert.IsType<TurnEndPayload>(turnEnd.Data).Turn);

        // 恢复出来的会话: 请求里那条 tool_calls 后面必须紧跟它的 tool 结果。
        var windowed = Session.FromWindow(
            full.Id,
            // 收尾事件是恢复时补上的, 所以总数也要把它们算进去。
            new SessionWindow(residentFrom, events.Count + closers.Count, [.. resident, .. closers],
                (offset, count) => [.. events.Skip((int)offset).Take(count)]),
            full.Header,
            0);
        var messages = windowed.DeriveMessages();
        AssertPaired(messages);
        Assert.Equal(2, SessionRepair.LastTurnOf(windowed));
    }

    [Fact]
    public void FullyResidentInterruptedTurn_StillProducesTheSameClosers()
    {
        var session = BuildSessionWithDanglingCall("interrupted-resident");
        var closers = SessionRepair.InterruptedTurnClosers(session.SnapshotEvents());
        Assert.Single(closers, sessionEvent => sessionEvent.Type == SessionEventTypes.ToolResult);
        Assert.Single(closers, sessionEvent => sessionEvent.Type == SessionEventTypes.StepEnd);
        Assert.Equal(2, Assert.IsType<TurnEndPayload>(Assert.Single(closers, e => e.Type == SessionEventTypes.TurnEnd).Data).Turn);
    }

    [Fact]
    public void ClosedTurn_NeedsNoClosers()
    {
        var session = BuildSessionWithDanglingCall("closed-turn", closeTurn: true);
        Assert.Empty(SessionRepair.InterruptedTurnClosers(session.SnapshotEvents()));
    }

    /**
     * 只有"未闭合的步"也算证据: 进程在两步之间死掉时, 窗口里能看到 step/start 却看不到 turn/end,
     * 恢复时必须补 turn/end, 否则界面会一直显示这一轮还在跑。
     */
    [Fact]
    public void OpenStepWithoutDanglingCalls_StillClosesTheTurn()
    {
        var session = BuildSession("open-step");
        session.Append(new TurnStartPayload(2));
        var stepStart = session.Append(new StepStartPayload(2, 1));
        var events = session.SnapshotEvents();
        var residentFrom = stepStart.Seq;
        var resident = events.Skip((int)residentFrom).ToList();

        var closers = SessionRepair.InterruptedTurnClosers(resident);

        Assert.DoesNotContain(closers, sessionEvent => sessionEvent.Type == SessionEventTypes.ToolResult);
        Assert.Equal(2, Assert.IsType<TurnEndPayload>(Assert.Single(closers, e => e.Type == SessionEventTypes.TurnEnd).Data).Turn);
    }

    /** fork 种子前缀是冻结历史: 它里面的悬空调用不补合成事件。 */
    [Fact]
    public void InheritedSeedTail_IsLeftAlone()
    {
        var session = BuildSessionWithDanglingCall("inherited-tail");
        var events = session.SnapshotEvents();
        Assert.Empty(SessionRepair.InterruptedTurnClosers(events, inheritedEventCount: events.Count));
    }

    /**
     * 窗口化恢复 + fork: 第二个参数是"传入列表内的偏移", 由调用方按 (inherited - ResidentFrom) 算。
     * 窗口若整个落在种子前缀里, 那条悬空调用属于冻结历史, 不能补。
     */
    [Fact]
    public void WindowedForkPrefix_StaysFrozen()
    {
        var seed = BuildSessionWithDanglingCall("fork-seed").SnapshotEvents().ToList();
        var resident = seed.Select(sessionEvent => sessionEvent with { SourceEventSeqs = null }).ToList();

        Assert.Empty(SessionRepair.InterruptedTurnClosers(resident, inheritedEventCount: seed.Count));
        // 偏移写错(按 0 算)时才会去补前缀里那条悬空调用 —— 这正是复核指出的那个坑。
        Assert.NotEmpty(SessionRepair.InterruptedTurnClosers(resident, inheritedEventCount: 0));
    }

    /** 补过一次之后再恢复一次: 尾部已经是 turn/end, 不能再补(补了就变成每次打开都多三条)。 */
    [Fact]
    public void SecondResume_AddsNothing()
    {
        var session = BuildSessionWithDanglingCall("second-resume");
        var events = session.SnapshotEvents();
        var first = SessionRepair.InterruptedTurnClosers(events);
        Assert.Equal(3, first.Count);
        Assert.Empty(SessionRepair.InterruptedTurnClosers([.. events, .. first]));
    }

    private const string CallId = "call_missing_result";

    private static void AssertPaired(IReadOnlyList<Message> messages)
    {
        var pending = new List<ToolCallId>();
        foreach (var message in messages)
        {
            var results = message.Content.OfType<ToolResultBlock>().ToList();
            foreach (var result in results)
                Assert.True(pending.Remove(result.ToolCallId), $"孤立工具结果: {result.ToolCallId}");
            if (results.Count > 0)
                continue;
            Assert.Empty(pending);
            foreach (var call in message.Content.OfType<ToolCallBlock>())
                pending.Add(call.Id);
        }
        Assert.Empty(pending);
    }

    /**
     * 造一个"最后一步发了工具调用、然后进程没了"的会话。
     * closeTurn 为真时把 step/turn 正常收尾, 用于验证闭合回合不需要补齐。
     */
    private static Session BuildSessionWithDanglingCall(string id, bool closeTurn = false)
    {
        var session = BuildSession(id);
        session.Append(new TurnStartPayload(2));
        session.Append(new StepStartPayload(2, 1));
        session.Append(new UserMessagePayload(MessageFactory.CreateUserText("继续")), new SurfaceOp.Append());
        var callIds = new[] { ToolCallId.Create(CallId) };
        var assistant = MessageFactory.CreateAssistantMessage(
            [.. callIds.Select(callId => (ContentBlock)new ToolCallBlock(callId, "read", "{}"))], "test", "test-model");
        session.Append(new AssistantMessagePayload(2, 1, assistant), new SurfaceOp.Append());
        foreach (var callId in callIds)
            session.Append(new ToolCallPayload(2, 1, callId, "read", "{}"));
        if (closeTurn)
        {
            session.Append(new StepEndPayload(2, 1));
            session.Append(new TurnEndPayload(2, new TurnEndReason.Completed()));
        }
        return session;
    }

    private static Session BuildSession(string id)
    {
        var header = new SessionHeader
        {
            Id = SessionId.Create(id),
            Version = SessionHeader.SessionFormatVersion,
            Cwd = Environment.CurrentDirectory,
            CreatedAt = 1_700_000_000_000,
            IsSeeded = false,
        };
        var session = Session.Create(header.Id, null, header);
        session.Append(new TurnStartPayload(1));
        session.Append(new StepStartPayload(1, 1));
        session.Append(new UserMessagePayload(MessageFactory.CreateUserText("第一轮")), new SurfaceOp.Append());
        session.Append(new AssistantMessagePayload(
            1, 1, MessageFactory.CreateAssistantMessage([new TextBlock("第一轮回答")], "test", "test-model")), new SurfaceOp.Append());
        session.Append(new StepEndPayload(1, 1));
        session.Append(new TurnEndPayload(1, new TurnEndReason.Completed()));
        return session;
    }
}
