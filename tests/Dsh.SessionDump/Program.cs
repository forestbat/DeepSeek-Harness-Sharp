using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Dsh.Core;
using Dsh.Llm;
using Dsh.Persistence;
using Dsh.SessionDump;

/**
 * 把一棵会话目录树读成**可逐行比较的指纹**: 同一份日志分别用改动前后的代码跑一遍, 指纹一致就说明
 * "读到的事件、顺序、序号、负载、surface 操作"没变。
 *
 * 为什么不是逐事件全文: 一份 383MB 的日志里 chunk 文本占绝大多数, 全文转储有 379MB, 没法比。
 * 这里对每个事件算一行规范文本再滚动 SHA-256, 于是每份会话只剩几行(头、条数、类型分布、摘要、末几条样本),
 * 摘要一变就说明内容变了; 真要定位就把 `--detail <会话>` 的单份转储拿出来看。
 *
 * 只读: 打开用 SessionAccess.Read, 不写会话文件(索引可能被重建一次, 那不算会话内容)。
 * 用法: Dsh.SessionDump <sessionsRoot> [outFile] [--detail <会话 id>]
 */
internal static class Program
{
    /** 压缩探针的常驻尾窗: 与宿主恢复大会话时的量级一致。 */
    private const int CompactionProbeTailEvents = 20_000;

    private static int Main(string[] args)
    {
        if (args.Length == 0)
        {
            Console.Error.WriteLine("用法: Dsh.SessionDump <sessionsRoot> [outFile] [--detail <会话 id>]");
            return 2;
        }
        var root = args[0];
        var outFile = args.Length > 1 && !args[1].StartsWith("--", StringComparison.Ordinal) ? args[1] : null;
        var detailIndex = Array.IndexOf(args, "--detail");
        var detail = detailIndex >= 0 && detailIndex + 1 < args.Length ? args[detailIndex + 1] : null;
        var probeIndex = Array.IndexOf(args, "--compact-probe");
        if (probeIndex >= 0)
        {
            var tail = probeIndex + 1 < args.Length && int.TryParse(args[probeIndex + 1], out var parsed) ? parsed : CompactionProbeTailEvents;
            return CompactionProbe.Run(root, tail);
        }
        var framesIndex = Array.IndexOf(args, "--frames");
        if (framesIndex >= 0)
        {
            long? around = framesIndex + 1 < args.Length && long.TryParse(args[framesIndex + 1], out var at) ? at : null;
            var span = framesIndex + 2 < args.Length && int.TryParse(args[framesIndex + 2], out var spanFrames) ? spanFrames : 6;
            return FrameInspector.Run(root, around, span);
        }
        var resumeIndex = Array.IndexOf(args, "--resume-probe");
        if (resumeIndex >= 0)
        {
            if (resumeIndex + 1 >= args.Length)
            {
                Console.Error.WriteLine("用法: Dsh.SessionDump <sessionsRoot> --resume-probe <会话id> [尾窗事件数]");
                return 2;
            }
            var tail = resumeIndex + 2 < args.Length && int.TryParse(args[resumeIndex + 2], out var tailEvents) ? tailEvents : 20_000;
            return ResumeProbe.Run(root, args[resumeIndex + 1], tail);
        }
        var fullIndex = Array.IndexOf(args, "--full-probe");
        if (fullIndex >= 0)
        {
            if (fullIndex + 1 >= args.Length)
            {
                Console.Error.WriteLine("用法: Dsh.SessionDump <sessionsRoot> --full-probe <会话id>");
                return 2;
            }
            return ResumeProbe.Full(root, args[fullIndex + 1]);
        }
        var gapsIndex = Array.IndexOf(args, "--gap-probe");
        if (gapsIndex >= 0)
        {
            if (gapsIndex + 1 >= args.Length)
            {
                Console.Error.WriteLine("用法: Dsh.SessionDump <sessionsRoot> --gap-probe <会话id>");
                return 2;
            }
            return ResumeProbe.Gaps(root, args[gapsIndex + 1]);
        }
        var takeOverIndex = Array.IndexOf(args, "--take-over");
        if (takeOverIndex >= 0)
        {
            if (takeOverIndex + 1 >= args.Length)
            {
                Console.Error.WriteLine("用法: Dsh.SessionDump <sessionsRoot> --take-over <会话id>");
                return 2;
            }
            return ResumeProbe.TakeOver(root, args[takeOverIndex + 1]);
        }
        var ser = new StreamWriter(outFile, append: false, Encoding.UTF8);
        var output = outFile is null ? Console.Out : (TextWriter)ser;
        var count = 0;
        foreach (var (project, session, path) in Enumerate(root))
        {
            if (detail is not null && session != detail)
                continue;
            Report(output, root, project, session, path, verbose: detail is not null);
            output.Flush();
            count += 1;
            Console.Error.WriteLine($"[{count}] {project}/{session}");
        }
        ser?.Dispose();
        Console.Out.WriteLine($"# sessions: {count}");
        return 0;
    }

    private static IEnumerable<(string Project, string Session, string Path)> Enumerate(string root)
    {
        if (!Directory.Exists(root))
            yield break;
        foreach (var project in Directory.EnumerateDirectories(root).OrderBy(path => path, StringComparer.Ordinal))
        {
            foreach (var session in Directory.EnumerateDirectories(project).OrderBy(path => path, StringComparer.Ordinal))
            {
                var log = Path.Combine(session, "session.jsonl.zstd");
                if (File.Exists(log))
                    yield return (Path.GetFileName(project), Path.GetFileName(session), log);
            }
        }
    }

    private static void Report(TextWriter output, string root, string project, string session, string path, bool verbose)
    {
        output.WriteLine($"## {project}/{session}");
        using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var types = new SortedDictionary<string, int>(StringComparer.Ordinal);
        var surface = new SortedDictionary<string, int>(StringComparer.Ordinal);
        var samples = new Queue<string>();
        var events = 0;
        var warnings = new List<string>();
        try
        {
            using var persistence = new JsonlSessionPersistence(root, warn: message => warnings.Add(message));
            var id = SessionId.Create(Path.GetFileName(Path.GetDirectoryName(path)!));
            var snapshot = persistence.Stat(id);
            if (snapshot is null)
            {
                output.WriteLine("!! no header");
                return;
            }
            var header = snapshot.Header;
            output.WriteLine(
                $"header id={header.Id.Value} version={header.Version} seeded={header.IsSeeded} "
                + $"inherited={snapshot.InheritedEventCount} parent={header.ParentSession?.Value ?? "-"} "
                + $"size={snapshot.SizeBytes}");
            using var handle = persistence.Open(id, SessionAccess.Read);
            var list = handle.Read();
            events = list.Count;
            foreach (var sessionEvent in list)
            {
                var line = CanonicalLine(sessionEvent);
                hasher.AppendData(Encoding.UTF8.GetBytes(line + "\n"));
                types[sessionEvent.Type] = types.GetValueOrDefault(sessionEvent.Type) + 1;
                var operation = SurfaceOf(sessionEvent);
                surface[operation] = surface.GetValueOrDefault(operation) + 1;
                if (verbose)
                    output.WriteLine("  " + line);
                else
                {
                    samples.Enqueue(line);
                    while (samples.Count > 4)
                        samples.Dequeue();
                }
            }
            output.WriteLine($"events={events}");
            output.WriteLine($"types: {string.Join(", ", types.Select(pair => $"{pair.Key}={pair.Value}"))}");
            output.WriteLine($"surface: {string.Join(", ", surface.Select(pair => $"{pair.Key}={pair.Value}"))}");
            output.WriteLine($"digest={Convert.ToHexString(hasher.GetHashAndReset())}");
            if (!verbose)
                foreach (var sample in samples)
                    output.WriteLine("  ~" + Truncate(sample, 160));
            foreach (var warning in warnings)
                output.WriteLine("!! warn: " + Truncate(warning, 200));
        }
        catch (Exception error) when (error is IOException or FormatException or InvalidOperationException)
        {
            output.WriteLine($"!! unreadable: {error.GetType().Name}: {Truncate(error.Message, 200)}");
        }
    }

    /** 一个事件的规范文本: 只含稳定内容(序号/类型/时间/surface 区间/来源序号/负载记录文本), 不含哈希码。 */
    private static string CanonicalLine(SessionEvent sessionEvent)
    {
        var data = sessionEvent.Data;
        var text = $"{data.GetType().Name}|{data}".Replace("\r", "\\r").Replace("\n", "\\n").Replace("|", "/");
        var sources = sessionEvent.SourceEventSeqs is { Count: > 0 } list ? string.Join(",", list) : "-";
        return string.Create(CultureInfo.InvariantCulture,
            $"{sessionEvent.Seq}|{sessionEvent.Type}|{sessionEvent.Time}|{SurfaceOf(sessionEvent)}|{sources}|{text}");
    }

    private static string SurfaceOf(SessionEvent sessionEvent) => sessionEvent.SurfaceOp switch
    {
        SurfaceOp.Append => "append",
        SurfaceOp.Replace replace => $"replace[{replace.Start},{replace.End})",
        null => "-",
        var other => other.GetType().Name,
    };

    private static string Truncate(string text, int limit)
        => text.Length <= limit ? text : text[..limit] + "…";
}