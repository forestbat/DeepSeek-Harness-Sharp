using System.Buffers.Binary;
using System.Text;
using Dsh.IdeHistory;

namespace Dsh.Tests;

public sealed class IdeHistoryTests
{
    [Fact]
    public void VSCode_ReadsSyntheticStore()
    {
        var home = CreateTempDirectory("vscode");
        try
        {
            var userDir = Path.Combine(home, ".config", "Code", "User");
            var historyDir = Path.Combine(userDir, "History", "abc123");
            Directory.CreateDirectory(historyDir);
            File.WriteAllText(Path.Combine(historyDir, "v1.txt"), "first version");
            File.WriteAllText(Path.Combine(historyDir, "v2.txt"), "second version");
            File.WriteAllText(Path.Combine(historyDir, "entries.json"), """
                {
                  "version": 1,
                  "resource": "file:///home/dev/project/app.py",
                  "entries": [
                    { "id": "v2.txt", "timestamp": 1780000002000, "source": "User" },
                    { "id": "v1.txt", "timestamp": 1780000001000, "source": "User" }
                  ]
                }
                """);
            File.WriteAllText(Path.Combine(userDir, "settings.json"), """
                { "workbench.localHistory.maxFileEntries": 7 }
                """);

            var provider = new VsCodeLocalHistoryProvider(home);
            var store = Assert.Single(provider.Discover());
            Assert.Contains("maxFileEntries=7", store.Retention);

            var entries = provider.List(store, "app.py", 10);
            Assert.Equal(2, entries.Count);
            Assert.Equal(1780000002000, entries[0].Timestamp);
            Assert.Equal("/home/dev/project/app.py", entries[0].Path);

            var content = provider.Read(store, entries[0]);
            Assert.NotNull(content);
            Assert.Equal("second version", content.Text);
        }
        finally
        {
            DeleteQuietly(home);
        }
    }

    [Fact]
    public void VSCode_FiltersByPath()
    {
        var home = CreateTempDirectory("vscode");
        try
        {
            var historyDir = Path.Combine(home, ".config", "Code", "User", "History", "abc123");
            Directory.CreateDirectory(historyDir);
            File.WriteAllText(Path.Combine(historyDir, "v1.txt"), "x");
            File.WriteAllText(Path.Combine(historyDir, "entries.json"), """
                {
                  "version": 1,
                  "resource": "file:///home/dev/other.py",
                  "entries": [ { "id": "v1.txt", "timestamp": 1780000001000 } ]
                }
                """);
            var provider = new VsCodeLocalHistoryProvider(home);
            var store = Assert.Single(provider.Discover());
            Assert.Empty(provider.List(store, "app.py", 10));
            Assert.Single(provider.List(store, "other.py", 10));
        }
        finally
        {
            DeleteQuietly(home);
        }
    }

    [Fact]
    public void JetBrains_DecodesSyntheticStore()
    {
        var directory = CreateTempDirectory("jetbrains");
        try
        {
            var localHistory = Path.Combine(directory, "IntelliJIdea2026.1", "LocalHistory");
            Directory.CreateDirectory(localHistory);
            var dataPath = Path.Combine(localHistory, "changes.storageData");
            var indexPath = Path.Combine(localHistory, "changes.storageRecordIndex");
            var caches = Path.Combine(directory, "IntelliJIdea2026.1", "caches");
            Directory.CreateDirectory(caches);
            var contentId = SyntheticContentStore.Write(Path.Combine(caches, "content.dat"), "old app.py body"u8.ToArray());
            var changes = new List<SyntheticChange>
            {
                new("content", "/home/dev/project/app.py", (int)contentId),
                new("delete", "/home/dev/project/old.py", null),
                new("delete-tree", "/home/dev/project/removed-dir", null),
                new("label", "里程碑", null),
                new("system-label", "自动保存", null),
            };
            var payload = SyntheticChangeSet.Build(7, 1_780_000_000_000, "外部更改", changes);
            var data = new byte[0x20 + payload.Length];
            payload.CopyTo(data, 0x20);
            File.WriteAllBytes(dataPath, data);

            var index = new byte[32 + 2 * 32];
            BinaryPrimitives.WriteInt32BigEndian(index.AsSpan(0), 0x1f2f3f58);
            BinaryPrimitives.WriteInt32BigEndian(index.AsSpan(4), 7);
            BinaryPrimitives.WriteInt64BigEndian(index.AsSpan(8), 1);
            BinaryPrimitives.WriteInt64BigEndian(index.AsSpan(64), 0x20);
            BinaryPrimitives.WriteInt32BigEndian(index.AsSpan(72), payload.Length);
            BinaryPrimitives.WriteInt32BigEndian(index.AsSpan(76), payload.Length);
            BinaryPrimitives.WriteInt64BigEndian(index.AsSpan(88), 1_780_000_000_000);
            File.WriteAllBytes(indexPath, index);

            var provider = new JetBrainsLocalHistoryProvider(directory);
            var store = Assert.Single(provider.Discover());
            var entries = provider.List(store, null, 10);
            Assert.Equal(3, entries.Count);
            Assert.Equal(0, provider.SkippedRecords);
            Assert.DoesNotContain(entries, entry => entry.Kind is "label" or "system-label");
            Assert.Contains(entries, entry => entry.Kind == "content" && entry.Path == "/home/dev/project/app.py" && entry.ContentId == contentId.ToString());
            Assert.Contains(entries, entry => entry.Kind == "delete" && entry.Path == "/home/dev/project/old.py");
            Assert.Contains(entries, entry => entry.Kind == "delete" && entry.Path == "/home/dev/project/removed-dir");
            Assert.All(entries, entry => Assert.Equal(1_780_000_000_000, entry.Timestamp));
            Assert.True(provider.SupportsContent);
            var contentEntry = Assert.Single(entries, entry => entry.Kind == "content");
            var content = provider.Read(store, contentEntry);
            Assert.NotNull(content);
            Assert.Equal("old app.py body", content.Text);
            var deleted = Assert.Single(entries, entry => entry.Kind == "delete" && entry.Path == "/home/dev/project/old.py");
            Assert.Null(provider.Read(store, deleted));
            Assert.NotNull(provider.LastReadError);
        }
        finally
        {
            DeleteQuietly(directory);
        }
    }

    [Fact]
    public void Registry_MergesProvidersAndPrefersContentCapable()
    {
        var home = CreateTempDirectory("registry");
        try
        {
            var historyDir = Path.Combine(home, ".config", "Code", "User", "History", "abc123");
            Directory.CreateDirectory(historyDir);
            File.WriteAllText(Path.Combine(historyDir, "v1.txt"), "from vscode");
            File.WriteAllText(Path.Combine(historyDir, "entries.json"), """
                {
                  "version": 1,
                  "resource": "file:///home/dev/project/app.py",
                  "entries": [ { "id": "v1.txt", "timestamp": 1780000001000 } ]
                }
                """);
            var registry = IdeHistoryRegistry.CreateDefault(home, Path.Combine(home, "nocache"));
            var query = registry.Query("app.py", null, 10);
            var item = Assert.Single(query);
            Assert.Equal("vscode", item.Provider.Name);
            var found = registry.Find("/home/dev/project/app.py", 1780000001000, null);
            Assert.NotNull(found);
            Assert.NotNull(found.Value.Provider.Read(found.Value.Store, found.Value.Entry));
        }
        finally
        {
            DeleteQuietly(home);
        }
    }

    [Fact]
    public void JetBrainsContentStore_ReadsRawAndCompressedRecords()
    {
        var directory = CreateTempDirectory("content");
        try
        {
            var path = Path.Combine(directory, "content.dat");
            var raw = "small content"u8.ToArray();
            var large = Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat("compressible line\n", 400)));
            var (store, ids) = SyntheticContentStore.Create(path, raw, large);
            using (store)
            {
                var first = store.TryReadContent(ids[0], out var error);
                Assert.Null(error);
                Assert.Equal(raw, first);
                var second = store.TryReadContent(ids[1], out error);
                Assert.Null(error);
                Assert.Equal(large, second);
                Assert.Null(store.TryReadContent(ids[1] + 1000, out var missing));
                Assert.NotNull(missing);
            }
            var opened = new JetBrainsContentStore(path);
            Assert.Equal(raw, opened.TryReadContent(ids[0], out _));
            Assert.Equal(large, opened.TryReadContent(ids[1], out _));
            opened.Dispose();
        }
        finally
        {
            DeleteQuietly(directory);
        }
    }

    [Fact]
    public void JetBrains_ReadsLocalStoreWhenPresent()
    {
        var provider = new JetBrainsLocalHistoryProvider();
        var stores = provider.Discover();
        if (stores.Count == 0)
            return;
        var samples = stores
            .SelectMany(store => provider.List(store, null, 200).Select(entry => (Store: store, Entry: entry)))
            .ToList();
        if (samples.Count == 0)
            return;
        Assert.All(samples, sample => Assert.StartsWith("/", sample.Entry.Path));
        Assert.All(samples, sample => Assert.True(sample.Entry.Timestamp > 1_400_000_000_000));
        Assert.True(provider.SkippedRecords * 4 < samples.Count,
            $"too many undecodable records: {provider.SkippedRecords} of {samples.Count}");

        var withContent = samples
            .Where(sample => sample.Entry.Kind == "content" && sample.Entry.ContentId is { Length: > 0 })
            .GroupBy(sample => sample.Entry.ContentId)
            .Take(60)
            .Select(group => group.First())
            .ToList();
        if (withContent.Count == 0)
            return;
        var recovered = withContent.Count(sample => provider.Read(sample.Store, sample.Entry) is not null);
        Assert.True(recovered > 0, $"failed to recover content for all {withContent.Count} sampled entries; last error: {provider.LastReadError}");
        var textual = withContent
            .Select(sample => provider.Read(sample.Store, sample.Entry)?.Bytes)
            .Count(bytes => bytes is { Length: > 0 } && !bytes.Contains((byte)0));
        Assert.True(textual > 0, "no readable text content recovered from the local JetBrains store");
    }

    private static class SyntheticContentStore
    {
        private const int FrameHeader = JetBrainsContentStore.RecordHeaderSize;

        public static long Write(string path, byte[] content)
        {
            var (store, ids) = Create(path, content);
            store.Dispose();
            return ids[0];
        }

        public static (JetBrainsContentStore Store, long[] Ids) Create(string path, params byte[][] contents)
        {
            var offsets = new int[contents.Length];
            var offset = JetBrainsContentStore.FileHeaderSize;
            for (var index = 0; index < contents.Length; index++)
            {
                offsets[index] = offset;
                var payload = Encode(contents[index], out _);
                var size = FrameHeader + payload.Length;
                offset += size;
                if (offset % 4 != 0)
                    offset += 4 - offset % 4;
            }
            var bytes = new byte[offset];
            var ids = new long[contents.Length];
            for (var index = 0; index < contents.Length; index++)
            {
                var payload = Encode(contents[index], out var compressed);
                var size = FrameHeader + payload.Length;
                BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offsets[index]), (uint)(JetBrainsContentStore.ValidRecordFlag | size));
                BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(offsets[index] + 24), compressed ? -contents[index].Length : contents[index].Length);
                payload.CopyTo(bytes.AsSpan(offsets[index] + FrameHeader));
                ids[index] = (offsets[index] - JetBrainsContentStore.FileHeaderSize) / 4 + 1;
            }
            File.WriteAllBytes(path, bytes);
            return (new JetBrainsContentStore(path), ids);
        }

        private static byte[] Encode(byte[] content, out bool compressed)
        {
            var buffer = new byte[K4os.Compression.LZ4.LZ4Codec.MaximumOutputSize(content.Length)];
            var length = K4os.Compression.LZ4.LZ4Codec.Encode(content.AsSpan(), buffer.AsSpan());
            compressed = content.Length > 512 && length > 0 && length < content.Length;
            return compressed ? buffer[..length] : content;
        }
    }

    private sealed record SyntheticChange(string Kind, string Path, int? ContentId);

    private static class SyntheticChangeSet
    {
        private const long TimeBase = 33L * 365L * 24L * 3600L * 1000L;

        public static byte[] Build(long id, long timestamp, string name, IReadOnlyList<SyntheticChange> changes)
        {
            var buffer = new List<byte>();
            VarInt(buffer, 1);
            VarLong(buffer, id);
            buffer.Add(1);
            Utf(buffer, name);
            Time(buffer, timestamp);
            buffer.Add(0);
            buffer.Add(0);
            VarInt(buffer, changes.Count);
            foreach (var change in changes)
            {
                VarInt(buffer, change.Kind switch
                {
                    "create-file" => 1,
                    "create-dir" => 2,
                    "content" => 3,
                    "rename" => 4,
                    "ro-status" => 5,
                    "move" => 6,
                    "delete" => 7,
                    "delete-tree" => 7,
                    "label" => 8,
                    "system-label" => 9,
                    _ => throw new InvalidOperationException(),
                });
                VarLong(buffer, 1000 + buffer.Count);
                // 标签变更无路径: name + projectId(+ zigzag color)。
                if (change.Kind is "label" or "system-label")
                {
                    Utf(buffer, change.Path);
                    Utf(buffer, "project");
                    if (change.Kind == "system-label")
                        SignedVarInt(buffer, 3);
                    continue;
                }
                Utf(buffer, change.Path);
                switch (change.Kind)
                {
                    case "content":
                        VarInt(buffer, change.ContentId ?? 0);
                        Time(buffer, timestamp - 1000);
                        break;
                    case "delete":
                        // 与 DataStreamUtil.writeEntry 一致: 类型(0=文件)在前, 条目名随后。
                        VarInt(buffer, 0);
                        Utf(buffer, "old.py");
                        WriteLong(buffer, timestamp - 2000);
                        buffer.Add(0);
                        VarInt(buffer, 0);
                        break;
                    case "delete-tree":
                        // 目录条目(1) + 新格式条目名(<FILE_ID_AND_HASH> + nameId/nameHash 两个大端 int32) + 一个文件子条目。
                        VarInt(buffer, 1);
                        Utf(buffer, "<FILE_ID_AND_HASH>");
                        WriteInt32(buffer, 123456);
                        WriteInt32(buffer, -7654321);
                        VarInt(buffer, 1);
                        VarInt(buffer, 0);
                        Utf(buffer, "inner.py");
                        WriteLong(buffer, timestamp - 3000);
                        buffer.Add(0);
                        VarInt(buffer, 0);
                        break;
                }
            }
            return [.. buffer];
        }

        private static void VarInt(List<byte> buffer, long value)
        {
            if (value >= 192)
            {
                buffer.Add((byte)(192 + (value & 0x3F)));
                value >>= 6;
                while (value >= 128)
                {
                    buffer.Add((byte)((value & 0x7F) | 0x80));
                    value >>= 7;
                }
            }
            buffer.Add((byte)value);
        }

        private static void VarLong(List<byte> buffer, long value) => VarInt(buffer, value);

        private static void SignedVarInt(List<byte> buffer, long value) => VarInt(buffer, (value << 1) ^ (value >> 63));

        private static void Utf(List<byte> buffer, string text)
        {
            var bytes = Encoding.UTF8.GetBytes(text);
            if (bytes.Length < 255 && bytes.All(b => b < 128))
            {
                buffer.Add((byte)bytes.Length);
                buffer.AddRange(bytes);
                return;
            }
            buffer.Add(0xFF);
            buffer.Add((byte)(bytes.Length >> 8));
            buffer.Add((byte)(bytes.Length & 0xFF));
            buffer.AddRange(bytes);
        }

        private static void Time(List<byte> buffer, long timestamp)
        {
            var relative = timestamp - TimeBase;
            if (relative < 0 || relative >= 0xFF00000000L)
            {
                buffer.Add(0xFF);
                WriteLong(buffer, timestamp);
                return;
            }
            buffer.Add((byte)(relative >> 32));
            buffer.Add((byte)(relative >> 24));
            buffer.Add((byte)(relative >> 16));
            buffer.Add((byte)(relative >> 8));
            buffer.Add((byte)relative);
        }

        private static void WriteLong(List<byte> buffer, long value)
        {
            for (var shift = 56; shift >= 0; shift -= 8)
                buffer.Add((byte)(value >> shift));
        }

        private static void WriteInt32(List<byte> buffer, int value)
        {
            for (var shift = 24; shift >= 0; shift -= 8)
                buffer.Add((byte)(value >> shift));
        }
    }

    private static string CreateTempDirectory(string prefix)
    {
        var directory = Path.Combine(Path.GetTempPath(), $"dsh-idehistory-{prefix}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static void DeleteQuietly(string directory)
    {
        try
        {
            Directory.Delete(directory, true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
