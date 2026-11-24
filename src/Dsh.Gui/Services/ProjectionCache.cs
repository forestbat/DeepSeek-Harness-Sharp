using System.Text.Json;
using Dsh.Core;

namespace Dsh.Gui.Services;

/**
 * 投影结果的磁盘缓存: 每台机器的每个会话一个文件, 内容是按 (格式版本, 日志修订) 校验过的投影快照。
 *
 * 缓存是**可以没有**的东西: 读不出来、版本不对、修订对不上、文件坏了, 一律当作没命中, 由调用方退回
 * "解析 + 重放"那条正路。唯一的硬约束是绝不能把过期的投影当成当前状态 —— 那表现为"重开之后少一段",
 * 比慢难查得多, 所以校验分四层: 文件头格式版本、日志修订(长度与事件数只增不减)、覆盖完整性(NextSeq 必须等于
 * 写下时的总条数, 半截投影不写也不认)、末事件锚点(时刻+类型)。
 */
public sealed class ProjectionCache(string root)
{
    /** 与远端会话日志目录区分开: 这是**客户端**的派生数据, 换机器就不用再算一遍重放。 */
    public const string DirectoryName = "projection-cache";

    private static readonly SemaphoreSlim Gate = new(1, 1);

    public string Root { get; } = root;

    public static ProjectionCache ForHome(string homeRoot)
        => new(Path.Combine(homeRoot, "cache", DirectoryName));

    /**
     * 读一份可能命中的缓存。
     *
     * `logLength`/`totalEvents` 是宿主报的当前修订: 两者都不小于缓存写下的值才算前缀兼容(日志只追加);
     * 末事件锚点用来兜住"回退之后又追加到同样长度"这种长度看不出来的改写。
     */
    public ProjectionSnapshot? TryLoad(string machine, string sessionId, long logLength, long totalEvents)
    {
        var path = PathFor(machine, sessionId);
        ProjectionSnapshot? snapshot;
        try
        {
            if (!File.Exists(path))
                return null;
            snapshot = ProjectionSnapshotCodec.Decode(File.ReadAllBytes(path));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return null;
        }
        if (snapshot is null)
            return null;
        if (snapshot.LogLength > logLength || snapshot.TotalEvents > totalEvents || snapshot.NextSeq > totalEvents)
            return null;
        // 只认"覆盖到末尾"的投影: NextSeq 落后于写下时的总条数, 说明这份投影是在历史没取全的状态下存的
        // (跳号/重同步/半截载入), 它前面的内容不可信 —— 用它还原就是"重开之后少一段"。
        if (snapshot.NextSeq != snapshot.TotalEvents)
            return null;
        return snapshot;
    }

    /**
     * 锚点校验: 缓存写下的最后一条事件, 现在还是不是同一条?
     * 调用方把当前日志里 `NextSeq - 1` 那条事件取来比对; 对不上就当作没命中。
     */
    public static bool AnchorMatches(ProjectionSnapshot snapshot, SessionEvent? anchor)
    {
        if (snapshot.NextSeq == 0)
            return true;
        if (anchor is null || anchor.Seq != snapshot.NextSeq - 1)
            return false;
        if (snapshot.LastEventTime is { } time && anchor.Time != time)
            return false;
        return snapshot.LastEventType is not { Length: > 0 } type || string.Equals(anchor.Type, type, StringComparison.Ordinal);
    }

    /** 存一份(原子替换: 先写临时文件再换过去, 半个文件不会被下次当成缓存读出来)。 */
    public async Task SaveAsync(string machine, string sessionId, ProjectionSnapshot snapshot, CancellationToken cancellationToken = default)
    {
        // 只存"已经投影到日志末尾"的那一份: 跳号/半截载入时存下来的投影前面可能有个洞,
        // 存了就是给下一次重开准备一个"少一段"的界面。
        if (snapshot.NextSeq != snapshot.TotalEvents)
            return;
        // 忙着的界面不能当"下次打开的样子": 那个回合早就随进程结束了, 存下来只会固化出
        // "一打开就按不动停止按钮"的死界面(真实发生过)。
        if (snapshot.IsBusy || snapshot.CompactionBusy)
            return;
        var path = PathFor(machine, sessionId);
        byte[] bytes;
        try
        {
            // 编码是 CPU 活(百万条会话 5MB 快照约 0.5s), 不能占着调用方的线程做: 这个调用是打开会话时
            // 顺手发起的, 卡住的就是那一帧界面。
            bytes = await Task.Run(() => ProjectionSnapshotCodec.Encode(snapshot), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException)
        {
            return;
        }
        await Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var temp = $"{path}.{Guid.NewGuid():N}.tmp";
            await File.WriteAllBytesAsync(temp, bytes, cancellationToken).ConfigureAwait(false);
            File.Move(temp, path, overwrite: true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // 缓存写不下去不是错误: 下次重开只是慢一次。
        }
        finally
        {
            Gate.Release();
        }
    }

    /** 日志被回退/重写(长度变小)时直接删掉: 留着只会在下次读的时候被判失效, 白占磁盘。 */
    public void Drop(string machine, string sessionId)
    {
        try
        {
            var path = PathFor(machine, sessionId);
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
        }
    }

    public string PathFor(string machine, string sessionId)
        => Path.Combine(Root, SafeSegment(machine), $"{SafeSegment(sessionId)}.projection");

    private static string SafeSegment(string value)
    {
        var builder = new System.Text.StringBuilder(value.Length);
        foreach (var character in value)
            builder.Append(char.IsLetterOrDigit(character) || character is '-' or '_' or '.' ? character : '_');
        return builder.ToString();
    }
}