using System.Diagnostics;
using System.Globalization;
using System.Text;
using Dsh.Agent;
using Dsh.Boot;
using Dsh.Core;
using Dsh.Interaction;
using Dsh.Llm;
using Dsh.Persistence;
using Dsh.Tui;

namespace Dsh.Tests;

/**
 * TUI 历史的端到端验收(对着真实会话副本跑, 见 plans/GUI优化向TUI移植方案-2026-11-24.md §6.2)。
 *
 * 为什么要它: 组件用例只能造出"一页历史"这种小场景, 而用户真正会碰到的是"几十万条事件、页缓存反复失效、
 * 上下来回乱跳"。这里对每份会话副本做三件事:
 *   1) 从打开位置一直滚到顶 —— 必须真的到顶, 期间任何一次补页都不许把视口甩到底部;
 *   2) 再从顶滚回底 —— 必须恢复贴底;
 *   3) 用**另一个新窗格**(只回放了尾窗)把**每一条用户消息都按随机顺序跳一遍** —— 这是用户的真实用法,
 *      也是唯一能把"跳转要补页 + 页缓存抖动 + 锚点还原"一起压到的路径。
 *
 * 语料: `artifacts/real-sessions-copy/sessions`(只读副本; 缺失时退回 `~/.dsh/sessions`), 不存在则跳过。
 * 没有任何断言依赖固定会话 id —— 换一批会话仍然跑得起来。
 */
[Collection(SerialProcessCollection.Name)]
[Trait("Category", "RealSessions")]
public sealed class TuiHistoryAcceptanceTests
{
    /** 一次验收覆盖的会话份数: 按体量分层挑, 不只挑大的。 */
    private const int CorpusSize = 10;

    /** 每份会话的预算: 打开 + 滚到顶 + 全量跳转 + 回到底。 */
    private static readonly TimeSpan PerSessionBudget = TimeSpan.FromMinutes(20);

    /**
     * 单次跳转的上限: 只用来兜住"第一次跳进前缀"那一次整段重放(与 GUI 完整加载同量级, 十几秒)。
     * 之后目标都落在已载入区间里, 跳转应当是瞬时的 —— 真正的判据是下面的整批预算。
     */
    private static readonly TimeSpan PerJumpBudget = TimeSpan.FromSeconds(20);

    /**
     * 整个会话的**全部**跳转(几十到上百次)合计上限。
     * 用户不会为跳一条消息等一分钟: 第一次整段重放之后, 其余每一次都必须是瞬时的。
     */
    private const long TotalJumpBudgetMs = 60_000;

    /**
     * 语料下限: 至少这么多份会话的跳转数 ≥ {@link MultiJumpThreshold}。
     *
     * 为什么要这条: 跳转数等于"该会话里仍在时间线上的用户消息数", 它由数据决定 ——
     * 只有一个回合的会话(实测 1043711 条事件的那种, turn/start=1)只有 1 条真实用户消息;
     * 被回退到开头的会话(revert 目标 5087、回退事件在 887079)也只有 1 条。
     * 那样的会话"1 次跳转"是对的, 但如果整个语料都退化成这种, 验收就什么都没压到。
     */
    private const int MinimumMultiJumpSessions = 3;

    private const int MultiJumpThreshold = 10;

    [Fact(Timeout = 3_600_000)]
    public async Task EveryUserMessage_IsReachable_AndBothEndsStayReachable()
    {
        var root = CorpusRoot();
        if (root is null)
            Assert.Skip("没有真实会话副本(artifacts/real-sessions-copy/sessions 或 ~/.dsh/sessions 都不存在)");
        var picks = PickSessions(root);
        Assert.True(picks.Count > 0, "语料目录里没有可用的会话");

        var csv = Path.Combine(TempTree.RepositoryRoot, "tests/.scratch/tui-history-acceptance.csv");
        Directory.CreateDirectory(Path.GetDirectoryName(csv)!);
        // 逐份落盘(而不是最后一次性写): 大会话一份要跑几分钟, 中途崩了也要留下已经跑完的行。
        await File.WriteAllLinesAsync(csv, ["session,mb,events,residentFrom,reverted,turns,subagent,folds,topSteps,topMs,pageFaultsTop,jumps,jumpFailures,jumpTotalMs,bottomOk,seed"], TestContext.Current.CancellationToken);
        var failures = new StringBuilder();
        var random = new Random(20261125);
        var multiJumpSessions = 0;

        foreach (var pick in picks)
        {
            try
            {
                var row = await ValidateAsync(root, pick, random);
                if (row is null)
                {
                    await Console.Out.WriteLineAsync($"skip {pick.Id} ({pick.Megabytes}MB): 日志里没有事件, 无历史可验收");
                    continue;
                }
                await File.AppendAllLinesAsync(csv, [row], TestContext.Current.CancellationToken);
                await Console.Out.WriteLineAsync($"ok   {pick.Id} ({pick.Megabytes}MB) {row}");
                // 列序: session,mb,events,residentFrom,reverted,turns,…,jumps(index 11)
                if (int.TryParse(row.Split(',')[11], out var jumps) && jumps >= MultiJumpThreshold)
                    multiJumpSessions += 1;
            }
            catch (Exception error)
            {
                failures.AppendLine($"{pick.Project}/{pick.Id} ({pick.Megabytes}MB): {error.Message}");
                await Console.Out.WriteLineAsync($"FAIL {pick.Id} ({pick.Megabytes}MB): {error.Message}");
            }
        }

        Assert.True(multiJumpSessions >= MinimumMultiJumpSessions,
            $"语料退化: 只有 {multiJumpSessions} 份会话的跳转数 ≥ {MultiJumpThreshold}, 少于要求的 {MinimumMultiJumpSessions} 份"
            + " —— 多跳转路径没有被压到(明细见 " + csv + ")");

        Assert.True(failures.Length == 0, $"以下会话未通过:\n{failures}\n明细: {csv}");
    }

    /** 返回 CSV 行; 日志里一条事件都没有(纯头)时返回 null, 由调用方记为跳过。 */
    /** 超过这个事件数的会话必须走窗口化恢复(尾窗 + 前缀按页读), 否则等于没压到补页。 */
    private const long WindowedFromEvents = 200_000;

    private static async Task<string?> ValidateAsync(string root, Pick pick, Random random)
    {
        await using var environment = await GuiTestEnvironment.CreateIsolatedAsync();
        Seed(environment.App.Home, root, pick);
        await using var restarted = await environment.RestartAsync();
        var agents = restarted.App.Ctx.Get<AgentRegistry>(AgentRegistry.ServiceName)!;
        using var budget = new CancellationTokenSource(PerSessionBudget);
        var handle = await agents.Resume(
            new ResumeAgentOptions(SessionId.Create(pick.Id), new AgentOptions("test", "test-model")),
            budget.Token);
        var agent = (AgentLoopAgent)handle.Agent;
        // 纯头日志(没有任何事件): 转录本来就是空的, "到顶/到底/跳转"都无从谈起, 记为跳过而不是失败。
        if (agent.Session.Seq == 0)
            return null;
        // 折出检查点再重进一次: 只有带检查点的会话才会窗口化恢复(常驻尾窗 + 前缀按页读),
        // 整份载入的话"滚到顶补页/锚点还原"这两条路径根本没被执行到。
        agent.Session.AssertFullyFolded();
        await restarted.App.Ctx.Get<SessionStore>(SessionStore.ServiceName)!.Flush(agent.Session);
        var eventsTotal = agent.Session.Seq;
        await using var windowed = await restarted.RestartAsync();
        agents = windowed.App.Ctx.Get<AgentRegistry>(AgentRegistry.ServiceName)!;
        handle = await agents.Resume(
            new ResumeAgentOptions(SessionId.Create(pick.Id), new AgentOptions("test", "test-model")),
            budget.Token);
        agent = (AgentLoopAgent)handle.Agent;
        if (eventsTotal >= WindowedFromEvents)
            Assert.True(agent.Session.ResidentFrom > 0,
                $"{pick.Id}: {eventsTotal} 条事件必须窗口化恢复, 实际 residentFrom={agent.Session.ResidentFrom}");

        // ---- 1) 打开后一直滚到顶: 必须真的到顶, 且视口不被甩到底部 ----
        using var chat = new ChatWindow(windowed.App.Ctx, agent, windowed.App.Home);
        var pane = chat.InputPane;
        Draw(chat);
        var topCheck = Stopwatch.StartNew();
        var steps = 0;
        while (pane.HasOlderHistory && steps < 400)
        {
            var before = pane.ScrollOffset;
            pane.ScrollOffset = 0;
            Draw(chat);
            pane.HandleMouseWheel(120);
            Draw(chat);
            steps += 1;
            Assert.False(pane.StickToBottom, $"{pick.Id}: 第 {steps} 次补页把视口甩回了底部(之前 {before})");
            Assert.Null(pane.OlderHistoryStatusText);
        }
        topCheck.Stop();
        Assert.False(pane.HasOlderHistory, $"{pick.Id}: 补了 {steps} 次仍未到顶");
        Assert.NotEmpty(pane.SnapshotLines());

        var events = agent.Session.Seq;
        var residentFrom = agent.Session.ResidentFrom;
        var totalMessages = pane.TimelineEntries.Count;
        Assert.True(pane.HistoryIndexComplete, $"{pick.Id}: 到了顶部, 时间线索引却没覆盖整段");

        // ---- 2) 从顶滚回底: 必须恢复贴底 ----
        // PageDown 一次滚 10 行, 所以次数要按当前转录的实际行数算(折行后有几万行),
        // 固定次数会把"还没滚到底"误判成"回不到末尾"。
        var linesToCover = pane.Renderer.LineCount + 1;
        for (var attempt = 0; attempt < (linesToCover / 10) + 100 && !pane.StickToBottom; attempt += 1)
        {
            pane.HandleKey(new ConsoleKeyInfo('\0', ConsoleKey.PageDown, false, false, false));
            Draw(chat);
        }
        Assert.True(pane.StickToBottom, $"{pick.Id}: PageDown 回不到末尾(不会恢复贴底)");

        // ---- 3) 新窗格(只有尾窗)+ 每一条用户消息按随机顺序跳一遍 ----
        using var fresh = new ChatWindow(windowed.App.Ctx, agent, windowed.App.Home);
        var freshPane = fresh.InputPane;
        Draw(fresh);
        // 候选与跳转必须用**同一个窗格**的索引: 新窗格只回放了尾窗, 索引也只覆盖尾窗。
        // 套用另一个窗格的 index/total 会让"渲染侧序号前移"算错, 跳转于是全部落到负下标。
        var scanned = IndexFreshPane(freshPane, agent);
        var freshMessages = freshPane.TimelineEntries;
        Assert.True(freshPane.HistoryIndexComplete, $"{pick.Id}: 新窗格的时间线索引没能铺满整段");
        Assert.Equal(totalMessages, freshMessages.Count);
        // 先按 seq 快照一份: 跳转时会往前补页, 索引会把更早的消息插到**前面**, 下标于是整体位移 ——
        // 边跳边按下标取消息会取到别的条目(这不是跳转不准, 是测试自己在错位索引)。
        var targets = freshMessages.Select(entry => (entry.Seq, Text: AnchorTextOf(entry))).ToList();
        var order = Enumerable.Range(0, targets.Count).OrderBy(_ => random.Next()).ToList();
        var jumpFailures = 0;
        var jumpTotalMs = 0L;
        var failureDetails = new StringBuilder();
        foreach (var index in order)
        {
            var (seq, expected) = targets[index];
            var jumpClock = Stopwatch.StartNew();
            fresh.RunSlashCommand(freshPane, $"/timestamp jump #{seq.ToString(CultureInfo.InvariantCulture)}");
            // 轮询只看视口顶行: 把整份转录物化成字符串(SnapshotLines)放进轮询里, 每次都要重建一遍
            // 几万行, 测出来的就不是跳转成本了。
            var landed = PumpUntil(fresh, () => freshPane.ViewportTopText().Contains(expected, StringComparison.Ordinal));
            jumpClock.Stop();
            jumpTotalMs += jumpClock.ElapsedMilliseconds;
            // jump 的语义就是"目标消息落在视口最上方": 顶行必须就是它。
            if (landed)
                continue;
            jumpFailures += 1;
            // 失败要能区分原因: 没停到目标 / 停到别处 / 明确说了跳不过去(附上它说的话)。
            var lines = freshPane.SnapshotLines();
            var said = lines.LastOrDefault(line => line.Contains("jump 失败", StringComparison.Ordinal));
            var top = freshPane.ViewportTopText();
            failureDetails.AppendLine(
                $"  #{seq} (order={index}/{targets.Count}, landed={landed}, {jumpClock.ElapsedMilliseconds}ms)"
                + $" top=\"{Truncate(top, 60)}\" expected=\"{Truncate(expected, 60)}\""
                + $" lines={lines.Count} chars={freshPane.Renderer.CurrentLength}"
                + $" userMsgs={freshPane.Renderer.UserMessageCount} older={freshPane.HasOlderHistory}"
                + $" stick={freshPane.StickToBottom}"
                + $" status=\"{freshPane.OlderHistoryStatusText}\""
                + (said is null ? "" : $" said=\"{Truncate(said.Trim(), 160)}\""));
        }
        Assert.True(jumpFailures == 0, $"{pick.Id}: {jumpFailures}/{targets.Count} 次跳转未落到目标\n{failureDetails}");
        // 用户不会为跳一条消息等一分钟: 整批跳转(第一次整段重放 + 其余瞬时)必须在一分钟内跑完。
        Assert.True(jumpTotalMs <= TotalJumpBudgetMs,
            $"{pick.Id}: {targets.Count} 次跳转共耗时 {jumpTotalMs}ms, 超过整批预算 {TotalJumpBudgetMs}ms");

        // 乱跳之后两端仍然可达。
        freshPane.StickToBottom = true;
        Draw(fresh);
        Assert.NotEmpty(freshPane.SnapshotLines());

        return string.Join(',',
            pick.Id,
            pick.Megabytes,
            events,
            residentFrom,
            agent.Session.RevertedRanges.Count,
            // 回合数: 跳转数由"该会话还剩几条用户消息"决定, 只有 1 个回合的会话本来就只有 1 次跳转。
            scanned.TurnStarts.Count,
            agent.Session.Header.ParentSession is null ? 0 : 1,
            pane.Renderer.Folds.Count,
            steps,
            topCheck.ElapsedMilliseconds,
            agent.Session.PageFaults,
            freshMessages.Count,
            jumpFailures,
            jumpTotalMs,
            1,
            20261125);
    }

    /**
     * 把窗格的时间线索引补齐到整段: 与 `/timestamp` 打开前那条后台补扫同一条路径
     * (`TranscriptHistoryIndex.Scan` → `MergeEarlierHistory`), 只是这里同步做, 好让断言有确定的前置条件。
     * 顺带把这份扫描结果返回给调用方(回合数要写进产物, 用来说明跳转数为什么是那个值)。
     */
    private static (List<SessionTimeline.Entry> Messages, List<long> TurnStarts,
        List<(long From, long To)> Reverted, long CoveredFrom) IndexFreshPane(ChatPane pane, AgentLoopAgent agent)
    {
        var scan = TranscriptHistoryIndex.Scan(agent.Session, 0, pane.HistoryCoveredFrom, pane.KnownRevertedRanges);
        if (!pane.HistoryIndexComplete)
            pane.MergeEarlierHistory(scan.Messages, scan.TurnStarts, scan.Reverted, scan.CoveredFrom);
        return scan;
    }

    /**
     * 跳转后应当出现在视口顶行的那段文字。
     * 只用消息首行的前若干个词: 视口一行有列宽上限(100 列), 中文按双宽算, 拿 40 个字的整段前缀
     * 去比对单条视觉行必然不匹配 —— 那是测试自己的错, 与跳转准不准无关。
     */
    private static string AnchorTextOf(SessionTimeline.Entry entry)
    {
        var first = entry.Text.Split('\n')[0].Trim();
        const int anchorCharacters = 12;
        return first.Length <= anchorCharacters ? first : first[..anchorCharacters];
    }

    private static string Truncate(string value, int limit) => value.Length <= limit ? value : value[..limit] + "…";

    private static void Draw(ChatWindow chat)
    {
        chat.DrainUi();
        var grid = new CellGrid(100, 30);
        chat.Draw(grid, LayoutEngine.Calculate(100, 30));
    }

    /** 轮询到 ready 或超时(每轮都画一帧, 让补页动作真的发生); 返回是否在预算内就绪。 */
    private static bool PumpUntil(ChatWindow chat, Func<bool> ready)
    {
        var clock = Stopwatch.StartNew();
        while (clock.Elapsed < PerJumpBudget)
        {
            Draw(chat);
            if (ready())
                return true;
            Thread.Sleep(2);
        }
        return ready();
    }

    /** 语料根: 优先用仓库里的只读副本, 其次本机真实会话。 */
    internal static string? CorpusRoot()
    {
        var copy = Path.Combine(TempTree.RepositoryRoot, "artifacts/real-sessions-copy/sessions");
        if (Directory.Exists(copy))
            return copy;
        var live = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".dsh", "sessions");
        return Directory.Exists(live) ? live : null;
    }

    internal sealed record Pick(string Project, string Id, long Bytes, long Megabytes, int Turns);

    /**
     * 语料下限: 小于这个体量的日志要么是事故留下的空壳(0 字节), 要么只有头部没有任何事件 ——
     * 它们没有历史可验收, 选进来只会得到与本次验收无关的失败。
     */
    private const long MinimumCorpusBytes = 64 * 1024;

    /**
     * 按**回合数**分层挑, 不是按文件大小。
     *
     * 为什么: 跳转数等于"该会话里仍在时间线上的用户消息数", 而用户消息数由回合数决定。
     * 实测最会让人误判的情形恰恰是那些"事件极多但只有一个回合"的会话
     * (383MB / 1,043,711 事件 / turn/start=1): 它们体量最大, 按大小挑必然选中, 却只有 1 次跳转。
     * 所以先排出回合数序列, 取最大、次大、中位、以及有内容里最小的一份(短会话不该有补页入口),
     * 其余按回合数从高到低补齐 —— 多回合会话因此总是占多数。
     */
    internal static List<Pick> PickSessions(string root)
    {
        var all = new List<Pick>();
        foreach (var project in Directory.EnumerateDirectories(root))
        foreach (var session in Directory.EnumerateDirectories(project))
        {
            var log = Path.Combine(session, "session.jsonl.zstd");
            if (!File.Exists(log))
                continue;
            var bytes = new FileInfo(log).Length;
            if (bytes < MinimumCorpusBytes)
                continue;
            var turns = CountTurns(log);
            // 连回合都读不出来的日志(项目目录名与日志内部 cwd 对不上、或本身损坏)宿主也打不开, 跳过。
            if (turns < 0)
                continue;
            all.Add(new Pick(project, Path.GetFileName(session), bytes, bytes / 1024 / 1024, turns));
        }
        if (all.Count == 0)
            return [];
        all.Sort((left, right) => right.Turns.CompareTo(left.Turns));
        var picks = new List<Pick>();
        foreach (var position in new[] { 0, 1, 2, all.Count / 2, all.Count - 1 })
        {
            if (position >= 0 && position < all.Count && !picks.Contains(all[position]))
                picks.Add(all[position]);
        }
        foreach (var candidate in all)
        {
            if (picks.Count >= CorpusSize)
                break;
            if (!picks.Contains(candidate))
                picks.Add(candidate);
        }
        return picks;
    }

    /**
     * 数这份日志里有多少个回合: 回合边界决定了"最多能跳几次"(每个回合至少一条用户消息)。
     * 布局是 &lt;root&gt;/&lt;项目&gt;/&lt;会话&gt;/session.jsonl.zstd, 所以 root 是日志往上三层。
     * 只走信封上的 Type, 不解析载荷。
     *
     * 打不开时返回 -1: 那种日志(项目目录名与日志内部记录的 cwd 对不上、或本身损坏)连宿主也读不了,
     * 选进语料只会得到与本次验收无关的失败, 由调用方跳过。
     */
    private static int CountTurns(string logPath)
    {
        var session = Path.GetDirectoryName(logPath)!;
        var root = Directory.GetParent(Directory.GetParent(session)!.FullName)!.FullName;
        var id = SessionId.Create(Path.GetFileName(session));
        try
        {
            using var persistence = new JsonlSessionPersistence(root);
            using var handle = persistence.Open(id, SessionAccess.Read);
            var events = handle.Read();
            var turns = 0;
            foreach (var sessionEvent in events)
            {
                if (sessionEvent.Type == SessionEventTypes.TurnStart)
                    turns += 1;
            }
            return turns;
        }
        catch (Exception error) when (error is IOException or InvalidDataException or FormatException or InvalidOperationException)
        {
            return -1;
        }
    }

    /** 按磁盘上的原布局复制进隔离 home(目录名即项目键与会话 id, 生产代码自己解析)。 */
    private static void Seed(HarnessHome home, string root, Pick pick)
    {
        var source = Path.Combine(root, pick.Project, pick.Id);
        var target = Path.Combine(home.SessionsPath, Path.GetFileName(pick.Project), pick.Id);
        Directory.CreateDirectory(target);
        foreach (var file in Directory.EnumerateFiles(source))
            File.Copy(file, Path.Combine(target, Path.GetFileName(file)), overwrite: true);
    }


}




