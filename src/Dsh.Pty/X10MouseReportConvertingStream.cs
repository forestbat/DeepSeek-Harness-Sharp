namespace Dsh.Pty;

/** 把终端输入流整条穿过 X10MouseReportConverter: X10 变成 SGR 后进会话, SGR 原样透传。 */
internal sealed class X10MouseReportConvertingStream(Stream inner) : Stream
{
    private readonly X10MouseReportConverter _converter = new();

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
            var converted = _converter.Convert(buffer.Span[..read]);
            if (converted.Length == 0)
                continue;   // 整段都还挂在 carry 里(报文被切开): 继续读, 不要把它当输入结尾
            converted.CopyTo(buffer.Span);
            return converted.Length;
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
