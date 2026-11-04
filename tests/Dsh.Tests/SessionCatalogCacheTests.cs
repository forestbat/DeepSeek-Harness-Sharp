using Dsh.Core;
using Dsh.Gui.Services;
using Dsh.Llm;

namespace Dsh.Tests;

/**
 * 侧栏目录(SessionCatalog)的磁盘扫描缓存。
 *
 * 背景: 每次侧栏刷新都全量走一遍 JsonlSessionPersistence.List() —— 逐个会话读日志的 zstd 首行。
 * dotnet-trace 实测该路径占 UI 线程 23% 的时间(180 秒里 212 次刷新、每次约 95ms), 是"点界面没反应"的主因。
 * 磁盘扫描只用于补齐 Fleet 里没有的历史会话, 因此可以缓存; 这里钉住缓存与失效这两个契约。
 */
public class SessionCatalogCacheTests
{
    /**
     * 缓存命中后不再观察磁盘变化; 显式失效后才重新扫描。
     * 用"直接从持久化删除会话"制造磁盘变化, 绕过 catalog 自身的失效入口。
     */
    [Fact]
    public async Task Load_CachesDiskScanUntilInvalidated()
    {
        await using var env = await GuiTestEnvironment.CreateAsync();
        // 空会话不落盘: 先写入一条消息, 让这个会话真正出现在磁盘上。
        env.Agent.Session.Append(new UserMessagePayload(MessageFactory.CreateUserText("缓存用例")), new SurfaceOp.Append());
        env.App.Ctx.Get<SessionStore>(SessionStore.ServiceName)!.Flush(env.Agent.Session).GetAwaiter().GetResult();
        var persistence = env.App.Ctx.Get<ISessionPersistence>(ISessionPersistence.ServiceName);
        Assert.NotNull(persistence);
        await using var fleet = FleetWorkspace.ForContext(env.App.Ctx, env.App.Home, null);
        var catalog = new SessionCatalog(env.App.Ctx, fleet);

        var first = catalog.Load();
        var victim = Assert.Single(first);
        Assert.Equal(env.Agent.Id, victim.SessionId);

        // 磁盘上真的没有了, 但缓存还在 —— 这就是"每轮刷新不再扫盘"的代价与收益。
        persistence.Delete(victim.SessionId);
        Assert.Empty(persistence.List());
        Assert.Single(catalog.Load());

        catalog.InvalidateDiskCache();
        Assert.Empty(catalog.Load());
    }

    /** 一条事件都没写过的会话只是内存里的占位, 不能出现在侧栏历史里(否则每开一次界面就多一条垃圾会话)。 */
    [Fact]
    public async Task Load_SkipsSessionsThatNeverWroteAnEvent()
    {
        await using var env = await GuiTestEnvironment.CreateAsync();
        var persistence = env.App.Ctx.Get<ISessionPersistence>(ISessionPersistence.ServiceName);
        Assert.NotNull(persistence);
        var empty = persistence.Create(new SessionHeader
        {
            Version = SessionHeader.SessionFormatVersion,
            Id = SessionId.Create(Guid.NewGuid().ToString("N")),
            CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            Cwd = env.Agent.Session.Header.Cwd,
            IsSeeded = false,
        });
        await using var fleet = FleetWorkspace.ForContext(env.App.Ctx, env.App.Home, null);
        var catalog = new SessionCatalog(env.App.Ctx, fleet);

        Assert.Contains(persistence.List(), snapshot => snapshot.Header.Id == empty.Id);
        Assert.DoesNotContain(catalog.Load(), node => node.SessionId == empty.Id);
        empty.Close();
    }

    /** 反复读取只做一次扫描: 同一批实例应被复用, 而不是每次都新建。 */
    [Fact]
    public async Task Load_ReusesTheSameNodesBetweenCalls()
    {
        await using var env = await GuiTestEnvironment.CreateAsync();
        await using var fleet = FleetWorkspace.ForContext(env.App.Ctx, env.App.Home, null);
        var catalog = new SessionCatalog(env.App.Ctx, fleet);

        var first = catalog.Load();
        var second = catalog.Load();

        Assert.Equal(first.Count, second.Count);
        // 缓存的是同一批节点实例(磁盘来源的条目); Fleet 来源每次新建, 故只比磁盘那部分。
        var cachedIds = first.Where(node => !node.IsRunning).Select(node => node.SessionId).ToList();
        Assert.Equal(cachedIds, second.Where(node => !node.IsRunning).Select(node => node.SessionId).ToList());
    }
}
