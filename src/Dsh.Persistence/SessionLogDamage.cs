namespace Dsh.Persistence;

/**
 * 读取会话日志时遇到的不可解析内容。
 *
 * 判据只有一条: 这段字节无法还原成事件。常见来源是跨版本编解码漂移(旧构建写下的记录新 codec 不认)、断电留下的半帧、
 * 以及外部工具把日志改坏。出现损伤时有两件事必须同时成立:
 * 会话要能打开(只装载可读前缀), 磁盘上的原始字节不能被丢掉 —— 由首次以写身份打开时的"另存原文 + 重写规范日志"负责。
 */
internal sealed record LogDamage(int Records, long? FirstByte, long? LastGoodByte)
{
    public static readonly LogDamage None = new(0, null, null);

    public bool Any => Records > 0;

    public LogDamage Plus(int records, long? firstByte, long? lastGoodByte = null)
        => records <= 0
            ? this
            : new LogDamage(Records + records, FirstByte ?? firstByte, lastGoodByte ?? LastGoodByte);
}