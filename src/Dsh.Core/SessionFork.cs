using Dsh.Llm;

namespace Dsh.Core;

/**
 * fork 的共享实现: TUI 的 `/timestamp fork` 与检查点的 `/checkpoints restore` 共用这套"复制父会话前缀 + 落盘"逻辑。
 *
 * 落成**种子会话**(与宿主 live 会话 `Session.Create(seed, IsSeeded:true, inheritedEventCount)` 同一形态):
 * - 继承父会话 [0, toSeqExclusive) 的事件, 末尾补 `session/end-seed` 标记, 日志头写 `seedLength`;
 * - `InheritedEventCount` 让 `OwnEvents()`/`TryTruncateTo` 只在本会话自有区间内动作, 种子区间不可回退;
 * - 种子部分不被 `SessionRepair.InterruptedTurnClosers` 统计, 所以打开(resume)这个 fork 不会补任何合成事件,
 *   也就不会出现"请判断是否重试"这类会被模型当成继续指令的文本 —— fork 只复制, 不触发推理。
 * cwd 用调用方(输入窗格/检查点)的工作目录: 进程内会话的 header 可能没带 cwd, 不带过去会让 fork 显示"(无工作目录)"。
 */
public static class SessionFork
{
    public static SessionId Fork(
        ISessionPersistence persistence,
        Session source,
        long toSeqExclusive,
        string title,
        string? cwd = null)
    {
        if (toSeqExclusive < 0)
            throw new InvalidOperationException($"fork 失败: {source.Id} 的 fork 位置 {toSeqExclusive} 无效");
        // 允许空种子: "从第一条用户消息之前 fork" 得到的就是一个只有握手事件的空会话。
        var seed = source.SnapshotEvents(0, Math.Min(toSeqExclusive, source.Seq));
        var header = source.Header with
        {
            Id = SessionId.Create($"session-fork-{Guid.NewGuid():N}"),
            Title = title,
            Cwd = string.IsNullOrWhiteSpace(cwd) ? source.Header.Cwd : cwd,
            CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            ParentSession = source.Id,
            ParentCallId = null,
            IsSeeded = true,
        };
        // 种子标记带的是"fork 发生的时刻": 种子里的最后一条用户消息带的是父会话的旧时间,
        // 拿它当新会话的活动时间会让 fork 在列表里沉到几天前。
        var marker = new SessionEvent
        {
            Type = SessionEventTypes.SessionEndSeed,
            Seq = seed.Count,
            Time = header.CreatedAt,
            Data = new SessionEndSeedPayload(),
        };
        using (var handle = persistence.Create(header, inheritedEventCount: seed.Count))
        {
            handle.Append([.. seed, marker]);
            handle.Close();
        }
        return header.Id;
    }

    /** fork 出的会话沿用父会话的标题(没有标题才退回 id): 用 id 命名的话侧栏里全是 uuid。 */
    public static string ForkTitle(Session source, long seq)
        => $"fork of {(string.IsNullOrWhiteSpace(source.Header.Title) ? source.Id.Value : source.Header.Title)} @{seq}";
}
