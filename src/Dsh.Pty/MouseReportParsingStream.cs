using System.Threading.Channels;

namespace Dsh.Pty;

/** 透传宿主终端字节, 同时把其中的鼠标报文解析成事件交给代理发送(见 MouseReportParser 的说明)。 */
internal sealed class MouseReportParsingStream(Stream inner, ChannelWriter<PtyMouseEvent> sink) : Stream
{
    private readonly MouseReportParser _parser = new();
    private readonly List<PtyMouseEvent> _events = [];

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => 0;

    public override long Position
    {
        get => 0;
        set { }
    }

    public override int Read(byte[] buffer, int offset, int count)
        => ReadAsync(buffer.AsMemory(offset, count), CancellationToken.None).AsTask().GetAwaiter().GetResult();

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        while (true)
        {
            var read = await inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read <= 0)
                return read;

            _events.Clear();
            var passThrough = _parser.Push(buffer.Span[..read], _events);
            foreach (var mouse in _events)
                sink.TryWrite(mouse);

            if (passThrough.Length == 0)
                continue;   // 整段都是(或还挂着)鼠标报文: 继续读, 不要把它当输入结尾
            passThrough.CopyTo(buffer.Span);
            return passThrough.Length;
        }
    }

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin) => 0;

    public override void SetLength(long value)
    {
    }

    public override void Write(byte[] buffer, int offset, int count)
    {
    }
}
