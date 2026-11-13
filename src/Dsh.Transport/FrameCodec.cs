using System.Buffers;
using System.Buffers.Binary;
using ZstdSharp;

namespace Dsh.Transport;

/**
 * 长度帧: 4 字节大端长度前缀 + 载荷(JSON-RPC 报文按 UTF-8 字节写入)。
 * 读写都基于 Stream; 读侧把粘包/拆包收敛在 ReadFrameAsync 内(不足则持续读)。
 * 这是中立层: ACP 与远程工作区都复用它, 彼此不依赖。
 *
 * 长度前缀的最高位是压缩标志: 会话事件是高度重复的 JSON, 原样传输要几十倍于落盘体积,
 * 跨设备链路(手机经隧道/公网)上的瓶颈因此不是带宽而是数据量。置位表示载荷是 zstd 帧。
 * 该位对 1 GiB 以内的帧永远是 0, 所以旧对端读新帧仍走原路径, 只有置位时才需要解压。
 */
public static class FrameCodec
{
    /** 长度前缀宽度(字节, 大端)。 */
    public const int HeaderLength = 4;

    /** 单帧默认上限: 防畸形长度前缀触发一次超大分配(工具输出应分片到该值以内)。 */
    public const int DefaultMaxFrameBytes = 16 * 1024 * 1024;

    /** 长度头里表示"载荷已压缩"的最高位。 */
    private const int CompressedFlag = 1 << 31;

    /** 小于这个长度的载荷压缩下来往往更大(帧头开销吃掉了收益), 直接原样发。 */
    private const int CompressionThreshold = 1024;

    public static async ValueTask WriteFrameAsync(
        Stream stream,
        ReadOnlyMemory<byte> payload,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (payload.Length > DefaultMaxFrameBytes)
            throw new ArgumentOutOfRangeException(nameof(payload), payload.Length, "帧载荷超过上限");
        if (payload.Length >= CompressionThreshold)
        {
            var compressed = Compress(payload.Span);
            // 压不小就发原样: 标志位只在真的变小时才置, 避免为不可压内容白付解压成本。
            if (compressed.Length < payload.Length)
            {
                await WriteRawAsync(stream, compressed, isCompressed: true, cancellationToken).ConfigureAwait(false);
                return;
            }
        }
        await WriteRawAsync(stream, payload, isCompressed: false, cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask WriteRawAsync(
        Stream stream,
        ReadOnlyMemory<byte> payload,
        bool isCompressed,
        CancellationToken cancellationToken)
    {
        var frameLength = HeaderLength + payload.Length;
        var frame = ArrayPool<byte>.Shared.Rent(frameLength);
        try
        {
            // One write per frame: a separate header write left the payload behind Nagle until the peer's delayed ACK (~40 ms per frame on loopback TCP).
            BinaryPrimitives.WriteInt32BigEndian(frame, isCompressed ? payload.Length | CompressedFlag : payload.Length);
            payload.CopyTo(frame.AsMemory(HeaderLength));
            await stream.WriteAsync(frame.AsMemory(0, frameLength), cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(frame);
        }
    }

    private static byte[] Compress(ReadOnlySpan<byte> payload)
    {
        using var compressor = new Compressor();
        return compressor.Wrap(payload).ToArray();
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

        var raw = BinaryPrimitives.ReadInt32BigEndian(header);
        var compressed = (raw & CompressedFlag) != 0;
        var length = raw & ~CompressedFlag;
        if (length < 0 || length > maxFrameBytes)
            throw new IOException($"非法帧长度: {length}");
        if (length == 0)
            return [];

        var payload = new byte[length];
        var payloadRead = await ReadAtLeastAsync(stream, payload, allowEofAtStart: false, cancellationToken).ConfigureAwait(false);
        if (payloadRead < length)
            throw new IOException("帧载荷不完整即结束");
        return compressed ? Decompress(payload, maxFrameBytes) : payload;
    }

    private static byte[] Decompress(byte[] payload, int maxFrameBytes)
    {
        try
        {
            using var decompressor = new Decompressor();
            var inflated = decompressor.Unwrap(payload).ToArray();
            // 长度前缀由对端给出: 解压后仍要按同一上限校验, 否则一个恶意小帧能撑爆内存。
            if (inflated.Length > maxFrameBytes)
                throw new IOException($"解压后帧过大: {inflated.Length}");
            return inflated;
        }
        catch (ZstdSharp.ZstdException error)
        {
            throw new IOException("压缩帧解压失败", error);
        }
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
