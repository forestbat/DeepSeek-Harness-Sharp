using System.Diagnostics;
using System.Text;
using Dsh.Transport;

namespace Dsh.Fleet;

/** Internal process lease for the host handshake; never exposed as a second raw web/relay API. */
internal sealed class SshCommandChannel : IDisposable
{
    private const int HandshakeBannerLimit = 512;
    private string? _askPass;
    private readonly Func<string> _errorOutput;
    private readonly HandshakePreviewStream _stdout;
    private int _disposed;

    public SshCommandChannel(Process process, string? askPass, Func<string> errorOutput)
    {
        Process = process;
        _askPass = askPass;
        _errorOutput = errorOutput;
        _stdout = new HandshakePreviewStream(process.StandardOutput.BaseStream, HandshakeBannerLimit);
        Stream = new StandardIoDuplexStream(_stdout, process.StandardInput.BaseStream);
    }

    public Process Process { get; }
    public Stream Stream { get; }
    public string ErrorOutput => _errorOutput();
    public string BannerOutput => _stdout.BannerText;

    public void AuthenticationCompleted()
    {
        _stdout.CompleteHandshake();
        if (Interlocked.Exchange(ref _askPass, null) is { } path)
            SshTransport.TryDelete(path);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        try
        {
            Stream.Dispose();
        }
        finally
        {
            SshTransport.Kill(Process);
            AuthenticationCompleted();
        }
    }

    /** Captures initial stdout bytes only until handshake succeeds, surfacing plain-text startup errors like wsl.exe's stdout messages. */
    private sealed class HandshakePreviewStream(Stream inner, int limit) : Stream
    {
        private readonly object _gate = new();
        private byte[]? _buffer = new byte[limit];
        private int _count;

        public string BannerText
        {
            get
            {
                lock (_gate)
                {
                    if (_buffer is null || _count == 0)
                        return "";
                    var slice = _buffer.AsSpan(0, _count);
                    return slice.Contains((byte)'\0') ? "" : Encoding.UTF8.GetString(slice).Trim();
                }
            }
        }

        public void CompleteHandshake()
        {
            lock (_gate)
            {
                _buffer = null;
                _count = 0;
            }
        }

        private void Record(ReadOnlySpan<byte> data)
        {
            lock (_gate)
            {
                if (_buffer is null || _count >= _buffer.Length || data.IsEmpty)
                    return;
                var take = Math.Min(data.Length, _buffer.Length - _count);
                data[..take].CopyTo(_buffer.AsSpan(_count));
                _count += take;
            }
        }

        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => inner.CanSeek;
        public override bool CanWrite => inner.CanWrite;
        public override long Length => inner.Length;
        public override long Position { get => inner.Position; set => inner.Position = value; }
        public override void Flush() => inner.Flush();
        public override Task FlushAsync(CancellationToken cancellationToken) => inner.FlushAsync(cancellationToken);

        public override int Read(byte[] buffer, int offset, int count)
        {
            var read = inner.Read(buffer, offset, count);
            if (read > 0)
                Record(buffer.AsSpan(offset, read));
            return read;
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var read = await inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read > 0)
                Record(buffer.Span[..read]);
            return read;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
        public override void SetLength(long value) => inner.SetLength(value);
        public override void Write(byte[] buffer, int offset, int count) => inner.Write(buffer, offset, count);
    }
}
