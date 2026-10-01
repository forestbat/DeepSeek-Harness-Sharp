namespace Dsh.Pty;

/** 把会话输出整条穿过 MouseModeFilter: "开/关鼠标上报"的模式序列不会到达宿主终端。 */
internal sealed class MouseModeFilteringStream(Stream inner) : Stream
{
    private readonly MouseModeFilter _filter = new();

    public override bool CanRead => false;

    public override bool CanSeek => false;

    public override bool CanWrite => true;

    public override long Length => 0;

    public override long Position
    {
        get => 0;
        set { }
    }

    public override void Write(byte[] buffer, int offset, int count)
        => WriteAsync(buffer.AsMemory(offset, count), CancellationToken.None).AsTask().GetAwaiter().GetResult();

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        var filtered = _filter.Filter(buffer.Span);
        if (filtered.Length == 0)
            return;
        await inner.WriteAsync(filtered, cancellationToken).ConfigureAwait(false);
    }

    public override void Flush() => inner.Flush();

    public override Task FlushAsync(CancellationToken cancellationToken) => inner.FlushAsync(cancellationToken);

    public override int Read(byte[] buffer, int offset, int count) => 0;

    public override long Seek(long offset, SeekOrigin origin) => 0;

    public override void SetLength(long value)
    {
    }
}
