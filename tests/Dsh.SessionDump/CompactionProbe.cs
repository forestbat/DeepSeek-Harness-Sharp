using Dsh.Compaction;
using Dsh.Core;
using Dsh.Llm;
using Dsh.Persistence;
using Dsh.Runtime;

namespace Dsh.SessionDump;

/**
 * 真实会话上的压缩探针: 按宿主恢复大会话的方式**窗口化**打开, 然后走一遍 /compact 的选路
 * (TokenMeter.Measure → CompactionRegion.SelectCompactableRange)。
 *
 * 这是"窗口化会话压缩失败"的复现器: 现场那两条报错
 * (token-meter surface does not match / tool-pairing balance ... no matching tool-call)
 * 都会在这里原样冒出来, 修好之后则逐份会话输出 OK 或"无可压区段"。
 */
internal static class CompactionProbe
{
    public static int Run(string root, int tailEvents)
    {
        var ctx = new Context();
        var meter = new TokenMeter(ctx);
        var failures = 0;
        var index = 0;
        foreach (var project in Directory.EnumerateDirectories(root).OrderBy(path => path, StringComparer.Ordinal))
        {
            foreach (var sessionDir in Directory.EnumerateDirectories(project).OrderBy(path => path, StringComparer.Ordinal))
            {
                var log = Path.Combine(sessionDir, "session.jsonl.zstd");
                if (!File.Exists(log))
                    continue;
                var id = SessionId.Create(Path.GetFileName(sessionDir));
                index += 1;
                Console.Error.WriteLine($"[{index}] {Path.GetFileName(project)}/{Path.GetFileName(sessionDir)}");
                try
                {
                    Console.WriteLine($"{Path.GetFileName(sessionDir)}: {Probe(root, id, meter, tailEvents)}");
                }
                catch (Exception error)
                {
                    failures += 1;
                    Console.WriteLine($"{Path.GetFileName(sessionDir)}: FAIL {error.GetType().Name}: {error.Message}");
                    Diagnose(root, id, tailEvents);
                }
            }
        }
        Console.WriteLine($"# probes: {index}, failures: {failures}");
        return failures == 0 ? 0 : 1;
    }

    private static string Probe(string root, SessionId id, TokenMeter meter, int tailEvents)
    {
        using var persistence = new JsonlSessionPersistence(root);
        var snapshot = persistence.Stat(id) ?? throw new InvalidOperationException("no header");
        var window = persistence.TryOpenWindow(id, tailEvents);
        var cached = persistence.TryLoadSurfaceFold(id);
        var session = window is { } opened
            ? Session.FromWindow(id, opened, snapshot.Header, snapshot.InheritedEventCount, cached)
            : Session.Create(id, persistence.Open(id, SessionAccess.Read).Read(), snapshot.Header, snapshot.InheritedEventCount);
        var clock = System.Diagnostics.Stopwatch.StartNew();
        Console.Error.WriteLine($"  session ready ({session.ResidentFrom} resident from, {session.Seq} events, cached={cached is not null})");
        var measureClock = System.Diagnostics.Stopwatch.StartNew();
        var measurement = meter.Measure(session);
        Console.Error.WriteLine($"  measured ({measurement.Nodes.Count} nodes, {measureClock.ElapsedMilliseconds}ms)");
        var surface = session.SurfaceManager.Nodes;
        Console.Error.WriteLine($"  surface ready ({surface.Count} nodes)");
        // 计量与取表面都要先把整条日志折一遍(前缀按页读) —— 命中派生缓存时这一步是常数级。
        var folded = clock.ElapsedMilliseconds;
        if (session.FoldSnapshot() is { } fold)
            persistence.SaveSurfaceFold(id, fold);
        var rangeClock = System.Diagnostics.Stopwatch.StartNew();
        var range = CompactionRegion.SelectCompactableRange(session, measurement, retainTokens: 1, tailTurns: 1);
        Console.Error.WriteLine($"  selected ({rangeClock.ElapsedMilliseconds}ms)");
        var windowText = window is { } w ? $"window from {w.ResidentFrom}" : "whole log";
        // 上下文 = surface 上的节点, 不是整条日志: 压缩就是把被替换的那一段折成一个摘要节点。
        foreach (var seq in surface.Skip(Math.Max(0, surface.Count - 8)))
            Console.Error.WriteLine($"    tail seq {seq}: {session.EventAt(seq)?.Type}, balancedBefore={ToolPairing.BalancedBefore(session, seq)}, balancedAfter={ToolPairing.BalancedAfter(session, seq)}");
        if (surface.Count > 0)
            Console.Error.WriteLine($"    surface first {surface[0]} last {surface[^1]}, nodes {surface.Count}, surfaceTokens {measurement.SurfaceTokens}");
        Console.Error.WriteLine($"    first3: {string.Join(", ", surface.Take(3).Select(s => $"{s}({session.EventAt(s)?.Type},{measurement.Nodes.First(n => n.Seq == s).Tokens}t)"))}");
        Console.Error.WriteLine($"    last3: {string.Join(", ", surface.TakeLast(3).Select(s => $"{s}({session.EventAt(s)?.Type},{measurement.Nodes.First(n => n.Seq == s).Tokens}t)"))}");
        var biggest = measurement.Nodes.OrderByDescending(n => n.Tokens).Take(6).ToList();
        foreach (var node in biggest)
            Console.Error.WriteLine($"    big node {node.Seq}: {session.EventAt(node.Seq)?.Type}, {node.Tokens} tokens");
        var compactions = CountCompactions(session);
        var retained = measurement.Nodes.Where(node => range is null || node.Seq < range.Start || node.Seq > range.End).Sum(node => node.Tokens);
        var shadowed = measurement.Nodes.Where(node => range is not null && node.Seq >= range.Start && node.Seq <= range.End).Sum(node => node.Tokens);
        Console.Error.WriteLine($"  tokens: total {measurement.TotalTokens}, shadowed {shadowed}, retained {retained}");
        var balance = 0;
        foreach (var seq in surface)
        {
            var e = session.EventAt(seq);
            var delta = e?.Data switch { AssistantMessagePayload a => a.Message.Content.OfType<ToolCallBlock>().Count(), ToolResultPayload => -1, _ => 0 };
            if (seq >= 129017)
            {
                balance += delta;
                if (delta != 0 || balance == 0)
                    Console.Error.WriteLine($"    seq {seq}: {e?.Type} delta {delta} balance {balance}");
            }
            else
                balance += delta;
        }
        var tokensBySeq = measurement.Nodes.ToDictionary(node => node.Seq, node => node.Tokens);
        var userStarts = surface.Where(seq => session.EventAt(seq)?.Data is UserMessagePayload { Message.Source: UserMessageSource }).ToList();
        var lastFew = userStarts.Skip(Math.Max(0, userStarts.Count - 6)).ToList();
        for (var index = 0; index < lastFew.Count; index += 1)
        {
            var from = surface.ToList().IndexOf(lastFew[index]);
            var to = index + 1 < lastFew.Count ? surface.ToList().IndexOf(lastFew[index + 1]) : surface.Count;
            var size = 0;
            for (var slot = from; slot < to; slot += 1)
                size += tokensBySeq.GetValueOrDefault(surface[slot]);
            Console.Error.WriteLine($"    turn@seq {lastFew[index]}: {to - from} nodes, {size} tokens");
        }
        var foldText = cached is null ? $"fold {folded}ms" : $"cached({folded}ms)";
        return range is null
            ? $"no compactable range ({windowText}, surface {surface.Count}/{session.Seq} nodes, {compactions} compactions, {measurement.TotalTokens} tokens, {foldText})"
            : $"range {range.Start}-{range.End} ({windowText}, surface {surface.Count}/{session.Seq} nodes, {compactions} compactions, {measurement.TotalTokens} tokens, {foldText})";
    }

    /** 失败现场: 数一数窗口前缀里有多少事件按页取不回来, 以及表头落在哪。 */
    private static void Diagnose(string root, SessionId id, int tailEvents)
    {
        using var persistence = new JsonlSessionPersistence(root, warn: message => Console.WriteLine($"  warn: {message}"));
        var window = persistence.TryOpenWindow(id, tailEvents);
        if (window is not { } opened)
        {
            Console.WriteLine("  window: declined (whole log)");
            return;
        }
        var session = Session.FromWindow(id, opened, persistence.Stat(id)!.Header, persistence.Stat(id)!.InheritedEventCount);
        var missing = new List<long>();
        for (var seq = 0L; seq < session.Seq && missing.Count < 5; seq += 1)
        {
            if (session.EventAt(seq) is null)
                missing.Add(seq);
        }
        Console.WriteLine($"  window from {session.ResidentFrom}, total {session.Seq}, missing pages at [{string.Join(",", missing)}]");
    }

    private static int CountCompactions(Session session)
    {
        // 一次压缩在表面上的痕迹是一个 replace 节点(摘要/剪枝替换掉被折叠的那一段); 旧的摘要节点会被后一次替换掉。
        var count = 0;
        foreach (var seq in session.SurfaceManager.Nodes)
        {
            if (session.EventAt(seq)?.SurfaceOp is SurfaceOp.Replace)
                count += 1;
        }
        return count;
    }
}