using System.Diagnostics;
using System.Text.Json;
using Dsh.Llm;

namespace Dsh.Persistence;

/** 票面: 谁在发号、从哪个号开始、什么时候到期。 */
public sealed record SessionTicketHolder(
    int Pid,
    string Machine,
    long ProcessStartUtc,
    long Epoch,
    string WriterId,
    long ExpiresUtc,
    long NextSeq);

/**
 * 会话写票(write ticket): 想给事件发号, 先在日志旁边领一张票。
 *
 * 票只登记**谁在发号、下一个号是几、什么时候到期**, 不预分配号段 —— 因此崩溃不会凭空留下空号:
 * 已经写下去的事件是唯一起点, 下一张票的起点就是日志真实条数。
 *
 * 与旧写租约的区别是结构性的: 票没有心跳线程, 只在"发号"与"落盘完成"这两个时刻被写一次。
 * 于是"进程活着却再也不写"不可能把会话锁死 —— 到期它就失效, 下一个写者从日志条数接着发号。
 *
 * 领票(读改写)与追加都在同一把尾锁下完成: 谁先谁后由文件系统排序, 不存在两张票同时有效。
 */
public static class SessionWriteTicket
{
    /** 票的有效期取"一批事件从发号到落盘"的合理上界, 每次发号与每次落盘都会顺手续期。 */
    private static readonly TimeSpan Ttl = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan LockRetryDelay = TimeSpan.FromMilliseconds(5);
    private const int LockAttempts = 200;

    /** 本进程启动时刻(UTC ticks): 识别 pid 复用。 */
    internal static long CurrentProcessStart { get; } = ReadProcessStart(Environment.ProcessId);

    /** 会话目录里的票文件路径。 */
    public static string PathFor(string root, string? cwd, SessionId id)
        => Path.Join(JsonlLayout.SessionDir(root, cwd, id), "ticket.json");

    /** 读当前票, 不做任何夺取; 供诊断与客户端判断"该会话此刻谁在发号"。 */
    public static SessionTicketHolder? Inspect(string path)
    {
        using var ticketLock = TryLock(path);
        return ticketLock is null ? null : Read(path);
    }

    /**
     * 领票并返回这批事件的起点序号。
     *
     * 起点一律以**日志真实条数**为准(由调用方在尾锁内取), 只有"还是我自己的票"时才用票面记的
     * NextSeq —— 那是本进程刚发出去、还没落盘的那一段, 号必须接着它走, 不能退回磁盘条数。
     *
     * 别人持有未过期的票时抛 `SessionWriteBusyException`: 那是"这一次先别写", 不是终局。
     */
    public static long NextSeq(string path, long storedCount, int count)
    {
        if (count <= 0)
            throw new ArgumentOutOfRangeException(nameof(count), "a write ticket covers at least one event");
        using var ticketLock = TryLock(path)
            ?? throw new SessionWriteBusyException(Environment.ProcessId,
                "another writer is taking this session's write ticket right now; retrying on the next write");
        DeleteLegacyLease(path);
        var now = Now;
        var current = Read(path);
        if (current is { } other && !IsMine(other) && other.ExpiresUtc > now)
            throw new SessionWriteBusyException(other.Pid,
                $"session is being numbered by pid {other.Pid} on {other.Machine} (ticket epoch {other.Epoch}); "
                + "retrying on the next write");
        // 票不在我手里时, 起点只能是日志真实条数: 别人的 NextSeq 里可能含着"发了号却没写成"的那一段。
        var start = current is { } mine && IsMine(mine) && mine.ExpiresUtc > now
            ? Math.Max(storedCount, mine.NextSeq)
            : storedCount;
        Write(path, Build(current?.Epoch ?? 0, start + count, now));
        return start;
    }

    /**
     * 这批事件已经落盘到 durableUpTo(不含)。
     *
     * 没有未落盘的事件了就**把票删掉**: 空闲进程不该占着发号权, 别人立刻就能领。
     */
    public static void MarkDurable(string path, long durableUpTo)
    {
        using var ticketLock = TryLock(path);
        if (ticketLock is null)
            return;
        var current = Read(path);
        if (current is null || !IsMine(current))
            return;
        if (durableUpTo >= current.NextSeq)
        {
            TryDelete(path);
            return;
        }
        Write(path, current with { ExpiresUtc = Now + (long)Ttl.TotalMilliseconds });
    }

    /** 立刻放弃自己手里的票(关闭写句柄时用): 未落盘的事件留给下一次领票按日志条数重发。 */
    public static void Release(string path)
    {
        using var ticketLock = TryLock(path);
        if (ticketLock is null)
            return;
        var current = Read(path);
        if (current is not null && IsMine(current))
            TryDelete(path);
    }

    private static SessionTicketHolder Build(long epoch, long nextSeq, long now) => new(
        Pid: Environment.ProcessId,
        Machine: Environment.MachineName,
        ProcessStartUtc: CurrentProcessStart,
        Epoch: epoch == 0 ? 1 : epoch,
        WriterId: Writer,
        ExpiresUtc: now + (long)Ttl.TotalMilliseconds,
        NextSeq: nextSeq);

    /** 本进程的票主标识: 同一个进程内的多个持久化实例共用它。 */
    private static readonly string Writer = Guid.NewGuid().ToString("N");

    /** 票是不是本进程这一张: 不看 pid(同进程多实例共用), 只看票主与进程启动时刻。 */
    private static bool IsMine(SessionTicketHolder holder)
        => holder.WriterId == Writer && holder.ProcessStartUtc == CurrentProcessStart;

    private static long Now => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    private static long ReadProcessStart(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return process.StartTime.ToUniversalTime().Ticks;
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or NotSupportedException)
        {
            return 0;
        }
    }

    private static SessionTicketHolder? Read(string path)
    {
        if (!File.Exists(path))
            return null;
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            var root = document.RootElement;
            return new SessionTicketHolder(
                Pid: root.GetProperty("pid").GetInt32(),
                Machine: root.GetProperty("machine").GetString() ?? "",
                ProcessStartUtc: root.GetProperty("processStartUtc").GetInt64(),
                Epoch: root.GetProperty("epoch").GetInt64(),
                WriterId: root.TryGetProperty("writerId", out var writerId) ? writerId.GetString() ?? "" : "",
                ExpiresUtc: root.GetProperty("expiresUtc").GetInt64(),
                NextSeq: root.TryGetProperty("nextSeq", out var nextSeq) ? nextSeq.GetInt64() : 0);
        }
        catch (Exception error) when (error is JsonException or KeyNotFoundException or InvalidOperationException or IOException)
        {
            // 坏掉的票等于没有: 让下一次发号重建, 否则一次断电就永久锁死会话。
            return null;
        }
    }

    private static void Write(string path, SessionTicketHolder holder)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = $"{path}.{Environment.ProcessId}.{Guid.NewGuid():N}.tmp";
        using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteNumber("pid", holder.Pid);
            writer.WriteString("machine", holder.Machine);
            writer.WriteNumber("processStartUtc", holder.ProcessStartUtc);
            writer.WriteString("writerId", holder.WriterId);
            writer.WriteNumber("epoch", holder.Epoch);
            writer.WriteNumber("expiresUtc", holder.ExpiresUtc);
            writer.WriteNumber("nextSeq", holder.NextSeq);
            writer.WriteEndObject();
        }
        if (File.Exists(path))
            File.Replace(temp, path, destinationBackupFileName: null);
        else
            File.Move(temp, path);
    }

    /** 旧版本的写租约文件: 票机制下它没有任何读者, 见到就删, 免得留下"谁在写"的假线索。 */
    private static void DeleteLegacyLease(string ticketPath)
    {
        var legacy = Path.Join(Path.GetDirectoryName(ticketPath), "lease.json");
        try
        {
            if (File.Exists(legacy))
                File.Delete(legacy);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
        }
    }

    /** 票读改写的短互斥: 只保护这一次读写, 进程退出由 OS 释放。 */
    private static FileStream? TryLock(string path)
    {
        // 会话目录可能还没建(第一条事件之前), 先建目录再抢锁, 否则每次都只会拿到 DirectoryNotFound。
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        for (var attempt = 0; attempt < LockAttempts; attempt += 1)
        {
            try
            {
                return new FileStream(path + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException)
            {
                Thread.Sleep(LockRetryDelay);
            }
        }
        return null;
    }
}
