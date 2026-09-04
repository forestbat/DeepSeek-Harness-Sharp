using System.Buffers.Binary;
using ZstdSharp;
using ZstdSharp.Unsafe;

namespace Dsh.Persistence;

public static class ZstdFrames
{
    private const uint Magic = 0xFD2FB528;
    private const int DefaultCompressionLevel = 3;
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
        using var compressor = new Compressor(DefaultCompressionLevel);
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
