using System.Buffers.Binary;
using System.IO.Pipes;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Dsh.Runtime;

namespace Dsh.Ma2a;

internal static class Ma2aConnection
{
    internal const int MaximumRequestBytes = 16_384;
    internal const int MaximumResponseBytes = 65_536;
    private const int HeaderBytes = 12;
    private static readonly TimeSpan TransportDeadline = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan WindowsDirectoryWaitInterval = TimeSpan.FromMilliseconds(50);

    [SupportedOSPlatform("windows")]
    internal static string? WindowsPipeName(string stateDirectory)
    {
        var (path, missing) = FilePathAccess.ResolveWindowsLocalDirectoryPath(stateDirectory);
        if (missing)
            return null;
        // The daemon hashes the raw UTF-16LE code units of the resolved Windows path.
        return $"ma2a-{Blake3.Hasher.Hash(MemoryMarshal.AsBytes((path ?? stateDirectory).AsSpan()))}";
    }

    internal static async Task<Stream> ConnectAsync(string stateDirectory, CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TransportDeadline);
        if (OperatingSystem.IsWindows())
        {
            deadline.Token.ThrowIfCancellationRequested();
            // The daemon creates the state directory before naming and binding its pipe.
            var name = WindowsPipeName(stateDirectory);
            while (name is null)
            {
                await Task.Delay(WindowsDirectoryWaitInterval, deadline.Token);
                name = WindowsPipeName(stateDirectory);
            }
            var pipe = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
            try { await pipe.ConnectAsync(deadline.Token); return pipe; }
            catch { await pipe.DisposeAsync(); throw; }
        }
        var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        try
        {
            await socket.ConnectAsync(new UnixDomainSocketEndPoint(Path.Combine(stateDirectory, "run-v1", "control.sock")), deadline.Token);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch { socket.Dispose(); throw; }
    }

    internal static async Task WriteAsync(Stream stream, ulong correlation, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken)
    {
        if (payload.Length is 0 or > MaximumRequestBytes)
            throw new Ma2aProtocolException("invalid_frame", "The local request exceeds the protocol bound.");
        var header = new byte[HeaderBytes];
        BinaryPrimitives.WriteUInt32BigEndian(header, (uint)payload.Length);
        BinaryPrimitives.WriteUInt64BigEndian(header.AsSpan(sizeof(uint)), correlation);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TransportDeadline);
        await stream.WriteAsync(header, deadline.Token);
        await stream.WriteAsync(payload, deadline.Token);
        await stream.FlushAsync(deadline.Token);
    }

    internal sealed class ResponseReader(Stream stream, ulong correlation, CancellationToken cancellationToken) : IDisposable
    {
        private readonly CancellationTokenSource _deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        private readonly byte[] _header = new byte[HeaderBytes];

        internal async Task<byte[]> ReadAsync(bool waitForReply = false)
        {
            if (waitForReply)
                await stream.ReadExactlyAsync(_header.AsMemory(0, 1), cancellationToken);
            else
                await ReadBoundedAsync(_header.AsMemory(0, 1));
            await ReadBoundedAsync(_header.AsMemory(1));
            var length = BinaryPrimitives.ReadUInt32BigEndian(_header);
            if (length is 0 or > MaximumResponseBytes)
                throw new Ma2aProtocolException("invalid_frame", "The local response exceeds the protocol bound.");
            if (BinaryPrimitives.ReadUInt64BigEndian(_header.AsSpan(sizeof(uint))) != correlation)
                throw new Ma2aProtocolException("correlation_mismatch");
            var payload = new byte[length];
            await ReadBoundedAsync(payload);
            return payload;
        }

        private async Task ReadBoundedAsync(Memory<byte> buffer)
        {
            _deadline.CancelAfter(TransportDeadline);
            try { await stream.ReadExactlyAsync(buffer, _deadline.Token); }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            { throw new Ma2aProtocolException("invalid_frame", "The peer stalled while sending a frame."); }
            finally { _deadline.CancelAfter(Timeout.InfiniteTimeSpan); }
        }

        public void Dispose() => _deadline.Dispose();
    }
}
