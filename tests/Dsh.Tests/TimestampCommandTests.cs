using Dsh.Core;
using Dsh.Interaction;
using Dsh.Llm;
using Dsh.Persistence;
using Dsh.Runtime;
using Dsh.SessionGraph;
using Dsh.Agent;

namespace Dsh.Tests;

/**
 * `/timestamp` 是 GUI 与 TUI 共用的会话时间线命令: 列候选、跳转、按检查点回退、派生新会话。
 * 这些用例直接打命令层, 不经过任何界面。
 */
public sealed class TimestampCommandTests
{
    [Fact]
    public async Task List_ShowsUserMessagesOnly()
    {
        using var fixture = new Fixture();
        fixture.AppendUser("first question");
        fixture.AppendPluginMessage("tool result pretending to be a message");
        fixture.AppendUser("second question");

        var result = Assert.IsType<CommandResult.Success>(await fixture.RunAsync("/timestamp"));

        var lines = result.Text!.Split('\n');
        Assert.Equal(2, lines.Length);
        Assert.Contains("first question", lines[0]);
        Assert.Contains("second question", lines[1]);
        Assert.DoesNotContain(lines, line => line.Contains("pretending"));
    }

    [Fact]
    public async Task List_StripsSystemRemindersFromPreview()
    {
        using var fixture = new Fixture();
        fixture.AppendUser("real question<system-reminder>noise</system-reminder>");

        var result = Assert.IsType<CommandResult.Success>(await fixture.RunAsync("/timestamp"));

        Assert.Contains("real question", result.Text);
        Assert.DoesNotContain("noise", result.Text);
    }

    [Fact]
    public async Task Jump_ReportsTheTargetWithoutTouchingTheSession()
    {
        using var fixture = new Fixture();
        var seq = fixture.AppendUser("only question");

        var result = Assert.IsType<CommandResult.Success>(await fixture.RunAsync($"/timestamp jump #{seq}"));

        Assert.Contains($"jump #{seq}", result.Text);
        var timeline = SessionTimeline.UserMessages(fixture.Agent.Session).Select(entry => entry.Text).ToList();
        Assert.Equal(["only question"], timeline);
    }

    /** 回退停在"这条消息所属回合之前": 消息本身连同它那一回合都不留在会话里(原文回输入区)。 */
    [Fact]
    public async Task Revert_TruncatesBeforeTheTurnThatOwnsTheChosenMessage()
    {
        using var fixture = new Fixture();
        fixture.AppendUser("keep me");
        var seq = fixture.AppendUser("drop me");
        fixture.AppendUser("drop me too");

        var result = Assert.IsType<CommandResult.Success>(await fixture.RunAsync($"/timestamp revert #{seq}"));

        Assert.Contains("back in the input area", result.Text);
        var remaining = SessionTimeline.UserMessages(fixture.Agent.Session).Select(entry => entry.Text).ToList();
        Assert.Equal(["keep me"], remaining);
        // 整个回合都要走(剩下唯一一个 TurnStart), 且日志不能以悬空的 TurnStart 收尾:
        // 那正是界面卡在"正在运行"、停止按钮又没反应的原因。
        var events = fixture.Agent.Session.SnapshotEvents();
        Assert.Single(events, sessionEvent => sessionEvent.Data is TurnStartPayload);
        Assert.IsNotType<TurnStartPayload>(events[^1].Data);
    }

    /** 分叉同样停在回合之前: 被选中那条不进新会话, 新会话标题沿用父会话标题。 */
    [Fact]
    public async Task Fork_StopsBeforeTheChosenTurn_AndTitlesFromTheParent()
    {
        using var fixture = new Fixture();
        fixture.Agent.Session.Rename("父会话标题");
        fixture.AppendUser("first");
        var seq = fixture.AppendUser("second");

        var result = Assert.IsType<CommandResult.Success>(await fixture.RunAsync($"/timestamp fork #{seq}"));

        Assert.Contains("forked ->", result.Text);
        var forkedId = SessionId.Create(result.Text!["forked -> ".Length..].Split(':')[0].Trim());
        using var handle = fixture.Persistence.Open(forkedId, SessionAccess.Read);
        var events = handle.Read();
        Assert.Equal(["first"], SessionTimeline.FromEvents(events).Select(entry => entry.Text));
        var header = fixture.Persistence.Stat(forkedId)!.Header;
        Assert.Equal("fork of 父会话标题 @", header.Title![.."fork of 父会话标题 @".Length]);
        // 种子带着父会话的旧时间, 但新会话的活动时间必须从创建时刻算起。
        Assert.Equal(header.CreatedAt, events[^1].Time);
    }

    [Fact]
    public async Task UnknownTarget_IsRejectedWithoutChangingTheSession()
    {
        using var fixture = new Fixture();
        fixture.AppendUser("only");

        var rejected = Assert.IsType<CommandResult.Error>(await fixture.RunAsync("/timestamp jump #999"));
        var malformed = Assert.IsType<CommandResult.Error>(await fixture.RunAsync("/timestamp jump later"));

        Assert.Contains("#999", rejected.Text);
        Assert.Contains("invalid timeline target", malformed.Text);
        Assert.Single(SessionTimeline.UserMessages(fixture.Agent.Session));
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _root = Path.Combine(
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../artifacts020/test-homes")),
            $"timestamp-{Guid.NewGuid():N}");

        public Fixture()
        {
            Directory.CreateDirectory(_root);
            Ctx = new Context();
            _ = new AgentRegistry(Ctx);
            Commands = CommandsService.Register(Ctx);
            Persistence = new JsonlSessionPersistence(_root, compression: JsonlCompression.None);
            _persistenceRegistration = Ctx.Provide(ISessionPersistence.ServiceName, Persistence);
            Agent = new FakeAgent(Ctx);
            _registration = TimestampCommand.Register(Ctx);
        }

        public Context Ctx { get; }
        public CommandsService Commands { get; }
        public JsonlSessionPersistence Persistence { get; }
        public FakeAgent Agent { get; }
        private readonly IDisposable _registration;
        private readonly EffectHandle _persistenceRegistration;

        /** 与生产的顺序一致: 先 TurnStart 再 UserMessage(回退/分叉都按回合边界切)。 */
        public long AppendUser(string text)
        {
            Agent.Session.Append(new TurnStartPayload(Agent.Session.SnapshotEvents().Count(IsTurnStart) + 1));
            var sessionEvent = Agent.Session.Append(
                new UserMessagePayload(MessageFactory.CreateUserText(text)), new SurfaceOp.Append());
            return sessionEvent.Seq;
        }

        private static bool IsTurnStart(SessionEvent sessionEvent) => sessionEvent.Data is TurnStartPayload;

        /** 插件来源的消息不算"我说过的话", 列时间线时必须被过滤掉。 */
        public void AppendPluginMessage(string text)
        {
            var message = MessageFactory.CreateUserText(text) with
            {
                Source = new PluginMessageSource("test-plugin", "notice"),
            };
            Agent.Session.Append(new UserMessagePayload(message), new SurfaceOp.Append());
        }

        public async Task<CommandResult> RunAsync(string line)
        {
            var execution = await Commands.Execute(Agent, line, TestContext.Current.CancellationToken);
            Assert.NotNull(execution);
            return execution.Result;
        }

        public void Dispose()
        {
            _persistenceRegistration.Dispose();
            _registration.Dispose();
            Persistence.Dispose();
            TempTree.Delete(_root);
        }
    }

    private sealed class FakeAgent : IAgent
    {
        public FakeAgent(Context ctx)
        {
            Ctx = ctx;
            var id = SessionId.Create($"session-{Guid.NewGuid():N}");
            Session = Session.Create(id, null, new SessionHeader
            {
                Version = SessionHeader.SessionFormatVersion,
                Id = id,
                CreatedAt = 0,
                Cwd = Path.GetTempPath(),
                IsSeeded = false,
            });
        }

        public SessionId Id => Session.Id;
        public Session Session { get; }
        public ScopeKey ScopeKey { get; } = new();
        public Context Ctx { get; }
        public AgentStatus Status => AgentStatus.Idle;
        public AgentOptions Options { get; } = new();

        public void Cancel(AgentCancelCause cause, bool keepInbox = false)
        {
        }

        public void Followup(UserMessage message)
        {
        }

        public void Steer(UserMessage message)
        {
        }

        public void Inject(UserMessage message)
        {
        }

        public void Send(UserMessage message, string target, bool wakeup)
        {
        }

        public Task WhenIdle() => Task.CompletedTask;


        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
