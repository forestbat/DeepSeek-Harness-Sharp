using System.Buffers.Binary;
using K4os.Compression.LZ4;
using Microsoft.Win32.SafeHandles;

namespace Dsh.Ide.History;

public sealed class JetBrainsContentStore : IDisposable
{
    public const int FileHeaderSize = 64;
    public const int RecordHeaderSize = 28;
    public const int HashSize = 20;
    public const uint ValidRecordFlag = 0x40000000;
    public const int RecordSizeMask = 0x3FFFFFFF;

    private readonly SafeFileHandle _handle;

    public JetBrainsContentStore(string path)
    {
        Path = System.IO.Path.GetFullPath(path);
        _handle = File.OpenHandle(Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        Length = RandomAccess.GetLength(_handle);
    }

    public string Path { get; }

    public long Length { get; }

    public static long ContentIdToOffset(long contentId) => (contentId - 1) * 4 + FileHeaderSize;

    public byte[]? TryReadContent(long contentId, out string? error)
    {
        error = null;
        if (contentId <= 0)
        {
            error = "content id is unavailable";
            return null;
        }
        var offset = ContentIdToOffset(contentId);
        if (offset < FileHeaderSize || offset + RecordHeaderSize > Length)
        {
            error = $"content id {contentId} points outside the store";
            return null;
        }
        Span<byte> header = stackalloc byte[RecordHeaderSize];
        if (RandomAccess.Read(_handle, header, offset) != RecordHeaderSize)
        {
            error = "content record header is truncated";
            return null;
        }
        var sizeField = BinaryPrimitives.ReadUInt32LittleEndian(header);
        if ((sizeField & ValidRecordFlag) == 0)
        {
            error = "content record is no longer valid";
            return null;
        }
        var size = (int)(sizeField & RecordSizeMask);
        if (size < RecordHeaderSize || offset + size > Length)
        {
            error = "content record has an invalid size";
            return null;
        }
        var length = BinaryPrimitives.ReadInt32LittleEndian(header[24..]);
        if (length >= 0)
        {
            if (length > size - RecordHeaderSize)
            {
                error = "content record length exceeds its size";
                return null;
            }
            var content = new byte[length];
            ReadExactly(content, offset + RecordHeaderSize);
            return content;
        }
        var expected = -length;
        var compressed = new byte[size - RecordHeaderSize];
        ReadExactly(compressed, offset + RecordHeaderSize);
        var target = new byte[expected];
        var written = LZ4Codec.Decode(compressed, target);
        if (written != expected)
        {
            error = $"LZ4 block decoded to {written} bytes instead of {expected}";
            return null;
        }
        return target;
    }

    public static string? LocateForLocalHistoryData(string localHistoryDataPath)
    {
        var directory = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(localHistoryDataPath));
        if (directory is null)
            return null;
        if (string.Equals(System.IO.Path.GetFileName(directory), "changes", StringComparison.OrdinalIgnoreCase))
            directory = System.IO.Path.GetDirectoryName(directory);
        var localHistory = directory is null ? null : System.IO.Path.GetDirectoryName(directory);
        if (localHistory is null)
            return null;
        var path = System.IO.Path.Combine(localHistory, "caches", "content.dat");
        return File.Exists(path) ? path : null;
    }

    private void ReadExactly(byte[] buffer, long offset)
    {
        var read = 0;
        while (read < buffer.Length)
        {
            var chunk = RandomAccess.Read(_handle, buffer.AsSpan(read), offset + read);
            if (chunk == 0)
                throw new IOException($"unexpected end of content store at offset {offset + read}");
            read += chunk;
        }
    }

    public void Dispose() => _handle.Dispose();
}
