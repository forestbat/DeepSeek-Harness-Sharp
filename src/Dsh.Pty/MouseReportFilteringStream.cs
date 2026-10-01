namespace Dsh.Pty;

/**
 * 把终端输入流整条穿过 MouseReportFilter: 鼠标上报一个字节都不进会话。
 * 只在客户端(终端形态 attach)使用 —— 会话侧 ConPTY 已经把报文拆得无法识别, 见 MouseReportFilter 的说明。
 */
internal sealed class MouseReportFilteringStream(Stream inner) : Stream
{
    private readonly MouseReportFilter _filter = new();

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
            var filtered = _filter.Filter(buffer.Span[..read]);
            if (filtered.Length == 0)
                continue;   // 整段都是(或还挂着)鼠标报文: 继续读, 不要把它当输入结尾
            filtered.CopyTo(buffer.Span);
            return filtered.Length;
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
