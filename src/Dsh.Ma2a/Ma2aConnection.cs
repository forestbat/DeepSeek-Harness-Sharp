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

    [SupportedOSPlatform("windows")]
    internal static string WindowsPipeName(string stateDirectory)
    {
        var path = FilePathAccess.ResolveWindowsLocalDirectoryPath(stateDirectory) ?? stateDirectory;
        // The daemon hashes the raw UTF-16LE code units of the resolved Windows path.
        return $"ma2a-{Blake3.Hasher.Hash(MemoryMarshal.AsBytes(path.AsSpan()))}";
    }

    internal static async Task<Stream> ConnectAsync(string stateDirectory, CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TransportDeadline);
        if (OperatingSystem.IsWindows())
        {
            var pipe = new NamedPipeClientStream(".", WindowsPipeName(stateDirectory), PipeDirection.InOut, PipeOptions.Asynchronous);
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

    internal static async Task<byte[]> ReadAsync(Stream stream, ulong correlation, CancellationToken cancellationToken, bool waitForReply = true)
    {
        var header = new byte[HeaderBytes];
        if (waitForReply)
            await stream.ReadExactlyAsync(header.AsMemory(0, 1), cancellationToken);
        else
            await ReadBoundedAsync(stream, header.AsMemory(0, 1), cancellationToken);
        await ReadBoundedAsync(stream, header.AsMemory(1), cancellationToken);
        var length = BinaryPrimitives.ReadUInt32BigEndian(header);
        if (length is 0 or > MaximumResponseBytes)
            throw new Ma2aProtocolException("invalid_frame", "The local response exceeds the protocol bound.");
        if (BinaryPrimitives.ReadUInt64BigEndian(header.AsSpan(sizeof(uint))) != correlation)
            throw new Ma2aProtocolException("correlation_mismatch");
        var payload = new byte[length];
        await ReadBoundedAsync(stream, payload, cancellationToken);
        return payload;
    }

    private static async Task ReadBoundedAsync(Stream stream, Memory<byte> buffer, CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TransportDeadline);
        try { await stream.ReadExactlyAsync(buffer, deadline.Token); }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        { throw new Ma2aProtocolException("invalid_frame", "The peer stalled while sending a frame."); }
    }
}
