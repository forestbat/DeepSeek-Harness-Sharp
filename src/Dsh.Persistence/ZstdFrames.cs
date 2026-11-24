using System.Buffers;
using System.Buffers.Binary;
using System.IO.Compression;

namespace Dsh.Persistence;

public static class ZstdFrames
{
    private const uint Magic = 0xFD2FB528;
    private const int MagicBytes = 4;
    private const int StreamBufferSize = 1 << 16;

    public readonly record struct FrameRange(int Start, int End);

    /**
     * 扫描结果。
     * Frames: 能完整解析的帧; Damaged: 无法解析、但后面还有合法帧的区段(原样保留, 不删);
     * TornStart: 文件末尾写了一半的最后一帧起点(断电或被杀时的半帧)。
     */
    public readonly record struct ScanResult(List<FrameRange> Frames, List<FrameRange> Damaged, int? TornStart);

    public static ScanResult Scan(ReadOnlySpan<byte> buffer, int maxFrames = int.MaxValue)
    {
        var frames = new List<FrameRange>();
        var damaged = new List<FrameRange>();
        var offset = 0;
        while (offset < buffer.Length)
        {
            var start = offset;
            if (buffer.Length - offset < MagicBytes) return new ScanResult(frames, damaged, start);
            if (BinaryPrimitives.ReadUInt32LittleEndian(buffer[offset..]) != Magic)
            {
                // 不是帧头: 后面还有合法帧就把它当坏区段跳过, 否则当成写了一半的尾巴。
                if (NextMagic(buffer, start + 1) is not { } next) return new ScanResult(frames, damaged, start);
                damaged.Add(new FrameRange(start, next));
                offset = next;
                continue;
            }
            if (!TryReadFrame(buffer, start, out var end))
            {
                if (NextMagic(buffer, start + 1) is not { } next) return new ScanResult(frames, damaged, start);
                damaged.Add(new FrameRange(start, next));
                offset = next;
                continue;
            }
            frames.Add(new FrameRange(start, end));
            offset = end;
            if (frames.Count == maxFrames) return new ScanResult(frames, damaged, null);
        }
        return new ScanResult(frames, damaged, null);
    }

    /** 只按帧头/块头推进算出这一帧的长度; 字节不够(半帧)或帧头不可信时返回 false。 */
    public static bool TryReadFrameLength(ReadOnlySpan<byte> buffer, out int length)
    {
        if (TryReadFrame(buffer, 0, out var end))
        {
            length = end;
            return true;
        }
        length = 0;
        return false;
    }

    /** offset 处是否落着一个帧头: 4 字节魔数即可判定, 用来核对索引里的帧边界是否还贴合日志。 */
    public static bool StartsWithFrame(Stream stream, long offset)
    {
        Span<byte> magic = stackalloc byte[MagicBytes];
        stream.Position = offset;
        return stream.ReadAtLeast(magic, MagicBytes, throwOnEndOfStream: false) == MagicBytes
            && BinaryPrimitives.ReadUInt32LittleEndian(magic) == Magic;
    }

    /** 从 from 起找下一个帧魔数; 找不到返回 null。 */
    private static int? NextMagic(ReadOnlySpan<byte> buffer, int from)
    {
        for (var index = Math.Max(0, from); index + MagicBytes <= buffer.Length; index += 1)
            if (BinaryPrimitives.ReadUInt32LittleEndian(buffer[index..]) == Magic)
                return index;
        return null;
    }

    private const int ScanBufferSize = 1 << 20;
    private const int MaxScanFrameBytes = 64 << 20;

    /**
     * 只按帧头切帧、不解压地扫一份日志: 返回每帧起点, 末项等于文件长度。
     *
     * 老日志没有 `.frames` 索引, 而建索引要整份解压(1M 事件约 18 秒)。
     * 切帧只需要帧头与块头, 383 MB 的日志因此从"解压 18 秒"降到"读一遍文件"。
     * 结构不连贯(坏区段、半帧、超大帧)返回 null: 那种日志交给全量解析, 由它去修。
     */
    public static long[]? ScanFrameBoundaries(Stream stream) => ScanFrameBoundaries(stream, 0);

    /** 从 from 起切帧(调用方保证 from 是帧边界); 返回的首项就是 from。 */
    public static long[]? ScanFrameBoundaries(Stream stream, long from)
    {
        var length = stream.Length;
        if (length <= from) return null;
        var buffer = new byte[ScanBufferSize];
        var starts = new List<long> { from };
        // buffer[0] 对应文件偏移 bufferStart, cursor 是待解析帧在缓冲内的位置。
        // 切帧只在缓冲内移动下标, 缓冲用尽才前移补齐: 每个字节只从磁盘读一次。
        // 现场日志的帧可以小到几百字节(上百万帧), 若每帧搬一次缓冲, 扫描会退化成 O(帧数 × 缓冲)。
        var bufferStart = from;
        var filled = 0;
        var cursor = 0;
        while (bufferStart + cursor < length)
        {
            if (filled < buffer.Length)
                filled += ReadAt(stream, bufferStart + filled, buffer, filled);
            if (TryReadFrame(buffer.AsSpan(cursor, filled - cursor), 0, out var frameLength))
            {
                cursor += frameLength;
                starts.Add(bufferStart + cursor);
                continue;
            }
            // 解析不了: 末尾半帧交给全量解析, 否则是缓冲里不够一帧。
            if (bufferStart + filled >= length)
                return null;
            if (filled < buffer.Length)
                return null;
            if (cursor > 0)
            {
                var remaining = filled - cursor;
                Buffer.BlockCopy(buffer, cursor, buffer, 0, remaining);
                bufferStart += cursor;
                filled = remaining;
                cursor = 0;
                continue;
            }
            if (buffer.Length >= MaxScanFrameBytes)
                return null;
            Array.Resize(ref buffer, buffer.Length * 2);
        }
        return bufferStart + cursor == length ? [.. starts] : null;
    }

    private static int ReadAt(Stream stream, long offset, byte[] buffer, int start)
    {
        stream.Position = offset;
        var total = 0;
        while (start + total < buffer.Length)
        {
            var read = stream.Read(buffer, start + total, buffer.Length - start - total);
            if (read == 0)
                break;
            total += read;
        }
        return total;
    }

    /** 帧头解析结果: 帧尾与帧头里声明的解压后字节数(没声明时为 null)。 */
    private readonly record struct FrameHeader(int End, long? ContentSize);

    /**
     * 每帧解压后的字节数, 取自帧头声明的 content size。
     * 有一帧没声明就返回 null: 调用方据此决定"能不能把整段解压结果按帧切开"。
     */
    public static long[]? PlaintextSizes(ReadOnlySpan<byte> buffer, IReadOnlyList<FrameRange> frames)
    {
        var sizes = new long[frames.Count];
        for (var slot = 0; slot < frames.Count; slot += 1)
        {
            if (!TryReadFrameHeader(buffer, frames[slot].Start, out var header) || header.ContentSize is not { } size)
                return null;
            sizes[slot] = size;
        }
        return sizes;
    }

    /**
     * 只读帧头里声明的解压后字节数: 传帧的前 20 字节就够, 不必把整帧读进来。
     *
     * 逐帧重切日志时要用它——"这一帧多大"决定要不要重切, 为此把整帧(甚至整份日志)读进内存是纯浪费。
     * 返回 false 表示帧头没写长度, 或者传进来的字节不够解析帧头。
     */
    public static bool TryReadPlaintextSize(ReadOnlySpan<byte> framePrefix, out long size)
    {
        size = 0;
        var offset = MagicBytes;
        if (offset >= framePrefix.Length) return false;
        var descriptor = framePrefix[offset];
        offset += 1;
        if ((descriptor & 0x18) != 0) return false;
        var contentSizeFlag = descriptor >> 6;
        var singleSegment = (descriptor & 0x20) != 0;
        var dictionaryFlag = descriptor & 0x03;
        var dictionaryBytes = dictionaryFlag == 3 ? 4 : dictionaryFlag;
        var contentSizeBytes = contentSizeFlag == 0 ? (singleSegment ? 1 : 0) : 1 << contentSizeFlag;
        var windowBytes = singleSegment ? 0 : 1;
        if (contentSizeBytes == 0) return false;
        if (framePrefix.Length - offset < windowBytes + dictionaryBytes + contentSizeBytes) return false;
        if (ReadContentSize(framePrefix, offset + windowBytes + dictionaryBytes, contentSizeFlag, contentSizeBytes) is not { } declared)
            return false;
        size = declared;
        return true;
    }

    /**
     * 只按帧头/块头推进算出帧尾, 不解压。返回 false 表示"字节不够(半帧)"或"帧头已不可信"。
     * 调用方据此决定这是坏区段还是写了一半的尾巴。
     */
    private static bool TryReadFrame(ReadOnlySpan<byte> buffer, int start, out int end)
    {
        if (TryReadFrameHeader(buffer, start, out var header))
        {
            end = header.End;
            return true;
        }
        end = start;
        return false;
    }

    private static bool TryReadFrameHeader(ReadOnlySpan<byte> buffer, int start, out FrameHeader header)
    {
        header = default;
        var offset = start + MagicBytes;
        if (offset >= buffer.Length) return false;
        var descriptor = buffer[offset];
        offset += 1;
        if ((descriptor & 0x18) != 0) return false;
        var contentSizeFlag = descriptor >> 6;
        var singleSegment = (descriptor & 0x20) != 0;
        var checksum = (descriptor & 0x04) != 0;
        var dictionaryFlag = descriptor & 0x03;
        var dictionaryBytes = dictionaryFlag == 3 ? 4 : dictionaryFlag;
        var contentSizeBytes = contentSizeFlag == 0 ? (singleSegment ? 1 : 0) : 1 << contentSizeFlag;
        var windowBytes = singleSegment ? 0 : 1;
        var remainingHeaderBytes = windowBytes + dictionaryBytes + contentSizeBytes;
        if (buffer.Length - offset < remainingHeaderBytes) return false;
        // 顺序是 Descriptor → [Window_Descriptor] → [Dictionary_ID] → [Frame_Content_Size]:
        // 多段帧(single segment 关掉)在字典与长度之前多一个窗口描述字节, 少跳它就会把窗口字节当成长度,
        // 于是"每帧明文多少"读出一个荒谬的数(改动前只在 DecompressInto 里靠长度对不上退回扩容路径, 一直没被发现)。
        long? contentSize = ReadContentSize(buffer, offset + windowBytes + dictionaryBytes, contentSizeFlag,
            contentSizeBytes);
        offset += remainingHeaderBytes;
        while (true)
        {
            if (buffer.Length - offset < 3) return false;
            var blockHeader = buffer[offset] | (buffer[offset + 1] << 8) | (buffer[offset + 2] << 16);
            offset += 3;
            var lastBlock = (blockHeader & 1) != 0;
            var blockType = (blockHeader >> 1) & 0x03;
            var blockSize = blockHeader >> 3;
            if (blockType == 0x03) return false;
            var payloadBytes = blockType == 0x01 ? 1 : blockSize;
            if (buffer.Length - offset < payloadBytes) return false;
            offset += payloadBytes;
            if (lastBlock) break;
        }
        if (checksum)
        {
            if (buffer.Length - offset < 4) return false;
            offset += 4;
        }
        header = new FrameHeader(offset, contentSize);
        return true;
    }

    /** content size 字段: 1/2/4/8 字节小端; 2 字节那种带 256 偏移, 0 字节表示"帧头没写"。 */
    private static long? ReadContentSize(ReadOnlySpan<byte> buffer, int offset, int contentSizeFlag, int contentSizeBytes)
    {
        if (contentSizeBytes == 0)
            return null;
        var size = contentSizeFlag == 1 ? 256L : 0L;
        for (var index = 0; index < contentSizeBytes; index += 1)
            size += (long)buffer[offset + index] << (8 * index);
        return size;
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
        => DecompressFrame(frame.ToArray(), 0, frame.Length, frameStart);

    /**
     * 直接从一块缓冲里的一段解压: 不复制输入字节。
     * 全量读一份 383 MB 的日志时, 旧实现的 bytes.ToArray() 会为了给 MemoryStream 喂输入再多占一份 383 MB。
     */
    public static byte[] DecompressFrame(byte[] source, int offset, int length, int frameStart)
    {
        try
        {
            return DecompressBytes(source, offset, length, Math.Max(1024, length * 4));
        }
        catch (Exception error)
        {
            throw new FormatException($"corrupt Zstandard session log: frame at byte {frameStart} failed validation", error);
        }
    }

    /**
     * 连续解压多帧。
     *
     * 会话日志每落盘一批就多一帧(200 条/100 毫秒预算), 十万级事件的会话因此只有几百帧;
     * 拼接的多帧可以一次读完, 不必逐帧新建解压上下文。
     * 只喂完整帧: 末尾写了一半的帧由 Scan 排除在 frames 之外, 一并喂给流式解压会因截断数据抛错。
     *
     * plaintextSizes: 每帧解压后的字节数(取自帧头)。给了就直接按总和开一块目标数组把明文解压进去;
     * 否则退回 MemoryStream 边解边扩容(长度不可知时才需要那份翻倍扩容与多一次 ToArray)。
     */
    public static byte[] DecompressFrames(
        IReadOnlyList<FrameRange> frames, byte[] buffer, int skip = 0, long[]? plaintextSizes = null)
    {
        if (skip >= frames.Count)
            return [];
        var from = frames[skip].Start;
        var to = frames[^1].End;
        try
        {
            if (PlaintextLength(plaintextSizes, skip, frames.Count) is { } total && total <= int.MaxValue)
                return DecompressInto(buffer, from, to - from, (int)total);
            return DecompressBytes(buffer, from, to - from, Math.Max(16, buffer.Length));
        }
        catch (Exception error)
        {
            throw new FormatException($"corrupt Zstandard session log: frame at byte {from} failed validation", error);
        }
    }

    /** 这些帧解压后的总字节数; 有一帧没写长度就返回 null(长度不可知, 不能按它开数组)。 */
    private static long? PlaintextLength(long[]? plaintextSizes, int skip, int count)
    {
        if (plaintextSizes is null || plaintextSizes.Length < count)
            return null;
        long total = 0;
        for (var slot = skip; slot < count; slot += 1)
            total += plaintextSizes[slot];
        return total;
    }
            /**
     * 逐帧解压, 单帧解不出来就跳过并报告。
     * 拼接帧一次解整段最快, 但里面只要有一帧坏了, 整段就整体失败 —— 那会让"一条会话坏一帧就再也打不开"。
     */
    public static (byte[] Chunks, List<FrameRange> Failed) DecompressFramesResilient(
        IReadOnlyList<FrameRange> frames, byte[] buffer, int skip = 0, long[]? plaintextSizes = null)
    {
        try
        {
            return (DecompressFrames(frames, buffer, skip, plaintextSizes), []);
        }
        catch (FormatException)
        {
            // 落回逐帧: 目标是把可读的帧全取出来, 而不是把整份日志判死。
        }
        var failed = new List<FrameRange>();
        using var output = new MemoryStream(Math.Max(16, buffer.Length));
        for (var index = skip; index < frames.Count; index += 1)
        {
            var frame = frames[index];
            try
            {
                var decoded = DecompressFrame(buffer, frame.Start, frame.End - frame.Start, frame.Start);
                output.Write(decoded, 0, decoded.Length);
            }
            catch (FormatException)
            {
                failed.Add(frame);
            }
        }
        return (output.ToArray(), failed);
    }

    public static byte[] DecompressPrefix(byte[] tornBytes)
    {
        using var output = new MemoryStream();
        var buffer = ArrayPool<byte>.Shared.Rent(StreamBufferSize);
        try
        {
            using var input = new MemoryStream(tornBytes, writable: false);
            using var stream = new ZstandardStream(input, CompressionMode.Decompress);
            int read;
            while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
                output.Write(buffer, 0, read);
        }
        catch (InvalidDataException)
        {
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
        return output.ToArray();
    }

    /** 按帧头声明的总长度开一块目标数组, 边解压边填: 明文只落一份。 */
    private static byte[] DecompressInto(byte[] source, int offset, int length, int total)
    {
        var plain = new byte[total];
        using var input = new MemoryStream(source, offset, length, writable: false);
        using var stream = new ZstandardStream(input, CompressionMode.Decompress);
        var read = 0;
        while (read < plain.Length)
        {
            var chunk = stream.Read(plain, read, plain.Length - read);
            if (chunk == 0)
                break;
            read += chunk;
        }
        // 帧头声明的长度与实际明文不符(外部写入方把 content size 写错): 老老实实退回长度不可知的路径重解一遍。
        // 少一次分配不值得换来"按长度把事件截掉"。
        if (read != plain.Length)
            return DecompressBytes(source, offset, length, Math.Max(16, total));
        Span<byte> probe = stackalloc byte[1];
        return stream.Read(probe) > 0 ? DecompressBytes(source, offset, length, Math.Max(16, total)) : plain;
    }

    /** 长度不可知时退回边解边扩容: 输入仍不复制, 明文只多一次 ToArray。 */
    private static byte[] DecompressBytes(byte[] source, int offset, int length, int capacity)
    {
        using var input = new MemoryStream(source, offset, length, writable: false);
        using var stream = new ZstandardStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream(capacity);
        stream.CopyTo(output);
        return output.ToArray();
    }
}
