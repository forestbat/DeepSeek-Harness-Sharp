using Dsh.Core;
using Dsh.Llm;
using Dsh.Persistence;

namespace Dsh.SessionDump;

/**
 * 按宿主恢复大会话的方式(常驻尾窗 + 中断轮收尾 + 窗口化会话)打开一份日志, 然后检查
 * `Session.DeriveMessages()` 里工具调用与工具结果的配对是否成立。
 *
 * 这就是"旧会话能不能再发消息"的复现器: 现场那条 `INVALID_REQUEST`
 * (An assistant message with 'tool_calls' must be followed by tool messages) 在这里会直接暴露成
 * "assistant 的 call X 后面没有 tool 消息"。
 *
 * 用法: Dsh.SessionDump <sessionsRoot> --resume-probe <会话id> [尾窗事件数=20000]
 * 只读: 打开用 SessionAccess.Read, 不写会话文件。
 */
internal static class ResumeProbe
{
    private const int DefaultTailEvents = 20_000;

    /**
     * 整份恢复(不窗口化)的对照: 消息条数与耗时。
     * 用来量"窗口化到底少给了多少上下文", 以及"把前缀折一遍"要付多少时间。
     */
    public static int Full(string root, string sessionId)
    {
        using var persistence = new JsonlSessionPersistence(root, warn: message => Console.WriteLine($"  warn: {message}"));
        var id = SessionId.Create(sessionId);
        var clock = System.Diagnostics.Stopwatch.StartNew();
        using var handle = persistence.Open(id, SessionAccess.Read);
        var events = handle.Read();
        var readAt = clock.ElapsedMilliseconds;
        var session = Session.FromRestore(id, events, handle.Header, handle.InheritedEventCount);
        var restoredAt = clock.ElapsedMilliseconds;
        var messages = session.DeriveMessages();
        var derivedAt = clock.ElapsedMilliseconds;
        var chars = messages.Sum(message => message.Content.Sum(block => block switch
        {
            TextBlock text => text.Text.Length,
            ToolResultBlock result => result.Content.Sum(inner => inner is TextBlock innerText ? innerText.Text.Length : 0),
            _ => 0,
        }));
        Console.WriteLine($"{sessionId}: events {events.Count}, messages {messages.Count}, text chars {chars:N0}");
        Console.WriteLine($"全量: read {readAt}ms, FromRestore {restoredAt - readAt}ms, DeriveMessages {derivedAt - restoredAt}ms, 合计 {derivedAt}ms");
        foreach (var message in messages.Take(3))
            Console.WriteLine($"  head: {message.Role} {Head(message)}");
        foreach (var message in messages.Skip(Math.Max(0, messages.Count - 3)))
            Console.WriteLine($"  tail: {message.Role} {Head(message)}");
        return 0;
    }

    private static string Head(Message message)
    {
        var text = string.Concat(message.Content.OfType<TextBlock>().Select(block => block.Text));
        var tools = message.Content.OfType<ToolCallBlock>().Count();
        var results = message.Content.OfType<ToolResultBlock>().Count();
        return $"[{text.Length} chars, tools {tools}, results {results}] {text.Replace('\n', ' ')[..Math.Min(80, text.Length)]}";
    }

    /**
     * 逐条读完整份日志, 检查序号是否连续(以第一条的序号为基准)。
     * 用产品自己的读取路径, 所以 chunk 行展开、缺页、损伤修复都按线上行为计算。
     */
    public static int Gaps(string root, string sessionId)
    {
        using var persistence = new JsonlSessionPersistence(root, warn: message => Console.WriteLine($"  warn: {message}"));
        var id = SessionId.Create(sessionId);
        using var handle = persistence.Open(id, SessionAccess.Read);
        var events = handle.Read();
        if (events.Count == 0)
        {
            Console.WriteLine($"{sessionId}: 空日志");
            return 0;
        }
        var expected = events[0].Seq;
        var holes = new List<string>();
        foreach (var sessionEvent in events)
        {
            if (sessionEvent.Seq != expected)
                holes.Add($"期望 {expected}，实际 {sessionEvent.Seq}（差 {sessionEvent.Seq - expected}）");
            expected = sessionEvent.Seq + 1;
        }
        Console.WriteLine($"{sessionId}: events={events.Count} 首={events[0].Seq} 末={events[^1].Seq} 不连续={holes.Count}");
        foreach (var hole in holes.Take(10))
            Console.WriteLine($"   {hole}");
        return holes.Count == 0 ? 0 : 1;
    }

    /**
     * 证明会话重新可写: 打开写句柄 → Flush → 关闭, 不改任何内容。
     *
     * 写票只按批持有, 因此这里不需要任何"接管"动作: 过期/空闲的票会自己失效, 下一个写者从日志条数接着发号。
     */
    public static int TakeOver(string root, string sessionId)
    {
        using var persistence = new JsonlSessionPersistence(root, warn: message => Console.WriteLine($"  warn: {message}"));
        var id = SessionId.Create(sessionId);
        var before = persistence.Stat(id);
        if (before is null)
        {
            Console.Error.WriteLine($"没有这份会话: {root}/{sessionId}");
            return 2;
        }
        using (var writer = persistence.Open(id, SessionAccess.Write))
        {
            writer.Flush();
            Console.WriteLine($"写句柄可用: 条数={writer.Count}");
        }
        var after = persistence.Stat(id);
        Console.WriteLine($"完成: 事件 {before.EventCount} → {after?.EventCount}, 文件长度 {after?.SizeBytes}");
        return 0;
    }

    public static int Run(string root, string sessionId, int tailEvents)
    {
        using var persistence = new JsonlSessionPersistence(root, warn: message => Console.WriteLine($"  warn: {message}"));
        var id = SessionId.Create(sessionId);
        var snapshot = persistence.Stat(id);
        if (snapshot is null)
        {
            Console.Error.WriteLine($"没有这份会话: {root}/{sessionId}");
            return 2;
        }
        using var handle = persistence.Open(id, SessionAccess.Read);
        if (persistence.TryOpenWindow(id, tailEvents) is not { } paged)
        {
            Console.WriteLine($"{sessionId}: 窗口化被拒绝(整份恢复), 不适用本探针");
            return 1;
        }
        var closers = SessionRepair.InterruptedTurnClosers(paged.Resident);
        var resident = closers.Count > 0 ? [.. paged.Resident, .. closers] : paged.Resident;
        var adopted = persistence.TryLoadSurfaceFold(id);
        var session = Session.FromWindow(
            id, paged with { Resident = resident, TotalEvents = paged.TotalEvents + closers.Count },
            handle.Header, handle.InheritedEventCount, adopted);
        var lastResident = paged.Resident.Count > 0 ? paged.Resident[^1] : null;
        Console.WriteLine($"window from {paged.ResidentFrom}, total {paged.TotalEvents}, last {lastResident?.Seq}/{lastResident?.Type}");
        Console.WriteLine($"closers: {closers.Count} [{string.Join(", ", closers.Select(e => $"{e.Seq}:{e.Type}"))}]");
        var messages = session.DeriveMessages();
        // 折前缀的页读效率: reads 与 distinct 相等说明同一页没有被反复解析(方案 §5.1 的 1.47x)。
        var ratio = session.PageFaultDistinct == 0 ? 0 : (double)session.PageFaults / session.PageFaultDistinct;
        Console.WriteLine($"page reads: {session.PageFaults}, distinct: {session.PageFaultDistinct}, ratio: {ratio:F2}");
        var problems = Pairing(messages);
        Console.WriteLine($"messages: {messages.Count}, pairing problems: {problems.Count}");
        foreach (var problem in problems.Take(10))
            Console.WriteLine($"  !! {problem}");
        return problems.Count == 0 ? 0 : 1;
    }

    /** 逐个 assistant 的 tool_calls 检查后面是否跟齐结果; 孤立结果也算问题。 */
    private static List<string> Pairing(IReadOnlyList<Message> messages)
    {
        var problems = new List<string>();
        var pending = new List<ToolCallId>();
        foreach (var message in messages)
        {
            var results = message.Content.OfType<ToolResultBlock>().ToList();
            foreach (var result in results)
            {
                if (!pending.Remove(result.ToolCallId))
                    problems.Add($"孤立工具结果 {result.ToolCallId} (前面没有对应的 tool_calls)");
            }
            if (results.Count > 0)
                continue;
            foreach (var callId in pending)
                problems.Add($"assistant 调用了 {callId}, 但后面没有 tool 消息");
            pending.Clear();
            foreach (var call in message.Content.OfType<ToolCallBlock>())
                pending.Add(call.Id);
        }
        foreach (var callId in pending)
            problems.Add($"assistant 调用了 {callId}, 但消息序列到此结束");
        return problems;
    }
}
