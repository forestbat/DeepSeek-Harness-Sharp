using Dsh.Core;
using Dsh.Llm;
using Dsh.Runtime;

namespace Dsh.Tests;

/**
 * SessionStore 的并发安全。
 *
 * 背景: 一条消息里并发 spawn 多个子代理会同时 Create 会话, 旧实现的无锁 Dictionary 在扩容窗口里
 * 让 Announce 读不到刚写入的键, 抛出 `session "..." is not entered in the store`, 有时还伴随字典内部
 * 状态损坏。这里只压这个契约, 不复制 store 的实现逻辑。
 */
public class SessionStoreConcurrencyTests
{
    private const int ParallelCreates = 64;
    private const int Rounds = 20;

    /**
     * 并发 Create 必须全部成功, 且每个会话都能立刻查回。
     * 旧实现在这里会间歇性抛 `is not entered in the store` / 丢失条目。
     */
    [Fact]
    public void ConcurrentCreate_KeepsEverySessionQueryable()
    {
        var ctx = new Context();
        var store = new SessionStore(ctx);

        for (var round = 0; round < Rounds; round++)
        {
            var created = new Session[ParallelCreates];
            Parallel.For(0, ParallelCreates, index => created[index] = store.Create());

            foreach (var session in created)
                Assert.Same(session, store.Get(session.Id));
            Assert.Equal(ParallelCreates, store.List().Count);

            foreach (var session in created)
                store.Release(session.Id);
            Assert.Empty(store.List());
        }
    }

    /** "登记并公告"是原子的: 并发登记同一会话只有一个成功, 其余拿 null 而不是抛异常。 */
    [Fact]
    public void ConcurrentTryEnter_RegistersExactlyOnce()
    {
        var ctx = new Context();
        var store = new SessionStore(ctx);
        var session = store.Prepare();

        var results = new IDisposable?[ParallelCreates];
        Parallel.For(0, ParallelCreates, index => results[index] = store.TryEnter(session));

        Assert.Single(results, result => result is not null);
        Assert.Same(session, store.Get(session.Id));
    }

    /** 已登记的会话再走 Enter 属于 bug, 必须炸出来而不是静默覆盖。 */
    [Fact]
    public void EnterTwice_Throws()
    {
        var ctx = new Context();
        var store = new SessionStore(ctx);
        var session = store.Create();

        var error = Assert.Throws<InvalidOperationException>(() => store.Enter(session));

        Assert.Contains("already live in the store", error.Message, StringComparison.Ordinal);
    }

    /** 并发 Create 与 Release 混跑: 不崩、不残留、每次 Create 返回的会话都能查到。 */
    [Fact]
    public async Task ConcurrentCreateAndRelease_LeavesNoResidue()
    {
        var ctx = new Context();
        var store = new SessionStore(ctx);

        var work = Enumerable.Range(0, ParallelCreates).Select(index => Task.Run(() =>
        {
            var session = store.Create();
            Assert.Same(session, store.Get(session.Id));
            if (index % 2 == 0)
                store.Release(session.Id);
            return session;
        }));

        var survivors = await Task.WhenAll(work);

        Assert.Equal(ParallelCreates / 2, store.List().Count);
        foreach (var released in survivors.Where((_, index) => index % 2 == 0))
            Assert.Null(store.Get(released.Id));
    }

    /** Release 递归释放子会话, 且不能在持锁状态下递归(否则自锁)。 */
    [Fact]
    public void Release_AlsoReleasesChildren()
    {
        var ctx = new Context();
        var store = new SessionStore(ctx);
        var parent = store.Create();
        var childId = SessionId.Create(Guid.NewGuid().ToString());
        var childSession = store.Create(
            childId,
            header: new SessionHeader
            {
                Version = SessionHeader.SessionFormatVersion,
                Id = childId,
                CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                ParentSession = parent.Id,
                IsSeeded = false,
            });

        store.Release(parent.Id);

        Assert.Null(store.Get(parent.Id));
        Assert.Null(store.Get(childSession.Id));
        Assert.Empty(store.List());
    }
}
