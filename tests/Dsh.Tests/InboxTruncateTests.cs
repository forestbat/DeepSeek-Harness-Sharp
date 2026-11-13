using Dsh.Agent;
using Dsh.Core;
using Dsh.Interaction;
using Dsh.Llm;
using Dsh.Runtime;
using Dsh.SessionGraph;

namespace Dsh.Tests;

/**
 * 收件箱是会话日志的派生视图: 撤回(截断)把日志改短之后, 内存里的待发列表必须跟着重建,
 * 否则日志与内存分叉 —— 日志还记着"这条已入队", 内存里已经发过了。
 */
public sealed class InboxTruncateTests
{
    private static Session NewSession()
    {
        var id = SessionId.Create(Guid.NewGuid().ToString("N"));
        return Session.Create(id, header: new SessionHeader
        {
            Version = SessionHeader.SessionFormatVersion,
            Id = id,
            CreatedAt = 1700000000000,
            IsSeeded = false,
        });
    }

    private static UserMessage Text(string text) => MessageFactory.CreateUserText(text);

    /**
     * 真实顺序: 入队(seq0) → TurnStart(seq1) → 取走(seq2) → 用户消息(seq3)。
     * 撤回到回合之前(boundary=1)会丢掉"取走"那条事件, 只留"入队"。
     */
    [Fact]
    public void Truncation_BeforeClaim_DoesNotResurrectTheWithdrawnMessage()
    {
        var session = NewSession();
        var inbox = new Inbox(session, new InboxNotifications());
        inbox.Splice(InboxTargets.NextTurn, int.MaxValue, 0, [Text("withdrawn")]);
        session.Append(new TurnStartPayload(1));
        inbox.Splice(InboxTargets.NextTurn, 0, 1, []);
        Assert.False(inbox.HasPending);

        Assert.True(session.TryTruncateTo(1));

        Assert.False(inbox.HasPending);
        // 重建出来的收件箱(恢复会话/重建 agent 时看到的就是这个)必须与日志一致。
        Assert.False(new Inbox(session, new InboxNotifications()).HasPending);
    }

    /** 撤回后再发新消息: 落盘日志重放出来只能有新消息, 不能把撤回那条一起带出来。 */
    [Fact]
    public void Truncation_ThenNewMessage_ReplaysOnlyTheNewMessage()
    {
        var session = NewSession();
        var inbox = new Inbox(session, new InboxNotifications());
        inbox.Splice(InboxTargets.NextTurn, int.MaxValue, 0, [Text("withdrawn")]);
        session.Append(new TurnStartPayload(1));
        inbox.Splice(InboxTargets.NextTurn, 0, 1, []);

        Assert.True(session.TryTruncateTo(1));
        inbox.Splice(InboxTargets.NextTurn, int.MaxValue, 0, [Text("next")]);

        var replayed = new Inbox(session, new InboxNotifications());
        var pending = Assert.Single(replayed.NextTurn);
        Assert.Equal("next", ((TextBlock)pending.Content[0]).Text);
    }

    /**
     * 撤回的清理必须写回日志: 进程重启后由持久化日志新建的 Inbox 没有"截断前快照",
     * 只靠内存剔除的话, 恢复会话会把撤回那条重新捞出来。
     */
    [Fact]
    public void Truncation_PersistsTheCleanup_SoARestoredInboxStaysClean()
    {
        var session = NewSession();
        var inbox = new Inbox(session, new InboxNotifications());
        inbox.Splice(InboxTargets.NextTurn, int.MaxValue, 0, [Text("withdrawn")]);
        session.Append(new TurnStartPayload(1));
        inbox.Splice(InboxTargets.NextTurn, 0, 1, []);

        Assert.True(session.TryTruncateTo(1));

        // 模拟重启: 只用日志里的事件重建一个全新 Inbox。
        var restored = new Inbox(session, new InboxNotifications());
        Assert.Null(restored.NextTurn.FirstOrDefault());
    }

    /**
     * 用户报的路径: 右击最新用户消息 → 回退到此消息 → 再发一条。
     * 真实顺序由 agent loop 产生(入队 → TurnStart → 取走 → 用户消息), 所以这里直接跑真实 agent。
     */
    [Fact]
    public async Task RevertThroughTheCommand_ThenSendingAgain_DeliversOnlyTheNewMessage()
    {
        var ctx = new Context();
        _ = new SessionStore(ctx);
        var agents = new AgentRegistry(ctx);
        _ = new SystemPrompt(ctx, new SystemPromptConfig());
        _ = new AgentLoop(ctx);
        var commands = CommandsService.Register(ctx);
        TimestampCommand.Register(ctx);
        var handle = await agents.Create(new CreateAgentOptions(
            SessionId.Create($"session-{Guid.NewGuid():N}"),
            Path.GetTempPath(),
            new AgentOptions("provider", "model")));
        var agent = (AgentLoopAgent)handle.Agent;
        await agent.WhenIdle();

        agent.Followup(Text("withdrawn question"));
        var seq = SessionTimeline.UserMessages(agent.Session).Single().Seq;

        var reverted = await commands.Execute(agent, $"/timestamp revert #{seq}", TestContext.Current.CancellationToken);
        Assert.IsType<CommandResult.Success>(reverted!.Result);

        agent.Followup(Text("replacement question"));
        // 新消息已被本回合取走(agent 空闲即取), 关键是被撤回那条不能借尸还魂。
        Assert.DoesNotContain(agent.Inbox.NextTurn,
            message => ((TextBlock)message.Content[0]).Text == "withdrawn question");
        Assert.DoesNotContain(agent.Inbox.NextStep,
            message => ((TextBlock)message.Content[0]).Text == "withdrawn question");
        Assert.Equal(["replacement question"],
            SessionTimeline.UserMessages(agent.Session).Select(entry => entry.Text));
    }
}