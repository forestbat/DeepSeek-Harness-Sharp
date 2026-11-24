using Dsh.Core;
using Dsh.Fleet;
using Dsh.Gui.Services;
using Dsh.Gui.ViewModels;
using Dsh.Interaction;
using Dsh.Llm;
using Dsh.RemoteHost;

namespace Dsh.Tests;

/**
 * 历史分页的端到端契约: 一页历史受**字节预算**约束, 一页可能只给你一段。
 *
 * 现场故障(真实会话 eec210bb, 887,072 条): 客户端按"页号 × 20,000"续页, 某一页在字节预算处被截断,
 * 于是被截掉的那几条被静默跳过; 界面重放到断点看到下一条序号跳了, 判定跳号并整批丢弃, 界面停在 53.8%;
 * 同时把这份半截投影写进缓存, 之后每次打开都直接还原成半截。10 秒冷启动也是这么来的
 * (跳号 → 重同步 → 再来一遍全量)。
 *
 * 用例**不依赖那个预算的具体数值**(12MB 改成别的值也不该让这些用例失效): 预算由 server 选项注入,
 * 这里用从 1KB 到 64MB 的几档把"每页都被截断"做成常态, 断言的是契约本身 ——
 * 不管一页给多少条, 客户端都必须按序号续页拿到整段历史, 或者明确说出缺哪一段。
 */
[Collection(SerialProcessCollection.Name)]
public sealed class GuiHistoryPagingTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /** 一页小到只能装几条: 几乎每一步都踩在"被截断的一页"上; 64MB 那档则一页装完(不截断的对照)。 */
    [Theory(Timeout = 240000)]
    [InlineData(1_024)]
    [InlineData(2_048)]
    [InlineData(8_192)]
    [InlineData(65_536)]
    [InlineData(64 * 1024 * 1024)]
    public async Task AnyPageBudget_DeliversTheWholeHistory(int pageBytes) => await HeadlessGui.RunAsync(async () =>
    {
        await using var host = await HarnessHostFixture.CreateAsync(
            "pg", Ct,
            hiddenCapabilities: [HostProtocol.LogSliceCapability],
            snapshotPageBytes: pageBytes);
        var session = host.Environment.Agent.Session;
        var id = session.Id.Value;
        // 400 轮小消息: 总量几十 KB, 小预算下每一页都会被截断。
        for (var turn = 1; turn <= 400; turn += 1)
            AppendTurn(session, turn, $"第{turn}轮{new string('·', turn % 40)}");
        await FlushAsync(host);
        var total = session.Seq;
        Assert.True(total > 1_000, $"precondition: the transcript should span many pages (got {total} events)");

        var client = await host.ConnectAsync("budget-probe", Ct);
        var head = await client.GetSessionSnapshotRangeAsync(id, 0, 1, Ct);
        Assert.Equal(total, head.TotalEventCount);
        var onePage = await client.GetSessionSnapshotRangeAsync(id, 0, (int)total, Ct);
        // 预算装不下整段历史时, 一页必然短于请求 —— 这正是"按页号续页会漏"的场景。
        var cut = onePage.Events.Count < total;
        if (!cut)
            Assert.True(pageBytes >= 64 * 1024 * 1024, $"a {pageBytes}-byte page must be cut for {total} events");

        var cache = NewCache();
        var chat = await HostChatSession.OpenAsync(Fleet(host), session.Header.Cwd!, id, Ct, cache);
        try
        {
            Assert.Null(chat.HistoryIncomplete);
            Assert.Equal(total, chat.Events.Count);
            Assert.Equal(total, chat.TotalEventCount);
            for (var index = 0; index < chat.Events.Count; index += 1)
                Assert.Equal(index, chat.Events[index].Seq);
            // 被截断处的那几条必须在(它们是按页号续页时最先丢掉的)。
            if (cut)
                Assert.Contains(chat.Events, sessionEvent => sessionEvent.Seq == onePage.Events[^1].Seq + 1);
            // 最后一条也必须在: 尾部丢失是"最新内容消失", 比中段丢失更难察觉。
            Assert.Contains(chat.Events, sessionEvent => sessionEvent.Seq == total - 1);

            // 界面这一层: 400 条用户消息全在, 状态行不许说"历史不完整"。
            await using (var viewModel = new MainViewModel(host.Environment.App, chat))
            {
                await GuiTestEnvironment.WaitForAsync(viewModel.Messages,
                    () => viewModel.Messages.Count(message => message.Kind == MessageKind.User) == 400,
                    () => GuiTestEnvironment.DescribeChatState(viewModel));
                Assert.DoesNotContain("不完整", viewModel.StatusText ?? "", StringComparison.Ordinal);
            }
        }
        finally
        {
            chat.Dispose();
        }

        // 完整载入才值得缓存: 存下来的那份必须覆盖到日志末尾。
        if (head.LogLength > 0)
        {
            // 写缓存是"打开之后顺手做的"(MainViewModel 不等它), 所以这里要等文件落下来再断言;
            // 紧跟其后就去读是拿时序当同步, 满了机器会随机挂。
            var saved = await WaitForCacheAsync(cache, id, head.LogLength, total);
            Assert.NotNull(saved);
            Assert.Equal(total, saved.NextSeq);
            Assert.Equal(total, saved.TotalEvents);
        }
    });

    /** 真的补不齐(宿主永远少给最后一条): 会话照常打开, 说明缺哪一段, 不写缓存, 不无限重试。 */
    [Fact(Timeout = 180000)]
    public async Task UnrecoverableHole_OpensAnywayAndSaysWhatIsMissing() => await HeadlessGui.RunAsync(async () =>
    {
        var probe = new ShortPageProbe();
        await using var host = await HarnessHostFixture.CreateAsync(
            "pg2", Ct, decorateBackend: probe.Decorate, snapshotPageBytes: 4_096);
        var session = host.Environment.Agent.Session;
        var id = session.Id.Value;
        for (var turn = 1; turn <= 20; turn += 1)
            AppendTurn(session, turn, $"第{turn}轮");
        await FlushAsync(host);
        var total = session.Seq;

        var cache = NewCache();
        var chat = await HostChatSession.OpenAsync(Fleet(host), session.Header.Cwd!, id, Ct, cache);
        try
        {
            // 打不开是不可接受的: 会话必须出来, 只是少了尾部那几条。
            Assert.NotEmpty(chat.Events);
            Assert.True(chat.Events.Count < total, $"the probe must keep the history short, got {chat.Events.Count} of {total}");
            Assert.NotNull(chat.HistoryIncomplete);
            // 说明里必须点名"从哪一条开始缺", 否则用户无从判断少了什么。
            Assert.Contains($"#{chat.Events.Count}", chat.HistoryIncomplete!, StringComparison.Ordinal);
            // 少一条就不写缓存: 这次投影不覆盖日志末尾。
            Assert.False(
                File.Exists(cache.PathFor(MachineSpec.LocalName, id)),
                "an incomplete projection must not be cached");

            // 界面照常打开, 并把"少了哪一段"说出来(不说的话用户会以为会话本来就这么短)。
            await using (var viewModel = new MainViewModel(host.Environment.App, chat))
            {
                Assert.Contains("不完整", viewModel.StatusText ?? "", StringComparison.Ordinal);
                Assert.NotEmpty(viewModel.Messages);
            }
        }
        finally
        {
            chat.Dispose();
        }
        // 重试有界: 补齐失败就停, 不会把宿主刷爆(实测曾经一直重试把界面卡死几分钟)。
        Assert.True(probe.SnapshotRequests < 40, $"the client made {probe.SnapshotRequests} snapshot requests for one open");
    });

    /** 投影缓存只认"覆盖到日志末尾"的那一份: 半截投影既不写也不读。 */
    [Fact]
    public async Task PartialProjection_IsNeitherSavedNorLoaded()
    {
        var root = Path.Combine(Path.GetTempPath(), $"dsh-partial-cache-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var cache = new ProjectionCache(root);
            var partial = Snapshot(nextSeq: 477_300, totalEvents: 887_072);
            await cache.SaveAsync("local", "s-1", partial, Ct);
            Assert.False(File.Exists(cache.PathFor("local", "s-1")), "a partial projection must not be written");

            // 上一个版本(或别的写入方)留下的半截缓存: 读的时候同样不认。
            var path = cache.PathFor("local", "s-1");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllBytesAsync(path, ProjectionSnapshotCodec.Encode(partial), Ct);
            Assert.Null(cache.TryLoad("local", "s-1", logLength: 100, totalEvents: 887_072));

            // 完整的那一份照常命中。
            var complete = Snapshot(nextSeq: 887_072, totalEvents: 887_072);
            await cache.SaveAsync("local", "s-1", complete, Ct);
            var loaded = cache.TryLoad("local", "s-1", logLength: 100, totalEvents: 887_072);
            Assert.NotNull(loaded);
            Assert.Equal(887_072, loaded.NextSeq);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static ProjectionSnapshot Snapshot(long nextSeq, long totalEvents) => new()
    {
        FormatVersion = ProjectionSnapshot.CurrentFormatVersion,
        NextSeq = nextSeq,
        TotalEvents = totalEvents,
        LogLength = 100,
        RenderedAny = true,
        IsBusy = false,
        CompactionBusy = false,
        Messages = [],
        Trace = [],
        LastUserMessageSeqs = [],
        PendingToolCalls = [],
        Tokens = new TokenStatsSnapshot(0, 0, 0, null, 0, ""),
        Deliverables = [],
        Diffs = [],
    };

    private static ProjectionCache NewCache()
        => new(Path.Combine(Path.GetTempPath(), $"dsh-paging-cache-{Guid.NewGuid():N}"));

    /** 投影缓存的写入是后台任务: 轮询到它出现为止, 不把"文件什么时候落盘"当成被测行为。 */
    private static async Task<ProjectionSnapshot?> WaitForCacheAsync(
        ProjectionCache cache, string id, long logLength, long total)
    {
        for (var attempt = 0; attempt < 300; attempt += 1)
        {
            if (cache.TryLoad(MachineSpec.LocalName, id, logLength, total) is { } saved)
                return saved;
            await Task.Delay(50);
        }
        return null;
    }

    private static FleetWorkspace Fleet(HarnessHostFixture host)
    {
        var options = FleetConnectionTests.TestOptions with { ClientName = "history-paging-gui" };
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
}