using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace Dsh.IdeHistory;

public sealed class JetBrainsLocalHistoryProvider : IIdeHistoryProvider
{
    private const int IndexHeaderSize = 32;
    private const int IndexRecordSize = 32;
    private const string StorageName = "changes";
    private const string DataSuffix = ".storageData";
    private const string IndexSuffix = ".storageRecordIndex";

    private readonly string? _root;

    public JetBrainsLocalHistoryProvider(string? cacheRoot = null)
    {
        _root = cacheRoot;
    }

    public string Name => "jetbrains";

    public bool SupportsContent => true;

    public int SkippedRecords { get; private set; }

    /** 解码失败的原因样本(至多 8 条,含记录 id):SkippedRecords 异常升高时用于定位不兼容的记录形态。 */
    public IReadOnlyList<string> SkipErrors => _skipErrors;

    private readonly List<string> _skipErrors = [];

    public string? LastReadError { get; private set; }

    public IReadOnlyList<IdeHistoryStoreInfo> Discover()
    {
        var root = _root
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cache", "JetBrains");
        if (!Directory.Exists(root))
            return [];
        var stores = new List<IdeHistoryStoreInfo>();
        foreach (var ideDir in Directory.EnumerateDirectories(root))
        {
            foreach (var candidate in CandidateStores(ideDir))
            {
                if (!File.Exists(candidate))
                    continue;
                stores.Add(new IdeHistoryStoreInfo
                {
                    Provider = Name,
                    Location = candidate,
                    Retention = $"localHistory.daysToKeep (IDE 注册表, 默认 5 个工作日) [{Path.GetFileName(ideDir)}]",
                });
            }
        }
        return stores;
    }

    private static IEnumerable<string> CandidateStores(string ideDir)
    {
        yield return Path.Combine(ideDir, "LocalHistory", StorageName + DataSuffix);
        yield return Path.Combine(ideDir, "LocalHistory", "changes", StorageName + DataSuffix);
    }

    public IReadOnlyList<IdeHistoryEntry> List(IdeHistoryStoreInfo store, string? pathFilter, int limit)
    {
        SkippedRecords = 0;
        _skipErrors.Clear();
        var dataPath = store.Location;
        var indexPath = dataPath[..^DataSuffix.Length] + IndexSuffix;
        if (!File.Exists(dataPath) || !File.Exists(indexPath))
            return [];
        var entries = new List<IdeHistoryEntry>();
        foreach (var record in Decode(dataPath, indexPath))
        {
            foreach (var change in record.Changes)
            {
                if (pathFilter is { Length: > 0 } && !change.Path.Contains(pathFilter, StringComparison.OrdinalIgnoreCase))
                    continue;
                entries.Add(new IdeHistoryEntry
                {
                    Provider = Name,
                    Path = change.Path,
                    Timestamp = record.Timestamp,
                    Kind = change.Kind,
                    ContentId = change.ContentId,
                });
            }
        }
        return [.. entries.OrderByDescending(entry => entry.Timestamp).Take(limit)];
    }

    public IdeHistoryContent? Read(IdeHistoryStoreInfo store, IdeHistoryEntry entry)
    {
        LastReadError = null;
        if (entry.ContentId is not { Length: > 0 } idText || !long.TryParse(idText, out var contentId))
        {
            LastReadError = "该历史条目没有关联内容（创建/删除类变更）";
            return null;
        }
        var path = JetBrainsContentStore.LocateForLocalHistoryData(store.Location);
        if (path is null)
        {
            LastReadError = "找不到 content.dat（IDE 内容存储）";
            return null;
        }
        using var contentStore = new JetBrainsContentStore(path);
        var bytes = contentStore.TryReadContent(contentId, out var error);
        if (bytes is null)
        {
            LastReadError = error;
            return null;
        }
        return new IdeHistoryContent
        {
            Bytes = bytes,
            Origin = $"jetbrains:{path}#{idText}",
        };
    }

    private IEnumerable<DecodedChange> Decode(string dataPath, string indexPath)
    {
        var index = File.ReadAllBytes(indexPath);
        if (index.Length < IndexHeaderSize)
            yield break;
        var lastId = (int)Math.Min(BinaryPrimitives.ReadInt64BigEndian(index.AsSpan(8)), (index.Length - IndexHeaderSize) / (long)IndexRecordSize);
        var data = File.ReadAllBytes(dataPath);
        for (var id = 1; id <= lastId; id++)
        {
            var offset = IndexHeaderSize + id * IndexRecordSize;
            if (offset + IndexRecordSize > index.Length)
                break;
            var address = BinaryPrimitives.ReadInt64BigEndian(index.AsSpan(offset));
            var size = BinaryPrimitives.ReadInt32BigEndian(index.AsSpan(offset + 8));
            if (size <= 0 || address <= 0 || address + size > data.Length)
                continue;
            DecodedChange? decoded = null;
            try
            {
                decoded = ChangeSetDecoder.Decode(data.AsSpan((int)address, size));
            }
            catch (Exception error)
            {
                SkippedRecords++;
                if (_skipErrors.Count < 8)
                    _skipErrors.Add($"id={id} size={size}: {error.Message}");
            }
            if (decoded is not null)
                yield return decoded;
        }
    }
}

internal sealed record DecodedChange(long Timestamp, IReadOnlyList<DecodedChangeItem> Changes);

public sealed record DecodedChangeItem(string Kind, string Path, string? ContentId);

internal static class ChangeSetDecoder
{
    private const long TimeBase = 33L * 365L * 24L * 3600L * 1000L;

    public static DecodedChange Decode(ReadOnlySpan<byte> bytes)
    {
        var reader = new SpanReader(bytes);
        var version = reader.ReadVarInt();
        reader.ReadVarLong();
        reader.ReadStringOrNull();
        var timestamp = reader.ReadTime();
        if (version >= 1)
        {
            reader.ReadStringOrNull();
            reader.ReadStringOrNull();
        }
        var count = reader.ReadVarInt();
        var items = new List<DecodedChangeItem>((int)count);
        for (var index = 0; index < count; index++)
        {
            if (ReadChange(ref reader) is { } item)
                items.Add(item);
        }
        return new DecodedChange(timestamp, items);
    }

    private static DecodedChangeItem? ReadChange(ref SpanReader reader)
    {
        var type = reader.ReadVarInt();
        reader.ReadVarLong();
        // 标签变更没有路径: 结构是 name + projectId(+ color), 不产生文件历史条目, 只推进游标。
        if (type == 8)
        {
            reader.ReadString();
            reader.ReadString();
            return null;
        }
        if (type == 9)
        {
            reader.ReadString();
            reader.ReadString();
            reader.ReadSignedVarInt();
            return null;
        }
        var path = Normalize(reader.ReadString());
        switch (type)
        {
            case 1:
                return new DecodedChangeItem("create-file", path, null);
            case 2:
                return new DecodedChangeItem("create-dir", path, null);
            case 3:
                var contentId = reader.ReadVarInt();
                reader.ReadTime();
                return new DecodedChangeItem("content", path, contentId > 0 ? contentId.ToString(CultureInfo.InvariantCulture) : null);
            case 4:
                var oldName = reader.ReadString();
                return new DecodedChangeItem("rename", path, oldName.Length > 0 ? oldName : null);
            case 5:
                reader.ReadBool();
                return new DecodedChangeItem("ro-status", path, null);
            case 6:
                var oldPath = Normalize(reader.ReadString());
                return new DecodedChangeItem("move", path, oldPath);
            case 7:
                ReadEntry(ref reader);
                return new DecodedChangeItem("delete", path, null);
            default:
                throw new InvalidOperationException($"unexpected change type: {type}");
        }
    }

    /**
     * 删除条目的序列化顺序是类型在前、条目体在后(DataStreamUtil.writeEntry)。
     * 条目名兼容两种形态: 旧格式直接写名字字符串; 注册表项 lvcs.store.entry.file.id 开启后写 FILE_ID_MAGIC + 两个大端 int32(nameId/nameHash)。
     */
    private static void ReadEntry(ref SpanReader reader)
    {
        var type = reader.ReadVarInt();
        ReadEntryName(ref reader);
        if (type == 0)
        {
            reader.ReadLong();
            reader.ReadBool();
            reader.ReadVarInt();
            return;
        }
        if (type == 1)
        {
            var count = reader.ReadVarInt();
            for (var index = 0; index < count; index++)
                ReadEntry(ref reader);
            return;
        }
        throw new InvalidOperationException($"unexpected entry type: {type}");
    }

    private const string FileIdMagic = "<FILE_ID_AND_HASH>";

    private static void ReadEntryName(ref SpanReader reader)
    {
        if (reader.ReadString() != FileIdMagic)
            return;
        reader.ReadInt32BigEndian();
        reader.ReadInt32BigEndian();
    }

    private static string Normalize(string path)
        => path.Length > 0 && path[0] != '/' && !path.Contains(':') ? "/" + path : path;

    private ref struct SpanReader(ReadOnlySpan<byte> bytes)
    {
        private readonly ReadOnlySpan<byte> _bytes = bytes;
        private int _position;

        private byte Next()
        {
            if (_position >= _bytes.Length)
                throw new EndOfStreamException();
            return _bytes[_position++];
        }

        public long ReadVarInt()
        {
            var value = Next();
            if (value < 192)
                return value;
            long result = value - 192;
            var shift = 6;
            while (true)
            {
                var next = Next();
                result |= (long)(next & 0x7F) << shift;
                if ((next & 0x80) == 0)
                    return result;
                shift += 7;
                if (shift > 63)
                    throw new InvalidDataException("varint overflow");
            }
        }

        public long ReadVarLong() => ReadVarInt();

        /** 有符号 varint(zigzag): PutSystemLabelChange 的 color 字段用这个编码。 */
        public long ReadSignedVarInt()
        {
            var raw = ReadVarInt();
            return (raw >>> 1) ^ -(raw & 1);
        }

        public int ReadInt32BigEndian()
        {
            if (_position + 4 > _bytes.Length)
                throw new EndOfStreamException();
            var value = BinaryPrimitives.ReadInt32BigEndian(_bytes[_position..]);
            _position += 4;
            return value;
        }

        public long ReadLong()
        {
            if (_position + 8 > _bytes.Length)
                throw new EndOfStreamException();
            var value = BinaryPrimitives.ReadInt64BigEndian(_bytes[_position..]);
            _position += 8;
            return value;
        }

        public bool ReadBool() => Next() != 0;

        public long ReadTime()
        {
            var first = Next();
            if (first == 0xFF)
                return ReadLong();
            var second = Next();
            var third = Next() << 16;
            var fourth = Next() << 8;
            var fifth = Next();
            return (((long)((first << 8) | second) << 24) | (uint)(third | fourth | fifth)) + TimeBase;
        }

        public string ReadStringOrNull() => ReadBool() ? ReadString() : "";

        public string ReadString()
        {
            var length = Next();
            if (length == 0xFF)
            {
                if (_position + 2 > _bytes.Length)
                    throw new EndOfStreamException();
                var utfLength = BinaryPrimitives.ReadUInt16BigEndian(_bytes[_position..]);
                _position += 2;
                if (_position + utfLength > _bytes.Length)
                    throw new EndOfStreamException();
                var text = Encoding.UTF8.GetString(_bytes.Slice(_position, utfLength));
                _position += utfLength;
                return text;
            }
            if (length == 0)
                return "";
            if (_position + length > _bytes.Length)
                throw new EndOfStreamException();
            var value = Encoding.Latin1.GetString(_bytes.Slice(_position, length));
            _position += length;
            return value;
        }
    }
}
