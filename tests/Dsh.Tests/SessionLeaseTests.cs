using Dsh.Core;
using Dsh.Llm;
using Dsh.Persistence;

namespace Dsh.Tests;

/**
 * 会话写租约的约定:
 * 一个会话只有一个写者(其余人是它的客户端); 租约过期不等于可以抢, 只有确认原持有者不在了
 * 或显式 take-over 才换人; 被接管后旧写者立刻停写(fencing)。
 */
public sealed class SessionLeaseTests : IDisposable
{
    private const string TestCwd = "/tmp/dsh-lease";
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"dsh-lease-{Guid.NewGuid():N}");

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
        }
    }

    [Fact]
    public void SecondWriter_IsRefused_WhileTheLeaseIsAlive()
    {
        var session = NewSession();
        using var first = new JsonlSessionPersistence(_root, compression: JsonlCompression.None);
        using var writer = first.Create(session.Header);
        writer.Append([session.Append(new TurnStartPayload(1))]);
        writer.Flush();

        using var second = new JsonlSessionPersistence(_root, compression: JsonlCompression.None);
        var refusal = Assert.Throws<SessionLeaseHeldException>(() => second.Open(session.Id, SessionAccess.Write));
        Assert.NotNull(refusal.Holder);
        Assert.Equal(Environment.ProcessId, refusal.Holder!.Pid);
    }

    [Fact]
    public void ReleasedLease_LetsTheNextWriterIn()
    {
        var session = NewSession();
        using var first = new JsonlSessionPersistence(_root, compression: JsonlCompression.None);
        using (var seeded = first.Create(session.Header))
        {
            seeded.Append([session.Append(new TurnStartPayload(1))]);
            seeded.Close();
        }

        using var second = new JsonlSessionPersistence(_root, compression: JsonlCompression.None);
        using var reopened = second.Open(session.Id, SessionAccess.Write);
        Assert.Equal(session.Id, reopened.Id);
    }

    [Fact]
    public void TakeOver_MovesWriteAuthority_AndTheOldWriterStops()
    {
        var session = NewSession();
        using var first = new JsonlSessionPersistence(_root, compression: JsonlCompression.None);
        using var writer = first.Create(session.Header);
        writer.Append([session.Append(new TurnStartPayload(1))]);
        writer.Flush();
        var before = first.InspectLease(session.Id);
        Assert.NotNull(before);

        var taken = first.ForceTakeOverLease(session.Id);
        Assert.True(taken.Epoch > before.Epoch);

        // 旧写者下一次落盘必须失败(fencing), 而不是继续和新持有者交错追加。
        // 追加只是进缓冲, 落盘(fsync)是提交点: 租约校验就在那里发生。
        writer.Append([session.Append(new StepStartPayload(1, 1))]);
        Assert.Throws<SessionLeaseLostException>(() => writer.Flush());
        // 失锁后关闭不再报错: 停写就是它该做的事。
        writer.Close();
    }

    [Fact]
    public void ExpiredLeaseOfDeadProcess_IsTakenOverAutomatically()
    {
        var id = SessionId.Create(Guid.NewGuid().ToString("N"));
        var path = SessionLease.PathFor(_root, TestCwd, id);
        WriteLease(path, pid: 2_000_000, processStartUtc: 1, expired: true, writerId: "dead-writer", epoch: 7);

        // 过期且原持有者确认不在: 允许自动接管并把 epoch 推进一格。
        using var lease = SessionLease.Acquire(path);
        Assert.Equal(8, lease.Holder.Epoch);
    }

    [Fact]
    public void ExpiredLeaseOfLiveProcess_NeedsExplicitTakeOver()
    {
        var id = SessionId.Create(Guid.NewGuid().ToString("N"));
        var path = SessionLease.PathFor(_root, TestCwd, id);
        // 同机仍活着的持有者(本进程)即使租约过期也不能被自动顶掉: 线程池饥饿/GC/休眠都会造成这种假象。
        WriteLease(path, Environment.ProcessId, SessionLease.CurrentProcessStart, expired: true,
            writerId: "slow-writer", epoch: 3);

        Assert.Throws<SessionLeaseHeldException>(() => SessionLease.Acquire(path));

        var taken = SessionLease.ForceTakeOver(path);
        Assert.Equal(4, taken.Epoch);
        Assert.Equal(0, taken.Pid);

        using var lease = SessionLease.Acquire(path);
        Assert.Equal(5, lease.Holder.Epoch);
    }

    [Fact]
    public void UnexpiredLeaseOfDeadProcess_IsStillHonouredUntilItExpires()
    {
        var id = SessionId.Create(Guid.NewGuid().ToString("N"));
        var path = SessionLease.PathFor(_root, TestCwd, id);
        WriteLease(path, pid: 2_000_000, processStartUtc: 1, expired: false, writerId: "dying-writer", epoch: 2);

        Assert.Throws<SessionLeaseHeldException>(() => SessionLease.Acquire(path));
    }

    private static Session NewSession()
    {
        var id = SessionId.Create(Guid.NewGuid().ToString("N"));
        return Session.Create(id,
            header: new SessionHeader
            {
                Version = SessionHeader.SessionFormatVersion,
                Id = id,
                CreatedAt = 1700000000000,
                Cwd = TestCwd,
                IsSeeded = false
            });
    }

    /** 手写租约文件: 钉住磁盘格式, 也用来构造"别人的租约"这种真实运行中难以复现的状态。 */
    private void WriteLease(string path, int pid, long processStartUtc, bool expired, string writerId, long epoch)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var expires = DateTimeOffset.UtcNow.AddSeconds(expired ? -30 : 30).ToUnixTimeMilliseconds();
        File.WriteAllText(path,
            $$"""{"pid":{{pid}},"machine":"{{Environment.MachineName}}","processStartUtc":{{processStartUtc}},"epoch":{{epoch}},"writerId":"{{writerId}}","expiresUtc":{{expires}}}""");
    }
}
