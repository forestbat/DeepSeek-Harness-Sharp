using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Dsh.Fleet;
using Dsh.Gui.Services;
using Dsh.Gui.ViewModels;
using Dsh.Llm;
using Dsh.RemoteHost;

namespace Dsh.Tests;

/**
 * 真实会话副本上的端到端校验: 打开、跳转、回退、分叉、随机访问、投影缓存、截断降级。
 *
 * 为什么要有它: 基准只量"打开多久", 量不到"打开之后能不能用" —— 跳转不过去、滑到一半卡住、
 * 回退之后打不开、尾巴坏了一点就整份打不开, 这些都不体现在耗时数字里。用例拿**真实日志的副本**
 * (复制到隔离宿主, 不碰原文件)跑一遍用户会做的动作, 每份会话一条记录; 一份失败不影响其它份继续跑,
 * 最后把所有失败一起报出来。
 *
 * 源目录默认 `~/.dsh/sessions`, 可用 `DSH_VALIDATE_SESSION_ROOT` 指定; 取最大的若干份:
 * `DSH_VALIDATE_COUNT`(默认 10)、`DSH_VALIDATE_MAX_MB`(默认 0 = 不限)。
 * 源目录不存在时直接返回(容器/CI 上没有会话副本不该报错)。
 */
[Collection(SerialProcessCollection.Name)]
[Trait("Category", "RealSessions")]
public sealed class RealSessionValidationTests
{
    private const int DefaultCount = 10;
    /** 分叉前缀对照的条数: 只是"抽一段核对", 与分叉语义无关。 */
    private const int ForkPrefixEvents = 64;
    private static readonly TimeSpan PerSessionBudget = TimeSpan.FromMinutes(5);
    
    [Fact(Timeout = 3600000)]
    public async Task WorkingSessions_OpenAndStayUsable()
    {
        if (!Directory.Exists(SessionsRoot()))
            return;
        var failures = new StringBuilder();
        var ran = 0;
        foreach (var (project, id, bytes) in PickSessions())
        {
            ran += 1;
            try
            {
                await ValidateAsync(project, id);
            }
            catch (Exception error)
            {
                failures.AppendLine($"{project}/{id} ({bytes / 1024 / 1024}MB): {error}");
            }
        }
        Assert.True(ran > 0, "没有可校验的会话副本");
        Assert.True(failures.Length == 0, $"以下会话未通过:\n{failures}");
    }

    [Fact(Timeout = 3600000)]
    public async Task TruncatedSessions_StillOpenWithTheirPrefix()
    {
        if (!Directory.Exists(SessionsRoot()))
            return;
        var failures = new StringBuilder();
        foreach (var (project, id, bytes) in PickSessions())
        {
            try
            {
                await ValidateDamagedAsync(project, id);
            }
            catch (Exception error)
            {
                failures.AppendLine($"{project}/{id} ({bytes / 1024 / 1024}MB): {error}");
            }
        }
        Assert.True(failures.Length == 0, $"截断降级未通过:\n{failures}");
    }

    private static async Task ValidateAsync(string project, string id) => await HeadlessGui.RunAsync(async () =>
    {
        using var budget = new CancellationTokenSource(PerSessionBudget);
        var ct = budget.Token;
        await using var host = await HarnessHostFixture.CreateAsync("real", ct);
        Seed(SessionDirectory(project, id), host.Environment.App.Home);
        await using var gui = await GuiTestEnvironment.CreateIsolatedAsync();
        var cache = ProjectionCache.ForHome(gui.App.Home.Root);
        await using var fleet = Fleet(host, gui, "real-session-validation");

        var clock = Stopwatch.StartNew();
        using var chat = await HostChatSession.OpenAsync(fleet, project, id, ct, cache);
        clock.Stop();
        var openMs = clock.ElapsedMilliseconds;
        // 真实会话里没有"取不全"的余地: 这些日志已经用 Dsh.SessionDump 逐条核过。
        Assert.Null(chat.HistoryIncomplete);
        var total = chat.TotalEventCount;
        Assert.Equal(total, chat.Events.Count);
        if (total > 0)
        {
            Assert.True(chat.Events[0].Seq == 0, $"{id}: Events[0].Seq={chat.Events[0].Seq} count={chat.Events.Count} total={total}");
            Assert.True(chat.Events[^1].Seq == total - 1, $"{id}: Events[^1].Seq={chat.Events[^1].Seq} total={total}");
            var middle = (int)(total / 2);
            Assert.True(chat.Events[middle].Seq == middle,
                $"{id}: Events[{middle}].Seq={chat.Events[middle].Seq} total={total}");
        }

        // 滑动压力 1: 用户把滚动条从头拖到尾 —— 顺序走一遍每一条(此时还没有释放前缀, 下标即序号)。
        var sweep = Stopwatch.StartNew();
        for (var index = 0; index < chat.Events.Count; index += 1)
        {
            // 失败信息只在失败时构造: 每条都拼一遍字符串, 80 万次下来光格式化就要几秒。
            if (chat.Events[index].Seq != index)
                Assert.Fail($"{id}: 扫到第 {index} 条时序号是 {chat.Events[index].Seq} (应为 {index})");
        }
        sweep.Stop();

        // 折叠会把已投影的前缀放掉, 之后 `Events` 不再从 #0 起: 分叉对照要用**放掉之前**的那一段序号。
        var headSeqs = chat.Events.Take(ForkPrefixEvents).Select(sessionEvent => sessionEvent.Seq).ToList();

        var timeline = await chat.ExecuteAsync("/timestamp list");
        Assert.True(timeline.Known, $"{id}: /timestamp 命令没有注册");
        Assert.True(timeline.Success, $"{id}: /timestamp list 失败: {timeline.Text}");
        var seqs = SeqOf(timeline.Text!);

        // 滑动压力 2: 幼儿园小朋友 —— 把每条用户消息都跳一遍(随机顺序、上下交替),
        // 每跳一次读"一屏"(用候选条数当一屏, 不另立数字)。
        if (seqs.Count > 0)
        {
            var screen = seqs.Count;
            var random = new Random(98765);
            var order = seqs.OrderBy(_ => random.Next()).ToList();
            var jumps = Stopwatch.StartNew();
            for (var index = 0; index < order.Count; index += 1)
            {
                var target = order[index];
                var downward = index % 2 == 0;
                for (var step = 0; step < screen; step += 1)
                {
                    var pick = downward ? target + step : target - step;
                    if (pick < 0 || pick >= total)
                        continue;
                    Assert.Equal(pick, chat.Events[(int)pick].Seq);
                }
            }
            jumps.Stop();
            // 机制判据: 一次跳转只碰一帧, 从头扫到尾要碰所有帧 —— 跳得比扫还慢就说明在反复重解。
            Assert.True(jumps.ElapsedMilliseconds <= Math.Max(sweep.ElapsedMilliseconds, 1),
                $"{id}: {order.Count} 次跳转({screen} 条/次)用了 {jumps.ElapsedMilliseconds}ms, "
                + $"比从头扫到尾的 {sweep.ElapsedMilliseconds}ms 还慢");
        }

        if (seqs.Count >= 3)
        {
            var middle = seqs[seqs.Count / 2];
            var jump = await chat.ExecuteAsync($"/timestamp jump #{middle}");
            Assert.True(jump.Success, $"{id}: 跳到 #{middle} 失败: {jump.Text}");
        }
        await using (var viewModel = new MainViewModel(gui.App, chat))
        {
            Assert.NotEmpty(viewModel.Messages);
            Assert.DoesNotContain("不完整", viewModel.StatusText ?? "", StringComparison.Ordinal);
            Assert.All(seqs, seq => Assert.InRange(seq, 0L, total - 1));

            // 滑动压力 3: 折叠之后再从头扫一遍 —— 这一步该只查表(信封索引已建好), 不该再碰任何负载。
            var afterFold = Stopwatch.StartNew();
            var offset = chat.Events.Count > 0 ? chat.Events[0].Seq : 0;
            for (var index = 0; index < chat.Events.Count; index += 1)
            {
                if (chat.Events[index].Seq != offset + index)
                    Assert.Fail($"{id}: 折叠后扫到第 {index} 条时序号是 {chat.Events[index].Seq} (应为 {offset + index})");
            }
            afterFold.Stop();
            Assert.True(afterFold.ElapsedMilliseconds <= Math.Max(sweep.ElapsedMilliseconds, 1),
                $"{id}: 折叠后再扫一遍用了 {afterFold.ElapsedMilliseconds}ms, 比首次(建索引)的 {sweep.ElapsedMilliseconds}ms 还慢");
            Console.WriteLine($"#   {id} sweepMs(first)={sweep.ElapsedMilliseconds} sweepMs(afterFold)={afterFold.ElapsedMilliseconds} kept={chat.Events.Count} offset={offset}");

            // 再次打开(同一个投影缓存目录): 命中缓存, 且条数一致。
            clock.Restart();
            using var again = await HostChatSession.OpenAsync(fleet, project, id, ct, cache);
            clock.Stop();
            // 期间执行过命令(/timestamp list/jump 自己也会落事件), 所以只要求"不少于第一次看到的条数"。
            Assert.True(again.TotalEventCount >= total, $"{id}: 重开之后条数反而变少 {total} -> {again.TotalEventCount}");
            Assert.True(again.TotalEventCount == again.Events.Count, $"{id}: 重开之后条数与列表不一致");
            

            // 分叉: 断点之前的副本必须能独立打开, 且前缀逐条一致。
            if (seqs.Count >= 3)
            {
                var forked = await chat.ExecuteAsync($"/timestamp fork #{seqs[^1]}");
                Assert.True(forked.Success, $"{id}: 在 #{seqs[^1]} 分叉失败: {forked.Text}");
                var forkId = ForkIdOf(forked.Text);
                Assert.NotNull(forkId);
                using var fork = await HostChatSession.OpenAsync(fleet, project, forkId!, ct, cache);
                Assert.True(fork.TotalEventCount > 0, $"{id}: 分叉出来的会话是空的");
                for (var index = 0; index < Math.Min(fork.TotalEventCount, headSeqs.Count); index += 1)
                    Assert.True(headSeqs[index] == fork.Events[index].Seq, $"{id}: 分叉前缀第 {index} 条对不上 ({headSeqs[index]} vs {fork.Events[index].Seq})");
            }

            // 回退: 回退是"只追加一条 revert 事件 + Replace 覆盖那一段", 日志不会变短 ——
            // 要看的是**可见历史**变短: 回退点之后的用户消息从时间线上消失, 且之后还能正常重开。
            if (seqs.Count >= 3)
            {
                var target = seqs[1];
                var reverted = await chat.ExecuteAsync($"/timestamp revert #{target}");
                Assert.True(reverted.Success, $"{id}: 回退到 #{target} 失败: {reverted.Text}");
                var afterTimeline = await chat.ExecuteAsync("/timestamp list");
                Assert.True(afterTimeline.Success, $"{id}: 回退之后 /timestamp list 失败: {afterTimeline.Text}");
                var afterSeqs = SeqOf(afterTimeline.Text!);
                Assert.All(afterSeqs, seq => Assert.True(seq < target,
                    $"{id}: 回退到 #{target} 之后时间线上还有 #{seq}"));
                await chat.ReloadAsync(forceReset: true, ct);
                // 回退之后**重新打开**: 用户真正常走的路(关掉再看), 必须条数齐全、末尾在日志末尾。
                using var afterRevert = await HostChatSession.OpenAsync(fleet, project, id, ct);
                Assert.True(afterRevert.TotalEventCount == afterRevert.Events.Count,
                    $"{id}: 回退后重开的条数与日志不一致 ({afterRevert.TotalEventCount} vs {afterRevert.Events.Count}), incomplete={afterRevert.HistoryIncomplete ?? "-"}");
                Assert.True(afterRevert.Events[^1].Seq == afterRevert.TotalEventCount - 1,
                    $"{id}: 回退后重开的末尾不在日志末尾 ({afterRevert.Events[^1].Seq} vs {afterRevert.TotalEventCount - 1})");
}
        }

        Console.WriteLine($"# {project}/{id} events={total} openMs={openMs} sweepMs={sweep.ElapsedMilliseconds}");
        await Task.CompletedTask;
    }, PerSessionBudget);


    /**
     * 坏掉一小段不能拖垮整份会话: 把日志砍到 60%(尾部半帧)之后照样要能打开。
     *
     * 判据: 不卡(预算内返回) + 打开后有事件可看 + 序号不重复。"要么全用要么全不用"在这里是错的 ——
     * 用户宁可看到前 60% 加一句说明, 也不接受"会话打不开"。
     */
    private static async Task ValidateDamagedAsync(string project, string id) => await HeadlessGui.RunAsync(async () =>
    {
        using var budget = new CancellationTokenSource(PerSessionBudget);
        var ct = budget.Token;
        await using var host = await HarnessHostFixture.CreateAsync("real-damaged", ct);
        Seed(SessionDirectory(project, id), host.Environment.App.Home);
        var log = Path.Combine(host.Environment.App.Home.SessionsPath, project, id, "session.jsonl.zstd");
        Assert.True(File.Exists(log), $"{id}: 复制之后找不到日志");
        var original = new FileInfo(log).Length;
        using (var stream = new FileStream(log, FileMode.Open, FileAccess.Write))
        {
            stream.SetLength(original * 3 / 5);
            stream.Flush(true);
        }
        // 索引是按完整文件写的: 砍过之后必须被判失效, 否则窗口读会按错位边界解压。
        // 注意 Windows 通配符的坑: `session.jsonl.zstd.*` 会连 `session.jsonl.zstd` 一起匹配, 必须按文件名判。
        foreach (var file in Directory.GetFiles(Path.GetDirectoryName(log)!))
        {
            var name = Path.GetFileName(file);
            if (name.StartsWith("session.jsonl.zstd.", StringComparison.Ordinal) && name != "session.jsonl.zstd")
                File.Delete(file);
        }

        await using var gui = await GuiTestEnvironment.CreateIsolatedAsync();
        var clock = Stopwatch.StartNew();
        await using var fleet = Fleet(host, gui, "real-session-damaged");
        using var chat = await HostChatSession.OpenAsync(fleet, project, id, ct);

        clock.Stop();
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(120), $"{id}: 截断后打开用了 {clock.ElapsedMilliseconds}ms");
        Assert.True(chat.Events.Count > 0, $"{id}: 截断后一条事件都没打开");
        Assert.Equal(chat.Events.Count, chat.Events.Select(sessionEvent => sessionEvent.Seq).Distinct().Count());
        Console.WriteLine($"# damaged {project}/{id} events={chat.Events.Count} openMs={clock.ElapsedMilliseconds} incomplete={chat.HistoryIncomplete ?? "-"}");
    });

    // ---------- 取样与辅助 ----------

    private static string SessionsRoot()
        => Environment.GetEnvironmentVariable("DSH_VALIDATE_SESSION_ROOT")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".dsh", "sessions");

    private static string SessionDirectory(string project, string id) => Path.Combine(SessionsRoot(), project, id);

    /** 取最大的若干份: 问题总是先在大日志上暴露。 */
    private static IEnumerable<(string Project, string Id, long Bytes)> PickSessions()
    {
        var maxBytes = int.TryParse(Environment.GetEnvironmentVariable("DSH_VALIDATE_MAX_MB"), out var mb) && mb > 0
            ? mb * 1024L * 1024L
            : long.MaxValue;
        var count = int.TryParse(Environment.GetEnvironmentVariable("DSH_VALIDATE_COUNT"), out var wanted) && wanted > 0
            ? wanted
            : DefaultCount;
        return Directory.EnumerateDirectories(SessionsRoot())
            .SelectMany(project => Directory.EnumerateDirectories(project)
                .Select(id => (Project: Path.GetFileName(project), Id: Path.GetFileName(id),
                    Log: Path.Combine(id, "session.jsonl.zstd"))))
            .Where(entry => File.Exists(entry.Log))
            .Select(entry => (entry.Project, entry.Id, Bytes: new FileInfo(entry.Log).Length))
            .Where(entry => entry.Bytes <= maxBytes)
            .OrderByDescending(entry => entry.Bytes)
            .Take(count)
            .ToList();
    }

    /** 复制到隔离宿主: 不碰源目录, 也不带 lease.json(那是别的进程的写权记录)。 */
    private static void Seed(string source, Dsh.Boot.HarnessHome home)
    {
        var project = Path.GetFileName(Path.GetDirectoryName(source)!);
        var target = Path.Combine(home.SessionsPath, project, Path.GetFileName(source));
        Directory.CreateDirectory(target);
        foreach (var file in Directory.EnumerateFiles(source))
        {
            var name = Path.GetFileName(file);
            if (name.StartsWith("lease.", StringComparison.Ordinal))
                continue;
            File.Copy(file, Path.Combine(target, name), overwrite: true);
        }
    }

    private static FleetWorkspace Fleet(HarnessHostFixture host, GuiTestEnvironment gui, string client)
    {
        var options = FleetConnectionTests.TestOptions with { ClientName = client };
        return new FleetWorkspace(new FleetWorkspaceServices(
            new MachineStore(gui.App.Home), options, () => new FleetClient([MachineSpec.Local(host.Root)], options)));
    }

    private static List<long> SeqOf(string timeline)
        => [.. Regex.Matches(timeline, @"#(\d+)").Select(match => long.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture))];

    private static string? ForkIdOf(string? text)
        => text is null ? null : Regex.Match(text, @"forked -> (?<id>[^ :]+)") is { Success: true } match
            ? match.Groups["id"].Value
            : null;
}
