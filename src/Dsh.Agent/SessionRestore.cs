using Dsh.Core;
using Dsh.Llm;

namespace Dsh.Agent;

/**
 * 打开一个已存在的会话: **有折叠检查点才窗口化**, 否则整份恢复。
 *
 * 这是宿主、TUI、GUI 宿主服务与离线探针共用的那一条路径 —— 以前它写在 `AgentLoop` 里(private),
 * 于是"打开性能基准"只能按同样的顺序把同一批产品函数再调一遍; 现在基准调的就是产品本身。
 */
public static class SessionRestore
{
    /** 窗口化恢复的尾窗大小: 几十个回合的量, 渲染与压缩都在这段里工作。 */
    public const int ResumeTailEvents = 20_000;

    public static Session Restore(SessionId id, ISessionPersistence persistence, ISessionHandle handle)
    {
        var adopted = persistence.TryLoadSurfaceFold(id);
        if (adopted is not null && persistence.TryOpenWindow(id, ResumeTailEvents) is { } paged)
        {
            // 中断收尾只看尾部: 未闭合的 turn / 悬空工具调用只可能出现在最后。
            // 第二个参数是"传入列表内的偏移": 窗口会话里自有的起点是 ResidentFrom, 所以 fork 种子前缀
            // 在窗口内那一段要按 (inherited - ResidentFrom) 跳过, 否则会给冻结前缀补合成事件。
            var closers = SessionRepair.InterruptedTurnClosers(
                paged.Resident,
                Math.Max(0, handle.InheritedEventCount - paged.ResidentFrom));
            var resident = closers.Count > 0 ? [.. paged.Resident, .. closers] : paged.Resident;
            // 检查点里的表面折叠结果直接用: 这条路径因此不必再折前缀(那是 5–21s 的量级)。
            var windowed = Session.FromWindow(id,
                paged with { Resident = resident, TotalEvents = paged.TotalEvents + closers.Count },
                handle.Header, handle.InheritedEventCount, adopted);
            // 旧检查点里没有请求头/请求上下文(那时还不进快照): 从日志尾部往前找最近的那两条补上,
            // 而不是"缺这两项就整份恢复"—— 那会把 2 秒的冷启动变成 5 秒, 换来一个后台就能补的字段。
            if (adopted.RequestHeader is null || adopted.RequestContext is null)
            {
                // 后台补: 这段扫描可能读到巨型帧(单页 3~5s), 放在同步路径上等于把窗口化的收益全吃掉。
                _ = Task.Run(() => BackfillFoldMetaAsync(persistence, id, windowed), CancellationToken.None);
            }
            return windowed;
        }
        var persisted = handle.Read();
        // 只对"本会话自有"的事件补中断收尾; 种子(fork 前缀)是冻结历史, 不在其中(见 InterruptedTurnClosers)。
        var fullClosers = SessionRepair.InterruptedTurnClosers(persisted, handle.InheritedEventCount);
        var events = fullClosers.Count > 0 ? [.. persisted, .. fullClosers] : persisted;
        return Session.FromRestore(id, events, handle.Header, handle.InheritedEventCount);
    }

    /** 从日志里补回请求头/上下文(后台): 从尾部往前按页找, 找到就停; 有上限, 找不到就算了(按 Options 走)。 */
    private static async Task BackfillFoldMetaAsync(ISessionPersistence persistence, SessionId id, Session session)
    {
        const int page = 4096;
        const int maxPages = 16;
        await Task.Yield();
        ISessionHandle handle;
        try
        {
            handle = persistence.Open(id, SessionAccess.Read);
        }
        catch (Exception error) when (error is IOException or InvalidOperationException)
        {
            return;
        }
        using var _ = handle;
        EpochHeader? header = null;
        RequestContextPayload? context = null;
        var from = handle.Count;
        for (var read = 0; read < maxPages && from > 0; read += 1)
        {
            var start = Math.Max(0, from - page);
            IReadOnlyList<SessionEvent> slice;
            try
            {
                slice = handle.Read(start, (int)(from - start));
            }
            catch (Exception error) when (error is IOException or FormatException or InvalidOperationException)
            {
                return;
            }
            for (var index = slice.Count - 1; index >= 0; index -= 1)
            {
                switch (slice[index].Data)
                {
                    case RequestHeaderPayload request when header is null:
                        header = request.Header;
                        break;
                    case RequestContextPayload request when context is null:
                        context = request;
                        break;
                }
                if (header is not null && context is not null)
                {
                    session.AdoptFoldMeta(header, context);
                    return;
                }
            }
            from = start;
        }
        session.AdoptFoldMeta(header, context);
    }
}

