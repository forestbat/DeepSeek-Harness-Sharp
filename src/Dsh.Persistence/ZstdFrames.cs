using System.Buffers.Binary;
using System.IO.Compression;

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
        using var encoder = new ZstandardEncoder(new ZstandardCompressionOptions { AppendChecksum = true });
        // 声明源长度: 帧头因此带上 content size, Scan 才能按同一套规则切帧。
        encoder.SetSourceLength(input.Length);
        using var output = new MemoryStream(Math.Max(64, input.Length / 2));
        using (var stream = new ZstandardStream(output, encoder, leaveOpen: true))
            stream.Write(input);
        return output.ToArray();
    }

    public static byte[] DecompressFrame(ReadOnlySpan<byte> frame, int frameStart)
    {
        try
        {
            return DecompressBytes(frame, Math.Max(1024, frame.Length * 4));
        }
        catch (Exception error)
        {
            throw new FormatException($"corrupt Zstandard session log: frame at byte {frameStart} failed validation", error);
        }
    }

    /**
     * 连续解压多帧。
     *
     * 会话日志每落盘一批就多一帧(256 条/1 秒), 十万级事件的会话有上百帧;
     * 拼接的多帧可以一次读完, 不必逐帧新建解压上下文。
     * 只喂完整帧: 末尾写了一半的帧由 Scan 排除在 frames 之外, 一并喂给流式解压会因截断数据抛错。
     */
    public static byte[] DecompressFrames(IReadOnlyList<FrameRange> frames, byte[] buffer, int skip = 0)
    {
        using var output = new MemoryStream(Math.Max(16, buffer.Length));
        if (skip < frames.Count)
        {
            var from = frames[skip].Start;
            var to = frames[^1].End;
            try
            {
                var decoded = DecompressBytes(new ReadOnlySpan<byte>(buffer, from, to - from), buffer.Length);
                output.Write(decoded, 0, decoded.Length);
            }
            catch (Exception error)
            {
                throw new FormatException($"corrupt Zstandard session log: frame at byte {from} failed validation", error);
            }
        }
        return output.ToArray();
    }

    public static byte[] DecompressPrefix(byte[] tornBytes)
    {
        using var output = new MemoryStream();
        try
        {
            using var input = new MemoryStream(tornBytes, writable: false);
            using var stream = new ZstandardStream(input, CompressionMode.Decompress);
            var buffer = new byte[StreamBufferSize];
            int read;
            while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
                output.Write(buffer, 0, read);
        }
        catch (InvalidDataException)
        {
        }
        return output.ToArray();
    }

    /** 从一段连续字节读出解压结果: 多帧拼接可一次读完, 半截输入会抛 InvalidDataException。 */
    private static byte[] DecompressBytes(ReadOnlySpan<byte> bytes, int capacity)
    {
        using var input = new MemoryStream(bytes.ToArray(), writable: false);
        using var stream = new ZstandardStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream(capacity);
        stream.CopyTo(output);
        return output.ToArray();
    }
}