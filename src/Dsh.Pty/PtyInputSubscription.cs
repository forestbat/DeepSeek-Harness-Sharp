using System.Threading.Channels;

namespace Dsh.Pty;

/**
 * 常驻 TUI 对会话输入的旁路订阅(见 PtySession.SubscribeInput)。
 * 一次性: Dispose 之后会话输入恢复写 pty。
 */
public sealed class PtyInputSubscription : IDisposable
{
    private readonly PtySession _session;
    private readonly Channel<byte[]> _channel = Channel.CreateUnbounded<byte[]>(
        new UnboundedChannelOptions { SingleReader = true });

    private bool _disposed;

    internal PtyInputSubscription(PtySession session) => _session = session;

    public ChannelReader<byte[]> Reader => _channel.Reader;

    /** 输入不能丢: 无界队列的风险只有"读端卡住时涨内存", 比丢字节可控。 */
    internal bool TryWrite(byte[] bytes) => _channel.Writer.TryWrite(bytes);

    internal void Complete() => _channel.Writer.TryComplete();

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _session.UnsubscribeInput(this);
        Complete();
    }
}
