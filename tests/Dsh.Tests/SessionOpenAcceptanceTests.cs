using System.Diagnostics;
using System.Globalization;
using System.Text;
using Dsh.Core;
using Dsh.Gui.Services;
using Dsh.Gui.ViewModels;
using Dsh.Llm;
using Dsh.Runtime;

namespace Dsh.Tests;

/**
 * 方案 §6.4 的正式验收: R1 / R3 / R5 在**每一份**真实会话上逐份判定(不抽样、不取平均), 冷热两态, 结果归档 CSV。
 *
 * 打开走真实 GUI 路径(本地宿主 + Fleet 客户端 + HostChatSession + MainViewModel, 与用户点开一条会话同一条路),
 * 只把窗口换成 headless。四个刻度来自 SessionOpenProbe: 首帧 / 可输入 / 最长主线程阻塞 / 收尾。
 *
 * 语料只用真实会话的字节级副本: 原件只被复制。冷态 = 删掉 *.surface 与投影缓存;
 * 热态 = 保留两者、同一份副本连开 R5 次。判定逐份进行: 任一份超出即不通过, 原因写进 verdict 列。
 */
[Collection("Bench")]
[Trait("Category", "Bench")]
public sealed class SessionOpenAcceptanceTests
{
    /** R1: 冷态首屏预算(用户看到内容)。 */
    private const long ColdFirstPaintBudgetMs = 1500;

    /** R1: 热态首屏预算。 */
    private const long WarmFirstPaintBudgetMs = 500;

    /** R3: 打开期间主线程最长阻塞预算。 */
    private const long StallBudgetMs = 100;

    /** R5: 热态重复打开次数。 */
    private const int WarmRepeats = 3;

    /** R5 判趋势用的抖动系数: 只拦"越开越慢", 不要求逐毫秒相等。 */
    private const double WarmGrowthFactor = 1.6;

    /** 让后台的投影缓存保存有机会落地, 再判"命中且无新事件不重写"。 */
    private const int CacheSettleMs = 400;

    private const string LogFileName = "session.jsonl.zstd";
    private const string SurfacePattern = "*.surface";
    private const string Missing = "-";

    private const string Header =
        "session,logBytes,events,tier,cold_firstPaint_ms,cold_interactive_ms,cold_stall_ms,cold_total_ms,"
        + "warm_firstPaint_ms,warm_interactive_ms,warm_stall_ms,warm_total_ms,warm_last_total_ms,warm_repeats,"
        + "projectionCacheRewritten,checkpointHit,historyIncomplete,r1,r3,r5,verdict";

    /** 本次会话打开过的界面: 不释放(见下), 只用来在会话结束时断开引用。 */
    private static readonly List<Open> Opens = [];

    [Fact(Explicit = true)]
    public async Task EveryRealSessionMeetsR1R3R5InColdAndWarmStates() => await HeadlessGui.RunAsync(async () =>
    {
        var sources = Distinct(EnumerateCorpus()).OrderBy(source => source.LogBytes).Take(Limit()).ToList();
        Assert.NotEmpty(sources);
        var rows = new List<Row>(sources.Count);
        // 逐行落盘: 全量要跑几十分钟, 中途被打断也不该丢掉已经测完的那些。
        var csv = await BeginCsvAsync();
        for (var index = 0; index < sources.Count; index += 1)
        {
            var source = sources[index];
            // 逐份打点: 全量跑要几十分钟, 出了问题必须能从输出看出卡在哪一份。
            Console.WriteLine($"[{index + 1}/{sources.Count}] start {source.Name} {source.LogBytes / 1024}KB");
            Console.Out.Flush();
            // 每个会话一个**隔离**环境: 共享环境的 FleetWorkspace 全进程一份, 释放一次界面会连带关掉别人的宿主连接。
            await using var environment = await GuiTestEnvironment.CreateIsolatedAsync();
            var row = await MeasureAsync(environment, source, TestContext.Current.CancellationToken);
            rows.Add(row);
            await File.AppendAllTextAsync(csv, row.ToCsv() + Environment.NewLine);
            Console.WriteLine($"[{index + 1}/{sources.Count}] {row.Verdict} {source.Name}");
            Console.Out.Flush();
        }
        var failures = rows.Where(row => !row.Passed).ToList();
        Assert.True(failures.Count == 0,
            $"{failures.Count}/{rows.Count} 份会话不满足 R1/R3/R5, CSV: {csv}\n"
            + string.Join('\n', failures.Take(20).Select(row => $"{row.Source.Name}: {row.Verdict}")));
    }, TimeSpan.FromMinutes(240));

    /**
     * 进程热 + 缓存冷: **GUI 已经在运行**, 用户点开一条从没在本进程打开过的会话。
     *
     * 这与上一条 Fact 的"冷态"不是一回事: 上一条每份会话都新起宿主进程(插件组合 + 首用 JIT 都算在内),
     * 量出来的是崩溃后第一次打开的最坏情形; 用户掐秒表量的是这一条 —— 进程已经热了, 只是这条会话没开过。
     */
    [Fact(Explicit = true)]
    public async Task WarmProcess_ColdCache_OpeningTheLargestSessions() => await HeadlessGui.RunAsync(async () =>
    {
        var top = TopBigSessions();
        Assert.NotEmpty(top);
        await using var environment = await GuiTestEnvironment.CreateIsolatedAsync();
        var cache = ProjectionCache.ForHome(environment.App.Home.Root);
        var rows = new List<(string Name, long Bytes, OpenCostResult? Cost, OpenCostResult? Cached)>();
        foreach (var source in top)
        {
            var log = Stage(environment, source);
            var root = Path.GetDirectoryName(log)!;
            ClearDerived(log, cache, source);
            var cold = await OpenAsync(environment, source, cache, TestContext.Current.CancellationToken);
            Opens.Add(cold);
            var warm = await OpenAsync(environment, source, cache, TestContext.Current.CancellationToken);
            Opens.Add(warm);
            rows.Add((source.Name, source.LogBytes, cold.Cost, warm.Cost));
            Console.WriteLine($"{source.Name} {source.LogBytes / 1024}KB: 进程热/缓存冷 {cold.Cost?.FirstPaintMs}ms, 再开一次 {warm.Cost?.FirstPaintMs}ms");
            Console.Out.Flush();
            try { Directory.Delete(root, recursive: true); } catch (IOException) { }
        }
        var directory = Path.Combine(TempTree.RepositoryRoot, "artifacts");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, $"session-open-warm-process-{DateTime.Now:yyyyMMdd-HHmmss}.csv");
        var text = new StringBuilder("session,logBytes,warmProcess_coldCache_firstPaint_ms,warmProcess_cached_firstPaint_ms,"
            + "warmProcess_coldCache_total_ms,warmProcess_cached_total_ms,stall_ms").AppendLine();
        foreach (var (name, bytes, coldCost, cachedCost) in rows)
        {
            text.Append(name).Append(',').Append(bytes).Append(',')
                .Append(coldCost?.FirstPaintMs.ToString(CultureInfo.InvariantCulture) ?? Missing).Append(',')
                .Append(cachedCost?.FirstPaintMs.ToString(CultureInfo.InvariantCulture) ?? Missing).Append(',')
                .Append(coldCost?.TotalMs.ToString(CultureInfo.InvariantCulture) ?? Missing).Append(',')
                .Append(cachedCost?.TotalMs.ToString(CultureInfo.InvariantCulture) ?? Missing).Append(',')
                .Append(Math.Max(coldCost?.StallMs ?? -1, cachedCost?.StallMs ?? -1).ToString(CultureInfo.InvariantCulture))
                .AppendLine();
        }
        await File.WriteAllTextAsync(path, text.ToString());
        Console.WriteLine($"CSV: {path}");
    }, TimeSpan.FromMinutes(90));

    /** 语料里最大的 N 份(默认 8): 用户掐秒表时看的就是这些。 */
    private static List<SourceSession> TopBigSessions()
    {
        var count = int.TryParse(Environment.GetEnvironmentVariable("DSH_ACCEPT_TOP"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) && parsed > 0
            ? parsed
            : 8;
        return [.. Distinct(EnumerateCorpus()).OrderByDescending(source => source.LogBytes).Take(count)];
    }

    /** 一份会话的冷热两态。 */
    private static async Task<Row> MeasureAsync(
        GuiTestEnvironment environment, SourceSession source, CancellationToken cancellationToken)
    {
        var cache = ProjectionCache.ForHome(environment.App.Home.Root);
        var log = Stage(environment, source);
        var sessionRoot = Path.GetDirectoryName(log)!;
        try
        {
            ClearDerived(log, cache, source);
            var cold = await OpenAsync(environment, source, cache, cancellationToken);
            Opens.Add(cold);
            if (cold.Cost is null)
                return Row.Failed(source, cold, []);
            // 冷态这次打开会让宿主写出折叠检查点: 热态是否命中它, 就看副本里有没有这个旁车。
            var checkpoint = Directory.EnumerateFiles(sessionRoot, SurfacePattern).Any();

            var warm = new List<Open>(WarmRepeats);
            for (var attempt = 0; attempt < WarmRepeats; attempt += 1)
            {
                var open = await OpenAsync(environment, source, cache, cancellationToken);
                warm.Add(open);
                Opens.Add(open);
            }
            return Row.Completed(source, cold, warm, checkpoint);
        }
        finally
        {
            // 界面不在这里释放: MainViewModel.Dispose 会连同进程内共享的 FleetWorkspace 一起收掉,
            // 之后任何一次打开都握不到宿主。断开引用交给 GC, 并显式收一次, 免得大会话的界面累积。
            Opens.Clear();
            GC.Collect(2, GCCollectionMode.Forced, blocking: true);
            GC.WaitForPendingFinalizers();
            try { Directory.Delete(sessionRoot, recursive: true); } catch (IOException) { }
            _ = cancellationToken;
        }
    }

    /** 把语料复制进宿主 home(生产布局), 返回副本里的日志路径。 */
    private static string Stage(GuiTestEnvironment environment, SourceSession source)
    {
        var root = Path.Combine(environment.App.Home.Root, "sessions", source.Project, source.DirectoryName);
        Directory.CreateDirectory(root);
        foreach (var file in Directory.EnumerateFiles(source.DirectoryPath))
            File.Copy(file, Path.Combine(root, Path.GetFileName(file)), overwrite: true);
        return Path.Combine(root, LogFileName);
    }

    /** 冷态: 折叠检查点与客户端投影缓存全清。 */
    private static void ClearDerived(string logPath, ProjectionCache cache, SourceSession source)
    {
        foreach (var sidecar in Directory.EnumerateFiles(Path.GetDirectoryName(logPath)!, SurfacePattern))
            File.Delete(sidecar);
        var projection = cache.PathFor(MachineSpec.LocalName, source.DirectoryName);
        if (File.Exists(projection))
            File.Delete(projection);
    }

    /** 打开一次(真实 GUI 路径), 等首帧与可输入都记下来。 */
    private static async Task<Open> OpenAsync(
        GuiTestEnvironment environment, SourceSession source, ProjectionCache cache, CancellationToken cancellationToken)
    {
        var projection = cache.PathFor(MachineSpec.LocalName, source.DirectoryName);
        var cacheMtime = File.Exists(projection) ? File.GetLastWriteTimeUtc(projection) : (DateTime?)null;
        try
        {
            var chat = await environment.OpenChatAsync(SessionId.Create(source.DirectoryName), cache);
            var viewModel = new MainViewModel(environment.App, chat);
            await GuiTestEnvironment.WaitForAsync(viewModel, () => viewModel.LastOpenCost is { InteractiveMs: >= 0 });
            await Task.Delay(CacheSettleMs, cancellationToken);
            var cost = viewModel.LastOpenCost!.Value;
            var rewritten = cacheMtime is { } before && File.Exists(projection)
                && File.GetLastWriteTimeUtc(projection) != before;
            return new Open(cost, chat.HistoryIncomplete is null, rewritten);
        }
        catch (Exception error)
        {
            return new Open(null, false, false) { Failure = Reason(error) };
        }
    }

    private static string Reason(Exception error)
        => $"{error.GetType().Name}: {error.Message}".Replace(',', ';').ReplaceLineEndings(" ");

    private static IEnumerable<SourceSession> EnumerateCorpus()
    {
        foreach (var root in CorpusRoots())
            foreach (var project in Directory.EnumerateDirectories(root))
                foreach (var session in Directory.EnumerateDirectories(project))
                {
                    var log = Path.Combine(session, LogFileName);
                    if (!File.Exists(log))
                        continue;
                    yield return new SourceSession(
                        $"{Path.GetFileName(project)}/{Path.GetFileName(session)}",
                        Path.GetFileName(project),
                        Path.GetFileName(session),
                        session,
                        new FileInfo(log).Length);
                }
    }

    /** 副本语料是主语料的字节复制: 同一 (项目, 会话) 且日志字节数相同的只测一次; 内容不同的 fork 照测。 */
    private static List<SourceSession> Distinct(IEnumerable<SourceSession> sessions)
    {
        var seen = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        var kept = new List<SourceSession>();
        foreach (var session in sessions)
        {
            var key = $"{session.Project}/{session.DirectoryName}";
            if (seen.TryGetValue(key, out var bytes) && bytes == session.LogBytes)
                continue;
            seen[key] = session.LogBytes;
            kept.Add(session);
        }
        return kept;
    }

    private static List<string> CorpusRoots()
    {
        if (Environment.GetEnvironmentVariable("DSH_ACCEPT_CORPUS") is { Length: > 0 } overridden)
            return [overridden];
        return [.. new List<string>
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".dsh", "sessions"),
            Path.Combine(TempTree.RepositoryRoot, "artifacts", "real-sessions-copy", "sessions"),
        }.Where(Directory.Exists)];
    }

    private static int Limit()
        => int.TryParse(Environment.GetEnvironmentVariable("DSH_ACCEPT_LIMIT"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var limit) && limit > 0
            ? limit
            : int.MaxValue;

    /** 新建 CSV 并写表头; 之后每测完一份就追加一行, 被打断也留得下已测结果。 */
    private static async Task<string> BeginCsvAsync()
    {
        var directory = Path.Combine(TempTree.RepositoryRoot, "artifacts");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, $"session-open-acceptance-{DateTime.Now:yyyyMMdd-HHmmss}.csv");
        await File.WriteAllTextAsync(path, Header + Environment.NewLine);
        return path;
    }

    private static string TierOf(long bytes) => bytes switch
    {
        < 2L * 1024 * 1024 => "<2MB",
        < 20L * 1024 * 1024 => "2-20MB",
        < 100L * 1024 * 1024 => "20-100MB",
        _ => ">100MB",
    };

    private static string N(long? value) => value?.ToString(CultureInfo.InvariantCulture) ?? Missing;
    private static string F(bool value) => value ? "true" : "false";

    internal sealed record SourceSession(string Name, string Project, string DirectoryName, string DirectoryPath, long LogBytes);

    private sealed record Open(OpenCostResult? Cost, bool HistoryComplete, bool CacheRewritten)
    {
        public string Failure { get; init; } = "";
    }

    private sealed record Row(
        SourceSession Source,
        Open Cold,
        IReadOnlyList<Open> Warm,
        bool CheckpointHit,
        bool R1,
        bool R3,
        bool R5,
        string Verdict)
    {
        public bool Passed => R1 && R3 && R5 && Verdict == "ok";

        public static Row Failed(SourceSession source, Open cold, IReadOnlyList<Open> warm)
            => new(source, cold, warm, false, false, false, false, cold.Failure.Length > 0 ? cold.Failure : "open failed");

        public static Row Completed(SourceSession source, Open cold, IReadOnlyList<Open> warm, bool checkpoint)
        {
            var failures = new List<string>();
            var warmCosts = warm.Where(item => item.Cost is not null).Select(item => item.Cost!.Value).ToList();
            var coldCost = cold.Cost!.Value;

            // R1: 冷热首屏预算。
            var r1 = coldCost.FirstPaintMs is >= 0 and <= ColdFirstPaintBudgetMs;
            if (!r1)
                failures.Add($"冷态 firstPaint {N(coldCost.FirstPaintMs)}ms > {ColdFirstPaintBudgetMs}ms");
            foreach (var cost in warmCosts.Where(cost => cost.FirstPaintMs is < 0 or > WarmFirstPaintBudgetMs))
            {
                r1 = false;
                failures.Add($"热态 firstPaint {N(cost.FirstPaintMs)}ms > {WarmFirstPaintBudgetMs}ms");
            }

            // R3: 最长主线程阻塞; 命中缓存且无新事件时不重写投影。
            var r3 = coldCost.StallMs <= StallBudgetMs && warmCosts.All(cost => cost.StallMs <= StallBudgetMs);
            if (coldCost.StallMs > StallBudgetMs)
                failures.Add($"冷态 stall {N(coldCost.StallMs)}ms > {StallBudgetMs}ms");
            foreach (var cost in warmCosts.Where(cost => cost.StallMs > StallBudgetMs))
            {
                r3 = false;
                failures.Add($"热态 stall {N(cost.StallMs)}ms > {StallBudgetMs}ms");
            }
            var rewritten = warm.Skip(1).Where(item => item.CacheRewritten).ToList();
            if (rewritten.Count > 0)
            {
                r3 = false;
                failures.Add($"热态重开又重写了投影缓存 {rewritten.Count} 次");
            }

            // R5: 热态 total 不随次数上升。
            var r5 = warmCosts.Count == WarmRepeats
                && warmCosts[^1].TotalMs <= warmCosts[0].TotalMs * WarmGrowthFactor + StallBudgetMs;
            if (!r5)
                failures.Add($"热态 total 递增: {string.Join('/', warmCosts.Select(cost => N(cost.TotalMs)))}ms");

            if (!cold.HistoryComplete)
                failures.Add("打开后历史不完整");

            var verdict = failures.Count == 0 ? "ok" : string.Join("; ", failures).Replace(',', ';');
            return new Row(source, cold, warm, checkpoint, r1, r3, r5, verdict);
        }

        public string ToCsv()
        {
            var cold = Cold.Cost;
            var warm = Warm.Where(item => item.Cost is not null).Select(item => item.Cost!.Value).ToList();
            var first = warm.Count > 0 ? warm[0] : default;
            var last = warm.Count > 0 ? warm[^1] : default;
            return string.Join(",",
                Source.Name, Source.LogBytes, N(cold?.Events), TierOf(Source.LogBytes),
                N(cold?.FirstPaintMs), N(cold?.InteractiveMs), N(cold?.StallMs), N(cold?.TotalMs),
                warm.Count > 0 ? N(first.FirstPaintMs) : Missing,
                warm.Count > 0 ? N(first.InteractiveMs) : Missing,
                warm.Count > 0 ? N(first.StallMs) : Missing,
                warm.Count > 0 ? N(first.TotalMs) : Missing,
                warm.Count > 0 ? N(last.TotalMs) : Missing,
                warm.Count.ToString(CultureInfo.InvariantCulture),
                F(Warm.Skip(1).Any(item => item.CacheRewritten)),
                F(CheckpointHit), F(Cold.HistoryComplete), F(R1), F(R3), F(R5),
                Verdict.Replace(',', ';'));
        }
    }
}
