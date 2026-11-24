using System.Security.Cryptography;
using Dsh.Llm;

namespace Dsh.Core;

public enum SessionAccess
{
    Read,
    Write,
}

public sealed record SessionPersistenceSnapshot
{
    public required SessionHeader Header { get; init; }
    public required string Revision { get; init; }
    public long? EventCount { get; init; }
    public long? SizeBytes { get; init; }
    public long InheritedEventCount { get; init; }
}

/**
 * 发号凭证的读侧: 会话在**创建事件之前**向它要一个序号, 事件一起床就带最终号。
 *
 * 号必须先定再广播, 而广播又必须早于落盘, 所以"从哪个号开始"不能靠各自内存里的计数 ——
 * 那正是两个宿主把同一段序号写进同一份日志的来源。实现方(JSONL 后端)用一张写票把
 * "此刻由谁发号、下一个号是几"记在日志旁边, 起点一律以日志真实条数为准。
 */
public interface ISessionSequencer
{
    /** 领票并返回下一个可用序号; 别人正持着票时抛可重试的异常(这一次先别写, 下次再试)。 */
    long NextSeq();

    /** 读回日志上 [from, to) 的事件: 别的写者写进去的那一段, 用于把内存对齐到磁盘。 */
    IReadOnlyList<SessionEvent> ReadRange(long from, long to);

    /** 已经落盘的事件条数(不含还在缓冲里的): 重编号只能从它之后开始, 否则会把已写的号改掉。 */
    long DurableCount { get; }
}

public interface ISessionHandle : IDisposable
{
    SessionId Id { get; }
    SessionHeader Header { get; }
    long InheritedEventCount { get; }
    SessionAccess Access { get; }
    /** 日志里的事件总数: 写句柄取自游标, 读句柄取自已解析缓冲(必要时才整份读)。 */
    long Count { get; }
    IReadOnlyList<SessionEvent> Read(long offset = 0, long? length = null);
    void Append(IReadOnlyList<SessionEvent> events);
    /**
     * 会话里"从某个绝对序号起"的全部事件: 落盘落后于内存时靠它把缺的那一段整段补写进去。
     *
     * 由持久化接线在创建句柄时设置。没设置时, 写句柄只能用磁盘条数给自己对账, 补不出内存里的事件。
     */
    Func<long, IReadOnlyList<SessionEvent>>? TailSource { get; set; }

    /**
     * 号被别的写者接管时调用: 参数是日志真实条数, 返回**按这个起点重编号之后**的未落盘事件。
     * 会话在实现里平移内存(并发出重编号通知), 句柄据此改号重写, 一条事件都不丢。
     */
    Func<long, IReadOnlyList<SessionEvent>>? RebaseSource { get; set; }

    /** 发号凭证: 写句柄才有; 只读句柄与会话内存里的序号是唯一事实时返回 null。 */
    ISessionSequencer? Sequencer => null;
    void Flush();
    void Close();
}
public interface ISessionPersistence
{
    public const string ServiceName = "sessionPersistence";

    ISessionHandle Create(SessionHeader header, long? inheritedEventCount = null);
    ISessionHandle Open(SessionId id, SessionAccess access);
    SessionPersistenceSnapshot? Stat(SessionId id);
    IReadOnlyList<SessionPersistenceSnapshot> List();

    /**
     * 日志里**已经落盘**的事件条数(不存在的会话为 0)。
     *
     * 落盘与内存的唯一对账依据: 写句柄的游标可能因为"上一批没写成功"而带着未落盘的事件,
     * 只有磁盘上的条数才能说明这一批该从哪条序号开始写。
     */
    long StoredEventCount(SessionId id);

    void Rename(SessionId id, string title);
    void Delete(SessionId id);

    /**
 * 窗口化打开: 只把尾部一段事件读进内存, 更早的留给按页重读。
 * 不支持的实现(以及日志太小、结构不可信的后端判断)返回 null, 调用方退回 Open + Read。
 */
    SessionWindow? TryOpenWindow(SessionId id, int tailEvents) => null;

    /**
     * 按帧索引取一段**日志字节**: 头帧 + 覆盖 [fromEvent, fromEvent + maxEvents) 的帧, 不解压。
     *
     * 把历史交给别的进程时这是成本下界 —— 交出去的就是磁盘上那几帧, 由接收方自己解析。
     * 不支持的后端返回 null, 调用方退回事件快照。
     */
    SessionLogSlice? TryReadLogSlice(SessionId id, long fromEvent, int maxEvents) => null;

    /**
     * 派生缓存: 上次算出来的表面折叠结果, 只要日志没变短、事件没变少就能接着用。
     * 不支持的后端返回 null, 调用方照旧把日志折一遍。
     */
    SurfaceFoldSnapshot? TryLoadSurfaceFold(SessionId id) => null;

    /** 存下表面折叠结果(派生缓存); 不支持的后端什么也不做。 */
    void SaveSurfaceFold(SessionId id, SurfaceFoldSnapshot fold)
    {
    }

    /** 日志变短(回退/修复)时作废派生缓存。 */
    void DropSurfaceFold(SessionId id)
    {
    }
}

/**
 * 一段日志字节: 头帧 + 覆盖 [FromSeq, FromSeq + EventCount) 的事件帧, 原样字节。
 *
 * FromSeq 是**覆盖请求起点的那一帧**的首序号, 可能早于请求的起点(帧边界只落在行边界上, 不许劈行);
 * 接收方按序号裁剪即可。序号是绝对的, 起点不是 0 时也照原样编号, 才能与实时流接上。
 *
 * ContentHash 是整段字节的 SHA-256: 切片不可变, 于是它能当内容标识 —— 接收方校验、跨节点去重/缓存都靠它。
 */
public sealed record SessionLogSlice(
    byte[] Bytes,
    SessionHeader Header,
    long FromSeq,
    int EventCount,
    long TotalEventCount,
    string ContentHash)
{
    /** 这段字节的内容标识(SHA-256 十六进制)。 */
    public static string HashOf(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
}

/**
 * 表面折叠结果的快照(折叠检查点): 表面是**派生状态**, 折一遍要读整条日志,
 * 而它只有几百到几千个序号。存下来之后, 恢复会话不必再折前缀。
 *
 * Nodes 是表面节点的绝对序号, 递增; LogLength/TotalEvents 是写入时的日志长度与事件总数,
 * 加载时两者都要与当前日志对得上(日志只追加, 长度不会变小; 回退/修复会变小 → 缓存作废)。
 *
 * Header/Context 是 `request/header` 与 `request/context` 的折叠值: 尾窗里常常没有这两条
 * (最近一次请求可能在窗口之外), 带在检查点里就不必为了它们把整份前缀扫一遍。
 */
public sealed record SurfaceFoldSnapshot(
    IReadOnlyList<long> Nodes,
    long LogLength,
    long TotalEvents,
    int ReplaceGeneration,
    EpochHeader? RequestHeader = null,
    RequestContextPayload? RequestContext = null)
{
    /** 快照只对"日志没变短、事件没变少"的日志有效。 */
    public bool Fits(long logLength, long totalEvents)
        => LogLength <= logLength && TotalEvents <= totalEvents && Nodes.Count <= TotalEvents;
}

/** 折叠检查点的尾部信息: 尾窗里常常没有这两条事件, 带上它们才能少扫一遍前缀。 */
public sealed record SurfaceFoldMeta(EpochHeader? RequestHeader, RequestContextPayload? RequestContext);

public static class SessionPersistenceMetadata
{
    /**
     * 空会话(还没有任何事件)默认不落盘; 改名是用户明确的动作, 此时先建出只含头的日志再写标题,
     * 否则标题只活在内存里, 重启后就丢了。
     */
    public static void RenameSession(this ISessionPersistence persistence, SessionHeader header, string title)
    {
        if (persistence.Stat(header.Id) is null)
        {
            // Create 只登记内存待写; 必须 Flush 才会真正把(只有头的)日志写盘。
            using var handle = persistence.Create(header);
            handle.Flush();
        }
        persistence.Rename(header.Id, title);
    }
}
