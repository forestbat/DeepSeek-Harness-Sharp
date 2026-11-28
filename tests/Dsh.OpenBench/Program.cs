using System.Diagnostics;
using System.Globalization;
using Dsh.Agent;
using Dsh.Core;
using Dsh.Llm;
using Dsh.Persistence;
using Dsh.Persistence.Database;

namespace Dsh.OpenBench;

/**
 * 打开性能基准(命令行版): 对真实语料里超过给定体积的会话, 分别量 JSONL 与数据库两条链路的冷/热打开。
 *
 * 参数一律走命令行(仓库规则: 不用环境变量做配置):
 *   Dsh.OpenBench --corpus <会话根> [--min-mb <N>] [--only <会话名子串>] [--limit <N>]
 *                [--db <复用的库文件>] [--skip-jsonl] [--skip-db] [--csv <路径>]
 *
 * 冷态 = 没有检查点(`*.surface` / `surface_fold`)的第一次打开; 热态 = 按产品路径写一次检查点后再打开。
 * 打开走 `SessionRestore.Restore`(宿主/TUI/GUI 共用), 再用 `DeriveMessages()` 触碰表面。
 * 两条链路默认分开量: 同一进程里先跑 JSONL 冷开会留下 GB 级垃圾, 后面的数据库测量会被 GC 压力带偏。
 */
internal static class Program
{
    private const long Megabyte = 1024 * 1024;

    private static int Main(string[] args)
    {
        var options = Options.Parse(args);
        if (options is null)
        {
            Console.Error.WriteLine(
                "用法: Dsh.OpenBench --corpus <会话根> [--min-mb N] [--only 子串] [--limit N] [--db 复用库] [--skip-jsonl] [--skip-db] [--csv 路径]");
            return 2;
        }

        // --import-only: 只把语料导入库(单独的进程), 让"冷开"在一个没有导入垃圾的进程里量。
        if (options.ImportOnly)
        {
            if (options.ReuseDatabase is not { Length: > 0 } target)
            {
                Console.Error.WriteLine("--import-only 需要 --db <库文件>");
                return 2;
            }
            using var store = new DatabaseSessionPersistence(target);
            var clock = Stopwatch.StartNew();
            var imported = store.ImportFrom(options.Corpus);
            Console.WriteLine($"imported {imported} session(s) into {target} in {clock.ElapsedMilliseconds}ms, "
                + $"{new FileInfo(target).Length / 1024 / 1024}MB");
            return 0;
        }

        var sources = Enumerate(options).ToList();
        if (sources.Count == 0)
        {
            Console.Error.WriteLine($"在 {options.Corpus} 下没有找到 ≥{options.MinMegabytes}MB 的会话");
            return 2;
        }
        var work = Path.Combine(Path.GetTempPath(), $"dsh-open-bench-{Guid.NewGuid():N}");
        Directory.CreateDirectory(work);
        try
        {
            Console.WriteLine(Header);
            var rows = new List<Row>();
            // 首次调用把 JIT 摊到第一行上: 先用最小的一份空跑并丢弃。
            _ = Measure(sources[0], work, options);
            foreach (var source in sources)
            {
                var row = Measure(source, work, options);
                rows.Add(row);
                Console.WriteLine(row.ToCsv());
            }
            if (options.CsvPath is { Length: > 0 } csvPath)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(csvPath))!);
                File.WriteAllLines(csvPath, [Header, .. rows.Select(row => row.ToCsv())]);
                Console.WriteLine($"CSV: {csvPath}");
            }
        }
        finally
        {
            try
            {
                Directory.Delete(work, recursive: true);
            }
            catch (IOException)
            {
            }
        }
        return 0;
    }

    private const string Header =
        "session,logMB,events,frames,jsonl_cold_open_ms,jsonl_cold_total_ms,jsonl_warm_open_ms,jsonl_warm_total_ms,"
        + "db_import_ms,db_MB,db_cold_open_ms,db_cold_total_ms,db_warm_open_ms,db_warm_total_ms,"
        + "jsonl_cold_allocMB,db_cold_allocMB,jsonl_warm_faults,db_warm_faults,jsonl_warm_from,db_warm_from,db_warm_fold_nodes,warm_windowed,verdict";

    private static Row Measure(SourceSession source, string work, Options options)
    {
        var copyRoot = Path.Combine(work, "sessions", source.Project, source.DirectoryName);
        CopyFiles(source.DirectoryPath, copyRoot);
        var id = SessionId.Create(source.DirectoryName);
        Measurement? jsonlCold = null, jsonlWarm = null, dbCold = null, dbWarm = null;
        var importMs = 0L;
        var databaseBytes = 0L;
        var verdict = "ok";
        var probeFold = -1L;
        var probeWindow = -1L;

        if (!options.SkipJsonl)
        {
            using var jsonl = new JsonlSessionPersistence(Path.Combine(work, "sessions"), warn: _ => { });
            jsonlCold = options.WarmOnly
                ? null
                : Attempt(() =>
                {
                    foreach (var sidecar in Directory.EnumerateFiles(copyRoot, "*.surface"))
                        File.Delete(sidecar);
                    return Restore(jsonl, id);
                }, ref verdict);
            if (jsonlCold is { } cold)
                SaveCheckpoint(jsonl, id, cold.Session);
            if (jsonlCold is not null || options.WarmOnly)
                jsonlWarm = Attempt(() => Restore(jsonl, id), ref verdict);
        }

        if (!options.SkipDb)
        {
            var databasePath = options.ReuseDatabase is { Length: > 0 } reused
                ? reused
                : Path.Combine(Path.Combine(work, "sessions", source.Project), "sessions.db");
            using var database = new DatabaseSessionPersistence(databasePath, warn: _ => { });
            if (database.Stat(id) is null)
            {
                var clock = Stopwatch.StartNew();
                database.ImportFrom(Path.Combine(work, "sessions"));
                importMs = clock.ElapsedMilliseconds;
            }
            databaseBytes = File.Exists(databasePath) ? new FileInfo(databasePath).Length : 0;
            if (options.ForgetCheckpoint)
                database.DropSurfaceFold(id);
            dbCold = options.WarmOnly ? null : Attempt(() => Restore(database, id), ref verdict);
            if (dbCold is { } cold)
                SaveCheckpoint(database, id, cold.Session);
            // 热态之前先探一次: 检查点读得回来吗? 尾窗能给出来吗? (两者任一不成立, 热态就会退回整份读)
            probeFold = database.TryLoadSurfaceFold(id)?.Nodes.Count ?? -1;

            probeWindow = database.TryOpenWindow(id, SessionRestore.ResumeTailEvents)?.ResidentFrom ?? -1;
            if (dbCold is not null || options.WarmOnly)
                dbWarm = Attempt(() => Restore(database, id), ref verdict);
        }

        return new Row(source, jsonlCold, jsonlWarm, importMs, databaseBytes, dbCold, dbWarm, probeFold, probeWindow, verdict);
    }

    /** 产品恢复路径 + 第一次触碰表面; allocMB 是这一整段的托管分配增量。 */
    private static Measurement Restore(ISessionPersistence persistence, SessionId id)
    {
        var allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
        var clock = Stopwatch.StartNew();
        using var handle = persistence.Open(id, SessionAccess.Read);
        var session = SessionRestore.Restore(id, persistence, handle);
        var openMs = clock.ElapsedMilliseconds;
        var messages = session.DeriveMessages();
        var totalMs = clock.ElapsedMilliseconds;
        var allocated = GC.GetTotalAllocatedBytes(precise: true) - allocatedBefore;
        return new Measurement(session, openMs, totalMs, session.Seq, session.ResidentFrom > 0, allocated, messages.Count);
    }

    private static void SaveCheckpoint(ISessionPersistence persistence, SessionId id, Session session)
    {
        // 与宿主 resume 同一步: 恢复出来的会话可能比日志多出几条(合成收尾/end-seed), 先落盘再写检查点;
        // 否则检查点会指向尚未落盘的事件, 按规则被丢弃。
        PersistRestoredTail(persistence, id, session);
        // 表面被回退/压缩收干净的会话没有节点可存: 那种会话的"热态"本来就等于冷态。
        if (session.FoldSnapshot() is { Nodes.Count: > 0 } fold)
            persistence.SaveSurfaceFold(id, fold);
    }

    /** 把"内存里比日志多出来的那几条"补写进日志(与 `Plugin.Adopt` 的尾部同步同一条路径)。 */
    private static void PersistRestoredTail(ISessionPersistence persistence, SessionId id, Session session)
    {
        try
        {
            using var writer = persistence.Open(id, SessionAccess.Write);
            if (writer.Count >= session.Seq)
                return;
            writer.Append(session.SnapshotEvents(writer.Count));
            writer.Flush();
        }
        catch (SessionPersistenceException)
        {
            // 打不开写句柄(只读介质/别的写者): 检查点这次不写, 不算失败。
        }
    }

    private static Measurement? Attempt(Func<Measurement> measure, ref string verdict)
    {
        try
        {
            return measure();
        }
        catch (Exception error)
        {
            verdict = $"{error.GetType().Name}: {error.Message}".Replace(',', ';').ReplaceLineEndings(" ");
            return null;
        }
    }

    private static void CopyFiles(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var file in Directory.EnumerateFiles(from))
            File.Copy(file, Path.Combine(to, Path.GetFileName(file)), overwrite: true);
    }

    private static IEnumerable<SourceSession> Enumerate(Options options)
    {
        if (!Directory.Exists(options.Corpus))
            yield break;
        foreach (var project in Directory.EnumerateDirectories(options.Corpus))
            foreach (var session in Directory.EnumerateDirectories(project))
            {
                var log = Path.Combine(session, "session.jsonl.zstd");
                if (!File.Exists(log))
                    continue;
                var bytes = new FileInfo(log).Length;
                if (bytes < options.MinMegabytes * Megabyte)
                    continue;
                var name = $"{Path.GetFileName(project)}/{Path.GetFileName(session)}";
                if (options.Only is { Length: > 0 } only && !name.Contains(only, StringComparison.Ordinal))
                    continue;
                var frames = Path.Combine(session, "session.jsonl.zstd.frames");
                yield return new SourceSession(name, Path.GetFileName(project), Path.GetFileName(session), session, bytes,
                    File.Exists(frames) ? Math.Max(0, new FileInfo(frames).Length / 36 - 1) : -1);
            }
    }

    private sealed record SourceSession(
        string Display, string Project, string DirectoryName, string DirectoryPath, long LogBytes, long Frames);

    private sealed record Measurement(
        Session Session, long OpenMs, long TotalMs, long Events, bool Windowed, long AllocatedBytes, int Messages)
    {
        public long PageFaults => Session.PageFaults;
    }

    private sealed record Row(
        SourceSession Source,
        Measurement? JsonlCold,
        Measurement? JsonlWarm,
        long ImportMs,
        long DatabaseBytes,
        Measurement? DbCold,
        Measurement? DbWarm,
        long ProbeFold,
        long ProbeWindow,
        string Verdict)
    {
        public string ToCsv() => string.Join(
            ',',
            Source.Display,
            Megabytes(Source.LogBytes),
            Number(JsonlCold?.Events ?? DbCold?.Events),
            Source.Frames < 0 ? "-" : Source.Frames.ToString(CultureInfo.InvariantCulture),
            Number(JsonlCold?.OpenMs),
            Number(JsonlCold?.TotalMs),
            Number(JsonlWarm?.OpenMs),
            Number(JsonlWarm?.TotalMs),
            ImportMs == 0 ? "-" : ImportMs.ToString(CultureInfo.InvariantCulture),
            Megabytes(DatabaseBytes == 0 ? null : DatabaseBytes),
            Number(DbCold?.OpenMs),
            Number(DbCold?.TotalMs),
            Number(DbWarm?.OpenMs),
            Number(DbWarm?.TotalMs),
            Megabytes(JsonlCold?.AllocatedBytes),
            Megabytes(DbCold?.AllocatedBytes),
            Number(JsonlWarm?.PageFaults),
            Number(DbWarm?.PageFaults),
            Number(JsonlWarm?.Session.ResidentFrom),
            Number(DbWarm?.Session.ResidentFrom),
            Number(DbWarm?.Session.FoldSnapshot()?.Nodes.Count ?? 0),
            ProbeFold < 0 ? "-" : ProbeFold.ToString(CultureInfo.InvariantCulture),
            ProbeWindow < 0 ? "-" : ProbeWindow.ToString(CultureInfo.InvariantCulture),
            (JsonlWarm?.Windowed ?? false) || (DbWarm?.Windowed ?? false) ? "true" : "false",
            Verdict);
    }

    private static string Number(long? value) => value?.ToString(CultureInfo.InvariantCulture) ?? "-";

    private static string Megabytes(long? bytes)
        => bytes is { } total ? Math.Round(total / (double)Megabyte, 1).ToString(CultureInfo.InvariantCulture) : "-";

    /** 命令行参数: 全部显式给出, 不从环境变量读。 */
    private sealed record Options(
        string Corpus, int MinMegabytes, string? Only, int Limit, string? ReuseDatabase, bool SkipJsonl, bool SkipDb,
        string? CsvPath, bool ImportOnly, bool ForgetCheckpoint, bool WarmOnly)
    {
        public static Options? Parse(string[] args)
        {
            string? corpus = null;
            var minMegabytes = 0;
            string? only = null;
            var limit = int.MaxValue;
            string? reuse = null;
            var skipJsonl = false;
            var skipDb = false;
            var importOnly = false;
            var forgetCheckpoint = false;
            var warmOnly = false;
            string? csv = null;
            for (var index = 0; index < args.Length; index += 1)
            {
                switch (args[index])
                {
                    case "--corpus" when index + 1 < args.Length:
                        corpus = args[++index];
                        break;
                    case "--min-mb" when index + 1 < args.Length:
                        minMegabytes = int.Parse(args[++index], CultureInfo.InvariantCulture);
                        break;
                    case "--only" when index + 1 < args.Length:
                        only = args[++index];
                        break;
                    case "--limit" when index + 1 < args.Length:
                        limit = int.Parse(args[++index], CultureInfo.InvariantCulture);
                        break;
                    case "--db" when index + 1 < args.Length:
                        reuse = args[++index];
                        break;
                    case "--skip-jsonl":
                        skipJsonl = true;
                        break;
                    case "--skip-db":
                        skipDb = true;
                        break;
                    case "--csv" when index + 1 < args.Length:
                        csv = args[++index];
                        break;
                    case "--import-only":
                        importOnly = true;
                        break;
                    case "--forget-checkpoint":
                        forgetCheckpoint = true;
                        break;
                    case "--warm-only":
                        warmOnly = true;
                        break;
                    default:
                        return null;
                }
            }
            return corpus is null ? null : new Options(corpus, minMegabytes, only, limit, reuse, skipJsonl, skipDb, csv, importOnly, forgetCheckpoint, warmOnly);
        }
    }
}














