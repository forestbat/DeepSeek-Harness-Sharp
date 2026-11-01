using Dsh.Boot;
using Dsh.Core;
using Dsh.Llm;
using Dsh.Runtime;

namespace Dsh.Tests;

/**
 * 会话落盘的句柄登记表。
 *
 * 背景: 登记表是插件内的普通字典, 而创建/接管跑在创建者线程、事件落盘跑在各回合线程, 因此它天然被多线程读写。
 * 无锁字典在这种用法下会丢项、错读甚至损坏内部状态; 检查与插入分离还会让两个线程各开一个写句柄,
 * 第二个在 ClaimWrite 处抛 SessionAlreadyOwnedException(真实日志里出现过)。
 * 这里走公开插件入口压契约, 不复写实现。
 */
public class PersistenceWiringTests
{
    private const string PersistencePackage = "@deepseek-ai/dsh-persistence";
    private const int Sessions = 12;
    private const int NotifiesPerSession = 6;

    /**
     * 多线程同时创建不同会话、并在创建后继续落盘: 不得抛异常, 每条日志只能有一个写句柄。
     */
    [Fact]
    public void ConcurrentSessionCreationAndAppends_KeepOneWriterPerLog()
    {
        var root = Path.Combine(Path.GetTempPath(), $"dsh-persist-{Guid.NewGuid():N}");
        var home = new HarnessHome(Path.Combine(root, "home"));
        home.Ensure();
        try
        {
            var ctx = new Context();
            ctx.SetOwn("dshHomePath", root);
            var store = new SessionStore(ctx);
            _ = new Dsh.Interaction.CommandsService(ctx);
            using var plugin = new Dsh.Persistence.Plugin(PersistencePackage).Apply(ctx, null);
            var persistence = ctx.Get<ISessionPersistence>(ISessionPersistence.ServiceName)!;
            var failures = new System.Collections.Concurrent.ConcurrentBag<Exception>();
            var sessions = Enumerable.Range(0, Sessions).Select(_ => store.Prepare()).ToList();

            var threads = sessions.Select(session => new Thread(() =>
            {
                try
                {
                    store.Enter(session);
                    for (var round = 0; round < NotifiesPerSession; round++)
                        ctx.Emit(new SessionCreatedNotification(session));
                    session.Append(new UserMessagePayload(MessageFactory.CreateUserText("hello")), new SurfaceOp.Append());
                    ctx.Emit(new SessionFlushNotification(session));
                }
                catch (Exception error)
                {
                    failures.Add(error);
                }
            })).ToList();
            threads.ForEach(thread => thread.Start());
            threads.ForEach(thread => Assert.True(thread.Join(TimeSpan.FromSeconds(30)), "session thread did not finish"));

            Assert.Empty(failures);
            // 每个会话的写句柄唯一: 插件已持有, 再开一个必须被拒。
            foreach (var session in sessions)
            {
                Assert.Throws<Dsh.Persistence.SessionAlreadyOwnedException>(
                    () => persistence.Open(session.Id, SessionAccess.Write));
            }
        }
        finally
        {
            TryDelete(root);
        }
    }

    private static void TryDelete(string root)
    {
        try
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
        catch (IOException)
        {
            // 临时目录清不掉不影响断言结果。
        }
    }
}
