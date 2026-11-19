using System.Diagnostics;
using System.Text.Json;
using Dsh.Llm;

namespace Dsh.Persistence;

/** 租约持有者: 谁在写这份会话日志。 */
public sealed record SessionLeaseHolder(
    int Pid,
    string Machine,
    long ProcessStartUtc,
    long Epoch,
    string WriterId,
    long ExpiresUtc);

/**
 * 会话写租约: 写权由会话目录里的 lease.json 记录, 判据是"租约还在被续", 不是"进程是否退出"。
 *
 * - 一个开几天的会话由持有者持续续租, 别人抢不走, 其他人应作为它的客户端发言。
 * - 租约过期本身**不触发**接管: 线程池饥饿、GC 长暂停、机器休眠、sshd 掉线都会让健康进程掉租约, 拿它当"卡死"判据会误抢。
 * - 接管是显式动作: 同机进程还活着时只能 take-over(强制, epoch+1); 只有租约已过期且确认原持有者
 *   不在了(同机 pid 已消失, 或跨机器无从确认)才允许自动接管, 避免僵尸进程把会话永久锁死。
 * - 写前校验(fencing): 每次落盘前核对租约还是不是自己的, 被接管后立刻停写, 不与新持有者交错追加。
 */
public sealed class SessionLease : IDisposable
{
    /** 有效期取心跳间隔的三倍, 给线程池抖动和 GC 暂停留余量。 */
    private static readonly TimeSpan Ttl = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan RenewInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan LockRetryDelay = TimeSpan.FromMilliseconds(10);
    private const int LockAttempts = 100;

    private readonly string _path;
    private readonly Timer _timer;
    private long _renewedAt;
    private volatile bool _lost;
    private bool _disposed;

    /** 本进程启动时刻(UTC ticks): 识别 pid 复用, 也让本进程的多个持久化实例认出彼此。 */
    internal static long CurrentProcessStart { get; } = ReadProcessStart(Environment.ProcessId);

    private readonly string _writerId;

    private SessionLease(string path, SessionLeaseHolder holder)
    {
        _path = path;
        Holder = holder;
        _writerId = holder.WriterId;
        _renewedAt = Environment.TickCount64;
        // 心跳只证明"这个进程还能执行代码", 与 LLM 推理时长、工具调用间隔、用户空闲都无关。
        _timer = new Timer(_ => RenewQuietly(), null, RenewInterval, RenewInterval);
    }

    public SessionLeaseHolder Holder { get; }

    /** 会话目录里的租约文件路径。 */
    public static string PathFor(string root, string? cwd, SessionId id)
        => Path.Join(JsonlLayout.SessionDir(root, cwd, id), "lease.json");

    /** 读当前租约, 不做任何夺取; 供诊断与客户端判断"该会话由谁在写"。 */
    public static SessionLeaseHolder? Inspect(string path)
    {
        using var leaseLock = TryLock(path);
        return leaseLock is null ? null : Read(path);
    }

    /**
     * 取得写权。已有未过期持有者时抛 SessionLeaseHeldException;
     * 过期且确认原持有者不在时自动接管(epoch+1); 同机仍活着的持有者必须走 ForceTakeOver。
     */
    public static SessionLease Acquire(string path)
    {
        using var leaseLock = TryLock(path)
            ?? throw new SessionLeaseHeldException(null, "another process is changing this session's lease right now");
        var existing = Read(path);
        var mine = Build(existing?.Epoch ?? 0, Guid.NewGuid().ToString("N"));
        if (existing is { } holder)
        {
            var expired = holder.ExpiresUtc <= Now;
            if (!expired || IsProcessAlive(holder))
                throw new SessionLeaseHeldException(holder, Describe(holder));
            // 过期且原持有者确认不在: 这是唯一允许自动接管的路径。
            mine = mine with { Epoch = holder.Epoch + 1 };
        }
        Write(path, mine);
        return new SessionLease(path, mine);
    }

    /** 强制接管(CLI 的 take-over): 不要求原持有者退出, 直接 epoch+1 并立刻放弃持有权。 */
    public static SessionLeaseHolder ForceTakeOver(string path)
    {
        using var leaseLock = TryLock(path)
            ?? throw new SessionLeaseHeldException(null, "another process is changing this session's lease right now");
        var existing = Read(path);
        var taken = Build((existing?.Epoch ?? 0) + 1, Guid.NewGuid().ToString("N")) with { Pid = 0, Machine = "", ExpiresUtc = 0 };
        Write(path, taken);
        return taken;
    }

    /** 写前校验: 租约已不是自己的(被接管)就抛, 调用方必须停止落盘。 */
    public void Verify()
    {
        ThrowIfLost();
        var current = Inspect(_path);
        if (!IsMine(current))
        {
            _lost = true;
            throw new SessionLeaseLostException(Holder.Pid, current);
        }
    }

    public void Renew()
    {
        ThrowIfLost();
        if (Environment.TickCount64 - _renewedAt < (long)RenewInterval.TotalMilliseconds)
            return;
        using var leaseLock = TryLock(_path);
        if (leaseLock is null)
            return;
        var current = Read(_path);
        if (!IsMine(current))
        {
            _lost = true;
            throw new SessionLeaseLostException(Holder.Pid, current);
        }
        Write(_path, Holder with { ExpiresUtc = Now + (long)Ttl.TotalMilliseconds });
        _renewedAt = Environment.TickCount64;
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _timer.Dispose();
        try
        {
            using var leaseLock = TryLock(_path);
            var current = Read(_path);
            // 只有还是自己的租约才删: 已被接管时删掉会把新持有者的写权一起抹掉。
            if (IsMine(current))
                File.Delete(_path);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
        }
    }

    private bool IsMine(SessionLeaseHolder? holder)
        => holder is not null && holder.WriterId == _writerId && holder.Epoch == Holder.Epoch;

    private void RenewQuietly()
    {
        try
        {
            Renew();
        }
        catch (SessionLeaseLostException)
        {
            // 失锁由下一次 Verify 上报给落盘方; 心跳线程自己不再抛(定时器上抛会终止进程)。
            _lost = true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // 一次写租约失败可能只是瞬时 IO: 不判定失锁, 交给下一次心跳重试。
        }
    }

    private void ThrowIfLost()
    {
        if (_lost)
            throw new SessionLeaseLostException(Holder.Pid, Inspect(_path));
    }

    private static SessionLeaseHolder Build(long epoch, string writerId) => new(
        Pid: Environment.ProcessId,
        Machine: Environment.MachineName,
        ProcessStartUtc: CurrentProcessStart,
        Epoch: epoch,
        WriterId: writerId,
        ExpiresUtc: Now + (long)Ttl.TotalMilliseconds);

    private static long Now => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    private static string Describe(SessionLeaseHolder holder)
        => $"session is being written by pid {holder.Pid} on {holder.Machine} (lease epoch {holder.Epoch}); "
            + "attach there instead, or run `session take-over` to hand the session over";

    private static bool IsProcessAlive(SessionLeaseHolder holder)
    {
        if (holder.Pid <= 0)
            return false;
        if (!string.Equals(holder.Machine, Environment.MachineName, StringComparison.OrdinalIgnoreCase))
            // 跨机器无从确认存活: 过期即视为可接管, 否则远程机器下线会把会话永久锁死。
            return false;
        // pid 可能被复用, 启动时刻对不上就当原持有者已经不在。
        return ReadProcessStart(holder.Pid) == holder.ProcessStartUtc;
    }

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

    private static SessionLeaseHolder? Read(string path)
    {
        if (!File.Exists(path))
            return null;
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            var root = document.RootElement;
            return new SessionLeaseHolder(
                Pid: root.GetProperty("pid").GetInt32(),
                Machine: root.GetProperty("machine").GetString() ?? "",
                ProcessStartUtc: root.GetProperty("processStartUtc").GetInt64(),
                Epoch: root.GetProperty("epoch").GetInt64(),
                WriterId: root.TryGetProperty("writerId", out var writerId) ? writerId.GetString() ?? "" : "",
                ExpiresUtc: root.GetProperty("expiresUtc").GetInt64());
        }
        catch (Exception error) when (error is JsonException or KeyNotFoundException or InvalidOperationException or IOException)
        {
            // 坏掉的租约文件等于没有: 让 Acquire 重建, 否则一次断电就永久锁死会话。
            return null;
        }
    }

    private static void Write(string path, SessionLeaseHolder holder)
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
            writer.WriteEndObject();
        }
        File.Move(temp, path, overwrite: true);
    }

    /** 租约读改写的短互斥: 只保护这一次读写(不保护每次落盘), 进程退出由 OS 释放。 */
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
