using Dsh.Boot;
using Dsh.Lsp;
using Dsh.Transport;

namespace Dsh.Host;

internal static class HostStdio
{
    private const int MaxFramePrefixByte = FrameCodec.DefaultMaxFrameBytes >> ((FrameCodec.HeaderLength - 1) * 8);

    public static async Task<int> RunAsync(HarnessHome home, string? token)
    {
        using var duplex = TransportConnection.FromStandardIo();
        var prefix = new byte[1];
        if (await duplex.ReadAsync(prefix).ConfigureAwait(false) == 0) return 0;
        using var input = new PrefixedStream(duplex, prefix[0]);
        if (prefix[0] <= MaxFramePrefixByte)
        {
            await HostAutostart.EnsureRunningAsync(home, token).ConfigureAwait(false);
            await HostBridge.RunAsync(input, home.Root, CancellationToken.None).ConfigureAwait(false);
            return 0;
        }

        using var app = await ConfigBoot.Compose(new HarnessOptions(home, Directory.GetCurrentDirectory(), IsTui: false)).ConfigureAwait(false);
        if (app.Ctx.Get<LspServer>(LspServer.ServiceName, false) is not { } server)
        {
            await Console.Error.WriteLineAsync("dsharp: LSP capability is not active; enable @deepseek-ai/dsh-lsp.");
            return 1;
        }
        return await server.RunStdioAsync(input, input).ConfigureAwait(false);
    }

    private sealed class PrefixedStream(Stream inner, byte first) : Stream
    {
        private bool _pending = true;
        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => inner.CanWrite;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Flush() => inner.Flush();
        public override Task FlushAsync(CancellationToken cancellationToken) => inner.FlushAsync(cancellationToken);
        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
        public override int Read(Span<byte> buffer)
        {
            if (buffer.IsEmpty) return 0;
            if (!_pending) return inner.Read(buffer);
            buffer[0] = first;
            _pending = false;
            return 1;
        }
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (cancellationToken.IsCancellationRequested) return ValueTask.FromCanceled<int>(cancellationToken);
            if (buffer.IsEmpty) return ValueTask.FromResult(0);
            if (!_pending) return inner.ReadAsync(buffer, cancellationToken);
            buffer.Span[0] = first;
            _pending = false;
            return ValueTask.FromResult(1);
        }
        public override void Write(byte[] buffer, int offset, int count) => inner.Write(buffer, offset, count);
        public override void Write(ReadOnlySpan<byte> buffer) => inner.Write(buffer);
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
            => inner.WriteAsync(buffer, cancellationToken);
    }
}
