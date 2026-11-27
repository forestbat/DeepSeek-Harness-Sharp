using System.Buffers.Binary;

namespace Dsh.Ma2a.Services;

/// <summary>Bounds the native service payload before allocation or reading.</summary>
public static class Ma2aServiceFraming
{
    private const int HeaderLength = 5;

    public static async Task<Ma2aServiceFrame> ReadAsync(Stream stream, CancellationToken cancellationToken = default)
    {
        var header = new byte[HeaderLength];
        await stream.ReadExactlyAsync(header, cancellationToken);
        var kind = (Ma2aFrameKind)header[0];
        var length = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(1));
        Ma2aServiceSession.ValidateLength(kind, length);
        var payload = new byte[(int)length];
        await stream.ReadExactlyAsync(payload, cancellationToken);
        return new Ma2aServiceFrame(kind, payload);
    }
}
