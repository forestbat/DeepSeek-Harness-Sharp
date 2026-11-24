using Dsh.Core;
using Dsh.Llm;
using Dsh.Persistence;
using Dsh.Runtime;
using Dsh.Runtime.Logging;
using PersistencePlugin = Dsh.Persistence.Plugin;

namespace Dsh.Tests;

/**
 * 恢复路径的落盘约定(M1/W5): 中断收尾这类合成事件也必须在恢复时落盘,
 * 否则每恢复一次内存就比磁盘多几条, 攒到某一批就和磁盘序号对不上 —— 2026-11-22 那条会话就是这么停写的。
 */
public sealed class SessionResumeDurabilityTests : IDisposable
{
    private const string TestCwd = "/tmp/dsh-resume-durability";
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"dsh-resume-{Guid.NewGuid():N}");

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
    public void ResumingAnInterruptedTurn_PersistsTheSyntheticClosers_EveryTime()
    {
        using var persistence = new JsonlSessionPersistence(_root, compression: JsonlCompression.None);
        var header = NewHeader();
        using (var seed = persistence.Create(header))
        {
            var session = Session.Create(header.Id, header: header);
            session.AttachSequencer(seed.Sequencer);
            // 一个没闭合的回合: turn/start + step/start, 没有 turn/end。
            seed.Append([session.Append(new TurnStartPayload(1)), session.Append(new StepStartPayload(1, 1))]);
            seed.Flush();
        }

        var stored = persistence.StoredEventCount(header.Id);
        Assert.True(stored == 2,
            $"种子日志应落 2 条, 实际 {stored}; 目录: {string.Join(", ", Directory.Exists(_root) ? Directory.GetFiles(_root, "*", SearchOption.AllDirectories) : ["<root 不存在>"])}");
        for (var attempt = 0; attempt < 3; attempt += 1)
        {
            using var logging = LoggingSetup.Create(new LoggingOptions { Level = "Debug" });
            var context = new Context(logging);
            using (var wiring = PersistencePlugin.WirePersistence(context, persistence))
            {
                using var handle = persistence.Open(header.Id, SessionAccess.Read);
                var history = handle.Read();
                var closers = SessionRepair.InterruptedTurnClosers(history, handle.InheritedEventCount);
                // 只有第一次恢复需要补收尾: 补上的事件已经落盘, 之后每次恢复都该是"无需修补"。
                Assert.True(attempt == 0 ? closers.Count > 0 : closers.Count == 0,
                    $"第 {attempt + 1} 次恢复补了 {closers.Count} 条合成收尾(期望: 第一次需要, 之后为 0)");
                var resumed = Session.FromRestore(header.Id, [.. history, .. closers], handle.Header, handle.InheritedEventCount);

                // 接线接管磁盘日志: 内存里多出来的尾部(合成收尾)在**没有后续事件**的情况下也要落盘。
                context.Events.Parallel(new SessionCreatedNotification(resumed)).GetAwaiter().GetResult();
                context.Events.Parallel(new SessionFlushNotification(resumed)).GetAwaiter().GetResult();

                stored = persistence.StoredEventCount(header.Id);
                Assert.True(resumed.Seq == stored,
                    $"第 {attempt + 1} 次恢复后: 内存 {resumed.Seq} 条, 磁盘 {stored} 条, 历史 {history.Count} 条, 合成收尾 {closers.Count} 条");

                // 会话生命周期结束: 让接线收掉写句柄, 下一轮才能重新接管同一份日志。
                context.Events.Parallel(new SessionDisposedNotification(resumed)).GetAwaiter().GetResult();
            }
        }
    }

    private static SessionHeader NewHeader()
    {
        var id = SessionId.Create(Guid.NewGuid().ToString("N"));
        return new SessionHeader
        {
            Version = SessionHeader.SessionFormatVersion,
            Id = id,
            CreatedAt = 1700000000000,
            Cwd = TestCwd,
            IsSeeded = false
        };
    }
}
