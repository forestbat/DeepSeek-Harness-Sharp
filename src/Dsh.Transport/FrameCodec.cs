using System.Buffers.Binary;

namespace Dsh.Transport;

/**
 * 长度帧: 4 字节大端长度前缀 + 载荷(JSON-RPC 报文按 UTF-8 字节写入)。
 * 读写都基于 Stream; 读侧把粘包/拆包收敛在 ReadFrameAsync 内(不足则持续读)。
 * 这是中立层: ACP 与远程工作区都复用它, 彼此不依赖。
 */
public static class FrameCodec
{
    /** 长度前缀宽度(字节, 大端)。 */
    public const int HeaderLength = 4;

    /** 单帧默认上限: 防畸形长度前缀触发一次超大分配(工具输出应分片到该值以内)。 */
    public const int DefaultMaxFrameBytes = 16 * 1024 * 1024;

    public static async ValueTask WriteFrameAsync(
        Stream stream,
        ReadOnlyMemory<byte> payload,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (payload.Length > DefaultMaxFrameBytes)
            throw new ArgumentOutOfRangeException(nameof(payload), payload.Length, "帧载荷超过上限");
        var header = new byte[HeaderLength];
        BinaryPrimitives.WriteInt32BigEndian(header, payload.Length);
        await stream.WriteAsync(header, cancellationToken).ConfigureAwait(false);
        if (payload.Length > 0)
            await stream.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    /** 读到帧边界且流干净结束返回 null; 半帧 EOF 视为错误(IOException)。 */
    public static async ValueTask<byte[]?> ReadFrameAsync(
        Stream stream,
        int maxFrameBytes = DefaultMaxFrameBytes,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        var header = new byte[HeaderLength];
        var headerRead = await ReadAtLeastAsync(stream, header, allowEofAtStart: true, cancellationToken).ConfigureAwait(false);
        if (headerRead == 0)
            return null;
        if (headerRead < HeaderLength)
            throw new IOException("帧头不完整即结束");

        var length = BinaryPrimitives.ReadInt32BigEndian(header);
        if (length < 0 || length > maxFrameBytes)
            throw new IOException($"非法帧长度: {length}");
        if (length == 0)
            return [];

        var payload = new byte[length];
        var payloadRead = await ReadAtLeastAsync(stream, payload, allowEofAtStart: false, cancellationToken).ConfigureAwait(false);
        if (payloadRead < length)
            throw new IOException("帧载荷不完整即结束");
        return payload;
    }

    private static async ValueTask<int> ReadAtLeastAsync(
        Stream stream,
        Memory<byte> buffer,
        bool allowEofAtStart,
        CancellationToken cancellationToken)
    {
        var total = 0;
        while (total < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer[total..], cancellationToken).ConfigureAwait(false);
            if (read == 0)
                return total == 0 && allowEofAtStart ? 0 : total;
            total += read;
        }

        return total;
    }
}
