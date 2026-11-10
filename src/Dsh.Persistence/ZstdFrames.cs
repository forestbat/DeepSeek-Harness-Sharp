using System.Buffers.Binary;
using ZstdSharp;
using ZstdSharp.Unsafe;

namespace Dsh.Persistence;

public static class ZstdFrames
{
    private const uint Magic = 0xFD2FB528;
    private const int StreamBufferSize = 1 << 16;

    public readonly record struct FrameRange(int Start, int End);

    public static (List<FrameRange> Frames, int? TornStart) Scan(ReadOnlySpan<byte> buffer, int maxFrames = int.MaxValue)
    {
        var frames = new List<FrameRange>();
        var offset = 0;
        while (offset < buffer.Length)
        {
            var start = offset;
            if (buffer.Length - offset < 4) return (frames, start);
            if (BinaryPrimitives.ReadUInt32LittleEndian(buffer[offset..]) != Magic)
                throw new FormatException($"corrupt Zstandard session log: invalid frame magic at byte {offset}");
            offset += 4;
            if (offset == buffer.Length) return (frames, start);
            var descriptor = buffer[offset];
            offset += 1;
            if ((descriptor & 0x18) != 0)
                throw new FormatException($"corrupt Zstandard session log: reserved frame-header bit at byte {offset - 1}");
            var contentSizeFlag = descriptor >> 6;
            var singleSegment = (descriptor & 0x20) != 0;
            var checksum = (descriptor & 0x04) != 0;
            var dictionaryFlag = descriptor & 0x03;
            var dictionaryBytes = dictionaryFlag == 3 ? 4 : dictionaryFlag;
            var contentSizeBytes = contentSizeFlag == 0 ? (singleSegment ? 1 : 0) : 1 << contentSizeFlag;
            var remainingHeaderBytes = (singleSegment ? 0 : 1) + dictionaryBytes + contentSizeBytes;
            if (buffer.Length - offset < remainingHeaderBytes) return (frames, start);
            offset += remainingHeaderBytes;
            while (true)
            {
                if (buffer.Length - offset < 3) return (frames, start);
                var blockHeader = buffer[offset] | (buffer[offset + 1] << 8) | (buffer[offset + 2] << 16);
                offset += 3;
                var lastBlock = (blockHeader & 1) != 0;
                var blockType = (blockHeader >> 1) & 0x03;
                var blockSize = blockHeader >> 3;
                if (blockType == 0x03)
                    throw new FormatException($"corrupt Zstandard session log: reserved block type at byte {offset - 3}");
                var payloadBytes = blockType == 0x01 ? 1 : blockSize;
                if (buffer.Length - offset < payloadBytes) return (frames, start);
                offset += payloadBytes;
                if (lastBlock) break;
            }
            if (checksum)
            {
                if (buffer.Length - offset < 4) return (frames, start);
                offset += 4;
            }
            frames.Add(new FrameRange(start, offset));
            if (frames.Count == maxFrames) return (frames, null);
        }
        return (frames, null);
    }

    public static byte[] CompressFrame(ReadOnlySpan<byte> input)
    {
        using var compressor = new Compressor();
        compressor.SetParameter(ZSTD_cParameter.ZSTD_c_checksumFlag, 1);
        return compressor.Wrap(input).ToArray();
    }

    public static byte[] DecompressFrame(ReadOnlySpan<byte> frame, int frameStart)
    {
        try
        {
            using var decompressor = new Decompressor();
            return decompressor.Unwrap(frame).ToArray();
        }
        catch (Exception error)
        {
            throw new FormatException($"corrupt Zstandard session log: frame at byte {frameStart} failed validation", error);
        }
    }

    /**
     * 连续解压多帧时复用同一个 Decompressor。
     *
     * 会话日志每落盘一批就多一帧(256 条/1 秒), 十万级事件的会话有上百帧;
     * 每帧 new 一个 Decompressor 等于每帧重新分配一次解压上下文, 而它本可以继续用。
     * 输出逐帧追加到同一块缓冲, 不再为每帧分配一个新数组。
     *
     * 这里用 TryUnwrap 的 Span 重载(库没有 IBufferWriter 重载), 因此需要自己按帧扩缩目标缓冲:
     * 先按上一帧的大小给一个估计值, 放不下就按 zstd 报的所需容量重试。
     */
    public static byte[] DecompressFrames(IReadOnlyList<FrameRange> frames, byte[] buffer, int skip = 0)
    {
        using var output = new MemoryStream(Math.Max(16, buffer.Length));
        using var decompressor = new Decompressor();
        var scratch = new byte[Math.Max(4096, buffer.Length)];
        for (var index = skip; index < frames.Count; index += 1)
        {
            var (start, end) = (frames[index].Start, frames[index].End);
            var frame = new ReadOnlySpan<byte>(buffer, start, end - start);
            int written;
            while (!decompressor.TryUnwrap(frame, scratch, out written))
            {
                // 目标太小不是"帧损坏": 按需扩一倍再试, 只有连续失败到上限才算真损坏。
                if (scratch.Length >= MaxFrameBytes)
                    throw new FormatException($"corrupt Zstandard session log: frame at byte {start} failed validation");
                scratch = new byte[Math.Min(MaxFrameBytes, scratch.Length * 2)];
            }
            output.Write(scratch, 0, written);
        }
        return output.ToArray();
    }

    /** 单帧解压后的上限: 一帧来自一次落盘批次(256 条事件), 远超这个量说明不是数据而是爆了。 */
    private const int MaxFrameBytes = 256 * 1024 * 1024;

    public static byte[] DecompressPrefix(byte[] tornBytes)
    {
        using var output = new MemoryStream();
        try
        {
            using var input = new MemoryStream(tornBytes, writable: false);
            using var stream = new DecompressionStream(input, StreamBufferSize, checkEndOfStream: false, leaveOpen: false);
            var buffer = new byte[StreamBufferSize];
            int read;
            while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
                output.Write(buffer, 0, read);
        }
        catch (ZstdException)
        {
        }
        return output.ToArray();
    }
}
