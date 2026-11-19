namespace Dsh.Persistence;

/**
 * 写盘批次: 一个压缩帧的预算 —— 先到者提交。
 *
 *  - MaxPlaintextBytes: 这一帧装机前的**明文字节**上限。它决定帧有多大, 而帧大小同时决定两头:
 *    帧越大, 一帧内的重复内容压得越好(日志更小); 帧越小, 随机访问(窗口/翻页)解的帧越少。
 *    d 档实测: 一次落盘装 200 条大工具输出时帧只有约 100 KB 明文, 把原来一次 Materialize 装完的
 *    124,664 条事件切成 1,642 帧后退回 7.22 MB(+29%)—— 跨事件冗余压不掉了。
 *  - MaxEvents: 条数上限, 兜底两件事: 百万级小事件不能让一帧攒到几 MB 明文(逐条估算的误差也会累积),
 *    一条超大 payload 不能把一帧独占成"等它的定时器"。
 *  - Interval: 时间上限, 决定崩溃(断电/被杀)时最多丢多久的增量, 也决定交互事件多久可见。
 *
 * 消息、工具调用、回合边界与流式 chunk 一样攒批 —— 它们不再单独触发落盘,
 * 否则每条消息、每次工具调用、每个回合边界都会是一个只有一条事件的帧(现场日志实测 99.7%~100% 的帧就是这样)。
 * 首次落盘仍然立即发生: 日志文件的存在与可见性(会话目录、侧栏)依赖它, 见 JsonlSessionHandle.Append。
 */
public readonly record struct JsonlWriteBatch(int MaxEvents, TimeSpan Interval)
{
    public const int DefaultMaxEvents = 20_000;

    /** 1 MB: 128 条 8 KB 的工具输出、或 ~2,000 条普通事件落成一帧; 见 plans/帧粒度调粗-2026-11-19.md 的参数扫描。 */
    public const int DefaultMaxPlaintextBytes = 1024 * 1024;

    public static readonly TimeSpan DefaultInterval = TimeSpan.FromMilliseconds(100);

    public int MaxPlaintextBytes { get; init; } = DefaultMaxPlaintextBytes;

    public static JsonlWriteBatch Default => new(DefaultMaxEvents, DefaultInterval);
}