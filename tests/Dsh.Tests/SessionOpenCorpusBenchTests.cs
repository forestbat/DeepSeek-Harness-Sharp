using System.Diagnostics;
using System.Globalization;
using System.Text;
using Dsh.Core;
using Dsh.Llm;
using Dsh.Persistence;

namespace Dsh.Tests;

/**
 * 打开性能基准: 对语料里**每一份**真实会话各打开一次(冷、热两态), 逐份一行落 CSV(方案 §6.1 / §6.2 / M8)。
 *
 * 冷态 = 会话目录原样复制进隔离副本, 再删掉 `*.surface`(宿主落盘的折叠检查点): 崩溃后第一次打开, 今天最坏的情形。
 * 热态 = 同一份副本上把语料自带的 `*.surface` 放回去再打开一次: 日常重开、切会话再切回来。
 * 每次测量都新建 JsonlSessionPersistence, 进程内解析缓存不跨测量复用, 因此冷热差异只来自检查点与磁盘页缓存。
 *
 * 打开走产品恢复路径: TryOpenWindow → SessionRepair.InterruptedTurnClosers → TryLoadSurfaceFold → Session.FromWindow,
 * 不能窗口化时 handle.Read → Session.FromRestore(与 AgentLoop.Restore 同序; 它是 private, 测试只能按同一顺序调用同一批产品函数)。
 * 折叠、解析、修复全部在产品代码里, 测试不重写。
 *
 * 列的真实性:
 * - 真测: open_ms(点到日志 → 历史可用)、total_ms(再加上第一次触碰表面的 DeriveMessages)、events、
 *   checkpointHit、historyIncomplete(Session.FoldSnapshot() 为 null, 即表面没折到日志末尾)、cold_allocMB。
 * - n/a: host_ms(宿主进程没起)、firstPaint_ms / interactive_ms(没有 GUI 帧与输入框)、projectionCacheHit
 *   (投影缓存是 GUI 侧概念, 本路径不读它) —— 方案 §6.3 要求这些先打点(M6), 本基准量不到就不编。
 *
 * 语料: DSH_BENCH_CORPUS 覆盖根目录, 默认主语料 ~/.dsh/sessions 加副本语料 artifacts/real-sessions-copy/sessions(存在才用)。
 * 只读: 原始会话只被复制, 不会被写。语料是真实日志, 本基准不生成合成日志。
 * DSH_BENCH_LIMIT 限制最多测几份(按日志体积升序取前 N 份), 便于快速跑; CSV 写到 artifacts/session-open-corpus-时间戳.csv。
 */
[Collection("Bench")]
[Trait("Category", "Bench")]
public sealed class SessionOpenCorpusBenchTests
{
    /** 窗口化恢复的尾窗大小: 与 AgentLoop.Restore 的 ResumeTailEvents 一致。 */
    private const int ResumeTailEvents = 20_000;
    private const long Megabyte = 1024 * 1024;
    private const int PrintedRows = 12;
    private const string LogFileName = "session.jsonl.zstd";
    private const string SurfacePattern = "*.surface";
    private const string OkVerdict = "ok";
    private const string Unavailable = "n/a";
    private const string Missing = "-";
    private const string TinyTier = "<2MB";
    private const string SmallTier = "2-20MB";
    private const string MediumTier = "20-100MB";
    private const string HugeTier = ">100MB";
    private const string Header = "session,logBytes,events,tier,cold_host_ms,cold_open_ms,cold_firstPaint_ms,cold_interactive_ms,cold_total_ms,cold_allocMB,warm_open_ms,warm_firstPaint_ms,warm_interactive_ms,warm_total_ms,checkpointHit,projectionCacheHit,historyIncomplete,verdict";

    /** 分档只用于看趋势(方案 §6.1): 判定仍逐份进行。 */
    private static readonly string[] Tiers = [TinyTier, SmallTier, MediumTier, HugeTier];

    [Fact]
    public async Task OpeningEveryRealSession_RecordsColdAndWarmIntoCsv()
    {
        var sources = EnumerateCorpus().OrderBy(source => source.LogBytes).Take(Limit()).ToList();
        Assert.NotEmpty(sources);
        // 进程第一次打开会把 zstd 与持久化路径的一次性 JIT 摊到那一行上: 先用最小的一份空跑一遍并丢弃(语料行一份不少)。
        _ = Measure(sources[0]);
        var rows = sources.Select(Measure).ToList();
        var path = await WriteAsync(rows, TestContext.Current.CancellationToken);
        await ReportAsync(rows, path, TestContext.Current.CancellationToken);
        Assert.Equal(sources.Count, rows.Count);
        Assert.True(rows.Exists(row => row.Ok), $"没有一份会话能打开, CSV: {path}");
    }

    /** 同一份副本先冷后热各打开一次: 冷态删掉 `*.surface`, 热态把语料自带的 `*.surface` 放回去。 */
    private static Row OpenBothStates(SourceSession source, string work)
    {
        var target = Path.Combine(work, source.Project, source.DirectoryName);
        var (cold, coldFailure) = Attempt(() =>
        {
            CopyFiles(source.DirectoryPath, target);
            // 派生缓存(折叠检查点)全删; 投影缓存在 GUI 家里, 本路径根本不读它, 没有可清的。
            foreach (var sidecar in Directory.EnumerateFiles(target, SurfacePattern))
                File.Delete(sidecar);
            return Restore(work, source.DirectoryName);
        });
        if (cold is null)
            return Row.Failed(source, null, null, $"cold:{coldFailure}");
        var (warm, warmFailure) = Attempt(() =>
        {
            CopyFiles(source.DirectoryPath, target, SurfacePattern);
            return Restore(work, source.DirectoryName);
        });
        return warm is null ? Row.Failed(source, cold, null, $"warm:{warmFailure}") : Row.Completed(source, cold, warm);
    }

    /** 一次打开: 产品恢复路径 + 第一次触碰表面。open_ms 到"历史可用", total_ms 到同步路径结束。 */
    private static Measurement Restore(string copyRoot, string sessionDirectory)
    {
        var id = SessionId.Create(sessionDirectory);
        var allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
        var clock = Stopwatch.StartNew();
        using var store = new JsonlSessionPersistence(copyRoot);
        using var handle = store.Open(id, SessionAccess.Read);
        var (session, checkpointHit) = RestoreSession(store, id, handle);
        var openMs = clock.ElapsedMilliseconds;
        // 第一次触碰表面: 前缀页回读与折叠都发生在这里(方案 §4.2), 这正是用户等到内容的那一刻。
        var messages = session.DeriveMessages();
        var totalMs = clock.ElapsedMilliseconds;
        var allocated = GC.GetTotalAllocatedBytes(precise: true) - allocatedBefore;
        return new Measurement(openMs, totalMs, session.Seq, checkpointHit, session.FoldSnapshot() is null, allocated, messages.Count);
    }

    /**
     * 与 AgentLoop.Restore 同序: **有折叠检查点才窗口化**(只读尾窗并采用检查点), 否则整份恢复。
     * 判断与折叠全在产品里 —— 这里只是按同一顺序调用同一批产品函数。
     */
    private static (Session Session, bool CheckpointHit) RestoreSession(ISessionPersistence persistence, SessionId id, ISessionHandle handle)
    {
        var adopted = persistence.TryLoadSurfaceFold(id);
        if (adopted is not null && persistence.TryOpenWindow(id, ResumeTailEvents) is { } paged)
        {
            // 中断收尾只看尾部; 第二个参数是"传入列表内的偏移", 窗口会话要按 (inherited - ResidentFrom) 跳过 fork 冻结前缀。
            var closers = SessionRepair.InterruptedTurnClosers(
                paged.Resident, Math.Max(0, handle.InheritedEventCount - paged.ResidentFrom));
            var resident = closers.Count > 0 ? [.. paged.Resident, .. closers] : paged.Resident;
            var window = paged with { Resident = resident, TotalEvents = paged.TotalEvents + closers.Count };
            return (Session.FromWindow(id, window, handle.Header, handle.InheritedEventCount, adopted), true);
        }
        var persisted = handle.Read();
        var fullClosers = SessionRepair.InterruptedTurnClosers(persisted, handle.InheritedEventCount);
        var events = fullClosers.Count > 0 ? [.. persisted, .. fullClosers] : persisted;
        return (Session.FromRestore(id, events, handle.Header, handle.InheritedEventCount), false);
    }

    private static Row Measure(SourceSession source)
    {
        var work = TempTree.CreateDirectory("session-open-corpus");
        try
        {
            return OpenBothStates(source, work);
        }
        finally
        {
            TempTree.Delete(work);
        }
    }

    private static (Measurement? Value, string Failure) Attempt(Func<Measurement> measure)
    {
        try
        {
            return (measure(), string.Empty);
        }
        catch (Exception error)
        {
            return (null, Reason(error));
        }
    }

    /** 一行以内说清原因: CSV 用逗号分列, 逗号与换行必须净化。 */
    private static string Reason(Exception error)
        => $"{error.GetType().Name}: {error.Message}".Replace(',', ';').ReplaceLineEndings(" ");

    /** 只复制文件、布局原样保留: 目录名就是 header 里的 project 键与会话号, 生产代码据此解析路径。 */
    private static void CopyFiles(string from, string to, string pattern = "*")
    {
        Directory.CreateDirectory(to);
        foreach (var file in Directory.EnumerateFiles(from, pattern))
            File.Copy(file, Path.Combine(to, Path.GetFileName(file)), overwrite: true);
    }

    /** 语料根: DSH_BENCH_CORPUS 覆盖; 默认主语料加副本语料, 只列存在的根。 */
    private static List<(string Label, string Root)> CorpusRoots()
    {
        if (Environment.GetEnvironmentVariable("DSH_BENCH_CORPUS") is { Length: > 0 } overridden)
            return [("corpus", overridden)];
        var candidates = new List<(string Label, string Root)>
        {
            ("home", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".dsh", "sessions")),
            ("copy", Path.Combine(TempTree.RepositoryRoot, "artifacts", "real-sessions-copy", "sessions"))
        };
        return [.. candidates.Where(candidate => Directory.Exists(candidate.Root))];
    }

    private static List<SourceSession> EnumerateCorpus()
    {
        var roots = CorpusRoots();
        var sessions = new List<SourceSession>();
        for (var index = 0; index < roots.Count; index += 1)
        {
            // 第一个根是主语料, 会话名不加后缀; 其它根(副本语料)加 `@标签`, 便于同名但内容不同的副本区分。
            sessions.AddRange(EnumerateRoot(roots[index].Root, index == 0 ? null : $"@{roots[index].Label}"));
        }
        return Distinct(sessions);
    }

    /** 语料布局与生产一致(根目录/项目键/会话号/session.jsonl.zstd)。只枚举, 不碰原始日志。 */
    private static IEnumerable<SourceSession> EnumerateRoot(string root, string? suffix)
    {
        foreach (var project in Directory.EnumerateDirectories(root))
            foreach (var session in Directory.EnumerateDirectories(project))
            {
                var log = Path.Combine(session, LogFileName);
                if (!File.Exists(log))
                    continue;
                var name = $"{Path.GetFileName(project)}/{Path.GetFileName(session)}{suffix}";
                yield return new SourceSession(
                    name, Path.GetFileName(project), Path.GetFileName(session), session, new FileInfo(log).Length);
            }
    }

    /** 副本语料是主语料的字节复制: project/会话号相同且日志字节数相同的会话不重复测; 内容不同的 fork 副本照测。 */
    private static List<SourceSession> Distinct(List<SourceSession> sessions)
    {
        var seen = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        var kept = new List<SourceSession>(sessions.Count);
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

    /** DSH_BENCH_LIMIT: 最多测几份, 缺省不限制。 */
    private static int Limit()
        => int.TryParse(
            Environment.GetEnvironmentVariable("DSH_BENCH_LIMIT"),
            NumberStyles.Integer,
            CultureInfo.InvariantCulture,
            out var limit) && limit > 0
            ? limit
            : int.MaxValue;

    private static string TierOf(long logBytes) => logBytes switch
    {
        < 2 * Megabyte => TinyTier,
        < 20 * Megabyte => SmallTier,
        < 100 * Megabyte => MediumTier,
        _ => HugeTier
    };

    private static string Number(long? value) => value?.ToString(CultureInfo.InvariantCulture) ?? Missing;

    private static string Megabytes(long? bytes)
        => bytes is { } total
            ? Math.Round(total / (double)Megabyte, 1).ToString(CultureInfo.InvariantCulture)
            : Missing;

    private static string Flag(bool? value) => value is null ? Missing : value.Value ? "true" : "false";

    /** CSV 落到 artifacts/ 下, 时间戳后缀便于逐次对比; 列与方案 §6.1 一一对应。 */
    private static async Task<string> WriteAsync(IReadOnlyList<Row> rows, CancellationToken cancellationToken)
    {
        var directory = Path.Combine(TempTree.RepositoryRoot, "artifacts");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, $"session-open-corpus-{DateTime.Now:yyyyMMdd-HHmmss}.csv");
        var csv = new StringBuilder();
        csv.AppendLine(Header);
        foreach (var row in rows)
            csv.AppendLine(row.ToCsv());
        await File.WriteAllTextAsync(path, csv.ToString(), cancellationToken);
        return path;
    }

    private static async Task ReportAsync(IReadOnlyList<Row> rows, string csvPath, CancellationToken cancellationToken)
    {
        var report = new StringBuilder();
        report.AppendLine(Header);
        foreach (var row in rows.Take(PrintedRows))
            report.AppendLine(row.ToCsv());
        if (rows.Count > PrintedRows)
            report.AppendLine($"… 其余 {rows.Count - PrintedRows} 行见 CSV");
        report.AppendLine();
        AppendSummary(report, rows);
        report.AppendLine($"CSV: {csvPath}");
        await Console.Out.WriteAsync(report.ToString(), cancellationToken);
    }

    private static void AppendSummary(StringBuilder report, IReadOnlyList<Row> rows)
    {
        var failed = rows.Where(row => !row.Ok).ToList();
        report.AppendLine($"共 {rows.Count} 行(逐份一行, 一份未剔): 打开成功 {rows.Count - failed.Count}, 打不开 {failed.Count}");
        AppendTiers(report, rows);
        foreach (var row in rows.Where(row => row.HistoryIncomplete is true))
            report.AppendLine($"  历史不完整: {row.Session}");
        foreach (var row in failed)
            report.AppendLine($"  打不开: {row.Session} -> {row.Verdict}");
        var messages = rows.Where(row => row.Ok).Select(row => row.Cold!.Messages).ToList();
        if (messages.Count > 0)
            report.AppendLine($"  冷态派生消息条数: 最少 {messages.Min()}, 最多 {messages.Max()}");
    }

    /** 逐档计数与最差 total: 分档只看趋势, R1 的判定用逐份那一列, 不是这里的数。 */
    private static void AppendTiers(StringBuilder report, IReadOnlyList<Row> rows)
    {
        foreach (var tier in Tiers)
        {
            var inTier = rows.Where(row => row.Tier == tier).ToList();
            var opened = inTier.Where(row => row.Ok).ToList();
            var cold = opened.Count == 0 ? Missing : Number(opened.Max(row => row.Cold!.TotalMs));
            var warm = opened.Count == 0 ? Missing : Number(opened.Max(row => row.Warm!.TotalMs));
            var hits = opened.Count(row => row.CheckpointHit is true);
            report.AppendLine(
                $"  {tier}: {inTier.Count} 份, 打开成功 {opened.Count}, 打不开 {inTier.Count - opened.Count}, "
                + $"最差冷 total {cold}ms / 最差热 total {warm}ms, 热态命中检查点 {hits} 份");
        }
    }

    /** 语料里的一份会话: 只读的原始目录 + 用来落 CSV 与做副本的名字。 */
    private sealed record SourceSession(
        string Display, string Project, string DirectoryName, string DirectoryPath, long LogBytes);

    /** 一次打开的实测值。Messages 只用于汇总核对, 不进 CSV(列由方案 §6.1 定死)。 */
    private sealed record Measurement(
        long OpenMs,
        long TotalMs,
        long Events,
        bool CheckpointHit,
        bool HistoryIncomplete,
        long AllocatedBytes,
        int Messages);

    /** CSV 的一行。Cold/Warm 为 null 表示这一态没打开(原因见 Verdict)。 */
    private sealed record Row(
        string Session,
        long LogBytes,
        string Tier,
        Measurement? Cold,
        Measurement? Warm,
        string Verdict)
    {
        public bool Ok => Verdict == OkVerdict;

        /** 热态是否命中检查点: 冷态按构造恒为 false(检查点被删掉了), 所以这一列报热态。 */
        public bool? CheckpointHit => Warm?.CheckpointHit;

        /** 两态里只要有一态的表面没折到日志末尾, 这一行就算"历史不完整"。 */
        public bool? HistoryIncomplete => Cold is null && Warm is null
            ? null
            : (Cold?.HistoryIncomplete ?? false) || (Warm?.HistoryIncomplete ?? false);

        public string ToCsv() => string.Join(
            ',',
            Session,
            LogBytes.ToString(CultureInfo.InvariantCulture),
            (Cold?.Events ?? Warm?.Events) is { } events ? events.ToString(CultureInfo.InvariantCulture) : Missing,
            Tier,
            Unavailable,
            Number(Cold?.OpenMs),
            Unavailable,
            Unavailable,
            Number(Cold?.TotalMs),
            Megabytes(Cold?.AllocatedBytes),
            Number(Warm?.OpenMs),
            Unavailable,
            Unavailable,
            Number(Warm?.TotalMs),
            Flag(CheckpointHit),
            Unavailable,
            Flag(HistoryIncomplete),
            Verdict);

        public static Row Completed(SourceSession source, Measurement cold, Measurement warm)
            => new(source.Display, source.LogBytes, TierOf(source.LogBytes), cold, warm, OkVerdict);

        public static Row Failed(SourceSession source, Measurement? cold, Measurement? warm, string reason)
            => new(source.Display, source.LogBytes, TierOf(source.LogBytes), cold, warm, $"unreadable:{reason}");
    }
}
