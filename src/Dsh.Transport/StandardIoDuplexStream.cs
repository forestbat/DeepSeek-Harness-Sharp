namespace Dsh.Transport;

/**
 * 把进程 stdin/stdout 当成一条双向流(stdio-over-SSH: `ssh host dsharp host --serve`)。
 * 读来自 input, 写去 output; 不监听任何网络端口, 攻击面最小。
 */
public sealed class StandardIoDuplexStream(Stream input, Stream output, bool ownsStreams = false) : Stream
{
    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => true;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override void Flush() => output.Flush();

    public override Task FlushAsync(CancellationToken cancellationToken) => output.FlushAsync(cancellationToken);

    public override int Read(byte[] buffer, int offset, int count) => input.Read(buffer, offset, count);

    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        => input.ReadAsync(buffer, cancellationToken);

    public override void Write(byte[] buffer, int offset, int count) => output.Write(buffer, offset, count);

    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        => output.WriteAsync(buffer, cancellationToken);

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing && ownsStreams)
        {
            input.Dispose();
            output.Dispose();
        }

        base.Dispose(disposing);
    }

    public override async ValueTask DisposeAsync()
    {
        if (ownsStreams)
        {
            await input.DisposeAsync().ConfigureAwait(false);
            await output.DisposeAsync().ConfigureAwait(false);
        }

        await base.DisposeAsync().ConfigureAwait(false);
    }
}
