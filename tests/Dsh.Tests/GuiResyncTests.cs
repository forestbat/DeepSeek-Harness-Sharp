using System.Runtime.CompilerServices;
using Dsh.Core;
using Dsh.Fleet;
using Dsh.Gui.Services;
using Dsh.Gui.ViewModels;
using Dsh.Llm;
using Dsh.RemoteHost;

namespace Dsh.Tests;

/**
 * 事件流里出现跳号时的契约: **尾巴必须继续流**。
 *
 * 现场故障(真实会话 33bd85ac, 107,718 条): 某一段事件没到手, 界面把整批事件丢掉并重取整段历史,
 * 于是同一批里的 turn/end 也没落地 —— 表现是"停止按钮按不动""底部停在旧内容""发送后跳到中段",
 * 而且重取每次都断在同一处, 每秒复现一次。
 *
 * 用例只依赖一个前提: 丢掉实时流里的某几条(宿主本身仍完整)。这是现场真实发生的事(补齐取回半段),
 * 不依赖分页预算的具体数值。
 */
[Collection(SerialProcessCollection.Name)]
public sealed class GuiResyncTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /** 实时流中途缺了几条: 后面的正文、turn/end 都要照常落地, 并且只登记缺口、不重取整段历史。 */
    [Fact(Timeout = 180000)]
    public async Task StreamEventsAfterAGap_KeepTheTailAndClearBusy() => await HeadlessGui.RunAsync(async () =>
    {
        var probe = new DroppingFollowProbe();
        await using var host = await HarnessHostFixture.CreateAsync("rs1", Ct, decorateBackend: probe.Decorate);
        var session = host.Environment.Agent.Session;
        var id = session.Id.Value;
        for (var turn = 1; turn <= 3; turn += 1)
            AppendTurn(session, turn, $"第{turn}轮");
        await FlushAsync(host);

        var chat = await HostChatSession.OpenAsync(Fleet(host), session.Header.Cwd!, id, Ct, NewCache());
        try
        {
            await using var viewModel = new MainViewModel(host.Environment.App, chat);
            await GuiTestEnvironment.WaitForAsync(
                viewModel.Messages,
                () => UserMessages(viewModel).Count == 3,
                () => GuiTestEnvironment.DescribeChatState(viewModel));
            var snapshotsBefore = probe.SnapshotRequests;

            // 第 4 轮: 用户消息那条事件在实时流里丢掉(宿主那边仍在), 客户端于是看到一次跳号。
            // 选它而不是 turn/start, 是因为补洞要能看见效果: 补回来的用户消息必须排在已经渲染出来的助手回复**前面**。
            var dropFrom = session.Seq + 2;
            probe.Drop(dropFrom, dropFrom + 1);
            AppendTurn(session, 4, "第四轮");
            await FlushAsync(host);

            // 尾巴继续流: 第 4 轮的助手回复必须出现(整批丢掉的话它永远不会来)。
            await GuiTestEnvironment.WaitForAsync(
                viewModel.Messages,
                () => viewModel.Messages.Any(message => message.IsAssistant && message.BodyText.Contains("第四轮", StringComparison.Ordinal)),
                () => GuiTestEnvironment.DescribeChatState(viewModel));
            // 补洞: 缺的那条按序号插回原位, 而且状态行回到正常。
            await GuiTestEnvironment.WaitForAsync(
                viewModel.Messages,
                () => UserMessages(viewModel).Any(message => message.BodyText.Contains("第四轮", StringComparison.Ordinal)),
                () => GuiTestEnvironment.DescribeChatState(viewModel));
            var messages = viewModel.Messages.ToList();
            var userAt = messages.FindIndex(message => message.IsUser && message.BodyText.Contains("第四轮", StringComparison.Ordinal));
            var assistantAt = messages.FindIndex(message => message.IsAssistant && message.BodyText.Contains("第四轮", StringComparison.Ordinal));
            Assert.True(userAt >= 0 && assistantAt >= 0 && userAt < assistantAt,
                $"the filled message must be inserted before the reply, got user@{userAt} assistant@{assistantAt}");
            // turn/end 落地 => 不再卡在"会话进行中"。
            Assert.False(viewModel.IsBusy, "the turn must end even when its first events were dropped");
            // 只补缺口, 不重取整段历史。
            Assert.InRange(probe.SnapshotRequests - snapshotsBefore, 0, 4);
            Assert.Equal("准备就绪", viewModel.StatusText);
        }
        finally
        {
            chat.Dispose();
        }
    });

    /** 连续几轮都缺: 每次只登记、不重取 —— 否则"每次重取都断在同一处"就是死循环。 */
    [Fact(Timeout = 180000)]
    public async Task GapDoesNotReloadTheWholeHistory() => await HeadlessGui.RunAsync(async () =>
    {
        var probe = new DroppingFollowProbe();
        await using var host = await HarnessHostFixture.CreateAsync("rs2", Ct, decorateBackend: probe.Decorate);
        var session = host.Environment.Agent.Session;
        var id = session.Id.Value;
        AppendTurn(session, 1, "第一轮");
        await FlushAsync(host);

        var chat = await HostChatSession.OpenAsync(Fleet(host), session.Header.Cwd!, id, Ct, NewCache());
        try
        {
            await using var viewModel = new MainViewModel(host.Environment.App, chat);
            await GuiTestEnvironment.WaitForAsync(
                viewModel.Messages,
                () => UserMessages(viewModel).Count == 1,
                () => GuiTestEnvironment.DescribeChatState(viewModel));
            var snapshotsBefore = probe.SnapshotRequests;

            for (var turn = 2; turn <= 4; turn += 1)
            {
                var dropFrom = session.Seq;
                probe.Drop(dropFrom, dropFrom + 1);
                AppendTurn(session, turn, $"第{turn}轮");
            }
            await FlushAsync(host);

            await GuiTestEnvironment.WaitForAsync(
                viewModel.Messages,
                () => UserMessages(viewModel).Any(message => message.BodyText.Contains("第4轮", StringComparison.Ordinal)),
                () => GuiTestEnvironment.DescribeChatState(viewModel));
            // 三处缺口各补一次(每处一个区间请求), 但绝不是"整段重取一次接一次"那种风暴。
            Assert.InRange(probe.SnapshotRequests - snapshotsBefore, 0, 6);
            Assert.False(viewModel.IsBusy);
        }
        finally
        {
            chat.Dispose();
        }
    });

    /**
     * 被撤回的区间不许出现在界面上 —— 尤其不许被"迟到补洞"捡回来。
     *
     * 宿主在服务区间取数时已经按 surface 过滤掉回退过的节点, 客户端这一层再按序号区间兜一次:
     * 回退后重放/补洞/缓存任何一条路径把那段内容送进来都不许渲染(判据是"序号在不在回退区间里",
     * 不是"游标恰好走过")。
     */
    [Fact(Timeout = 180000)]
    public async Task RevertedRange_IsNotResurrectedByALateFill() => await HeadlessGui.RunAsync(async () =>
    {
        var probe = new DroppingFollowProbe();
        await using var host = await HarnessHostFixture.CreateAsync("rs3", Ct, decorateBackend: probe.Decorate);
        var session = host.Environment.Agent.Session;
        var id = session.Id.Value;
        for (var turn = 1; turn <= 2; turn += 1)
            AppendTurn(session, turn, $"第{turn}轮");
        await FlushAsync(host);

        var chat = await HostChatSession.OpenAsync(Fleet(host), session.Header.Cwd!, id, Ct, NewCache());
        try
        {
            await using var viewModel = new MainViewModel(host.Environment.App, chat);
            await GuiTestEnvironment.WaitForAsync(
                viewModel.Messages,
                () => UserMessages(viewModel).Count == 2,
                () => GuiTestEnvironment.DescribeChatState(viewModel));

            // 第 3 轮: 用户消息那条在实时流里丢掉(于是成为一个缺口), 紧接着整轮被回退掉。
            // 补洞的取数被拖慢, 保证它一定在撤回事件之后才回来 —— 那才是"删过一次又长回来"的现场。
            probe.Delay = TimeSpan.FromMilliseconds(1_200);
            var userSeq = session.Seq + 2;
            probe.Drop(userSeq, userSeq + 1);
            AppendTurn(session, 3, "第三轮");
            await FlushAsync(host);
            session.Revert(userSeq);
            await FlushAsync(host);

            // 给补洞留出时间: 补回来的那条属于被撤掉的区间, 不许出现在正文里。
            await Task.Delay(2_500, Ct);
            Assert.DoesNotContain(viewModel.Messages, message => message.BodyText.Contains("第三轮", StringComparison.Ordinal));
            Assert.DoesNotContain(viewModel.Messages, message => message.BodyText.Contains("第三轮", StringComparison.Ordinal));
            Assert.Equal(2, UserMessages(viewModel).Count);
            // 回退本身要留痕。
            Assert.Contains(viewModel.TraceItems, item => item.Title.Contains("回退", StringComparison.Ordinal));
        }
        finally
        {
            chat.Dispose();
        }
    });

    private static List<MessageViewModel> UserMessages(MainViewModel viewModel)
        => [.. viewModel.Messages.Where(message => message.Kind == MessageKind.User)];

    private static ProjectionCache NewCache()
        => new(Path.Combine(Path.GetTempPath(), $"dsh-resync-cache-{Guid.NewGuid():N}"));

    private static FleetWorkspace Fleet(HarnessHostFixture host)
    {
        var options = FleetConnectionTests.TestOptions with { ClientName = "gui-resync" };
        return new FleetWorkspace(new FleetWorkspaceServices(
            new MachineStore(host.Environment.App.Home), options, () => new FleetClient([MachineSpec.Local(host.Root)], options)));
    }

    private static async Task FlushAsync(HarnessHostFixture host)
    {
        var store = host.Environment.App.Ctx.Get<SessionStore>(SessionStore.ServiceName)!;
        foreach (var live in store.List())
            await store.Flush(live);
    }

    /** 每轮一条用户消息 + 一条助手消息。 */
    private static void AppendTurn(Session session, int turn, string text)
    {
        session.Append(new TurnStartPayload(turn));
        session.Append(new StepStartPayload(turn, 1));
        session.Append(new UserMessagePayload(MessageFactory.CreateUserText($"{text}: 用户")), new SurfaceOp.Append());
        session.Append(
            new AssistantMessagePayload(turn, 1, MessageFactory.CreateAssistantMessage([new TextBlock($"{text}: 助手")], "p", "m")),
            new SurfaceOp.Append());
        session.Append(new TurnEndPayload(turn, new TurnEndReason.Completed()));
    }

    /**
     * 测试用的宿主探针: 把指定序号的实时事件从订阅流里抽掉(快照仍然完整)。
     *
     * 这模拟的是"补齐只取回半段": 客户端收到的事件流里出现跳号, 而宿主本身没有坏。
     */
    private sealed class DroppingFollowProbe
    {
        private readonly HashSet<long> _dropped = [];

        public int SnapshotRequests { get; private set; }

        /** 让补洞的取数晚一点回来: 用来把"补洞在撤回之后才到"这个顺序固定下来。 */
        public TimeSpan Delay { get; set; }

        private async Task<RemoteSessionSnapshot> DelayAsync(Task<RemoteSessionSnapshot> response, CancellationToken cancellationToken)
        {
            if (Delay > TimeSpan.Zero)
                await Task.Delay(Delay, cancellationToken);
            return await response;
        }

        public void Drop(long from, long to)
        {
            lock (_dropped)
                for (var seq = from; seq < to; seq += 1)
                    _dropped.Add(seq);
        }

        public IRemoteHostBackend Decorate(IRemoteHostBackend inner) => new Backend(inner, this);

        private bool IsDropped(long seq)
        {
            lock (_dropped)
                return _dropped.Contains(seq);
        }

        private sealed class Backend(IRemoteHostBackend inner, DroppingFollowProbe probe) : IRemoteHostBackend
        {
            public HostPendingDecisions Pending => inner.Pending;
            public HostSessionRegistry Sessions => inner.Sessions;
            public IRemoteSessionBackend Session { get; } = new DroppingSession(inner.Session, probe);
            public Task<HostInfo> InfoAsync(CancellationToken cancellationToken) => inner.InfoAsync(cancellationToken);
            public IRemoteAgentBackend? Agent => inner.Agent;
            public IRemotePtyBackend? Pty => inner.Pty;
            public HostFileSystem? Files => inner.Files;
            public HostGitService? Git => inner.Git;
            public HostNotificationCenter? Notifications => inner.Notifications;
            public IRemoteSessionEditingBackend? SessionEditing => inner.SessionEditing;
            public IRemotePluginsBackend? Plugins => inner.Plugins;
            public IRemoteNetworkBackend? Network => inner.Network;
            public IRemoteEditorBackend? Editor => inner.Editor;
        }

        private sealed class DroppingSession(IRemoteSessionBackend inner, DroppingFollowProbe probe) : IRemoteSessionBackend
        {
            public Task<IReadOnlyList<RemoteSessionSummary>> ListSessionsAsync(CancellationToken cancellationToken)
                => inner.ListSessionsAsync(cancellationToken);

            public Task<RemoteSessionSummary> CreateSessionAsync(string cwd, CancellationToken cancellationToken)
                => inner.CreateSessionAsync(cwd, cancellationToken);

            public Task<RemoteSessionSummary> ResumeSessionAsync(string sessionId, CancellationToken cancellationToken)
                => inner.ResumeSessionAsync(sessionId, cancellationToken);

            public Task<RemoteSessionSnapshot> GetSessionSnapshotAsync(
                string sessionId, long fromEvent, int maxEvents, CancellationToken cancellationToken)
            {
                probe.SnapshotRequests += 1;
                // 只有"补洞"的取数被拖慢(整段打开的历史照常), 这样补回来的事件一定晚于撤回事件到达。
                return fromEvent > 0
                    ? probe.DelayAsync(inner.GetSessionSnapshotAsync(sessionId, fromEvent, maxEvents, cancellationToken), cancellationToken)
                    : inner.GetSessionSnapshotAsync(sessionId, fromEvent, maxEvents, cancellationToken);
            }

            public Task SendMessageAsync(string sessionId, string text, IReadOnlyList<RemoteImageBlock> images, CancellationToken cancellationToken)
                => inner.SendMessageAsync(sessionId, text, images, cancellationToken);

            public Task InterruptAsync(string sessionId, CancellationToken cancellationToken)
                => inner.InterruptAsync(sessionId, cancellationToken);

            public Task<IReadOnlyList<string>> ListToolsAsync(string sessionId, CancellationToken cancellationToken)
                => inner.ListToolsAsync(sessionId, cancellationToken);

            public async Task<RemoteHostStream<RemoteSessionEvent>> SubscribeAsync(
                string sessionId, long fromSeq, CancellationToken cancellationToken)
            {
                var stream = await inner.SubscribeAsync(sessionId, fromSeq, cancellationToken);
                return new RemoteHostStream<RemoteSessionEvent>(Filter(stream, probe, cancellationToken), stream.DisposeAsync);
            }

            private static async IAsyncEnumerable<RemoteSessionEvent> Filter(
                IAsyncEnumerable<RemoteSessionEvent> source,
                DroppingFollowProbe probe,
                [EnumeratorCancellation] CancellationToken cancellationToken)
            {
                await foreach (var item in source.WithCancellation(cancellationToken))
                {
                    if (item.Event is { } sessionEvent && probe.IsDropped(sessionEvent.Seq))
                        continue;
                    yield return item;
                }
            }
        }
    }
}

