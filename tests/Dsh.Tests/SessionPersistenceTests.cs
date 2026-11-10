using System.Text;
using System.Text.Json;
using Dsh.Core;
using Dsh.Llm;
using Dsh.Persistence;

namespace Dsh.Tests;

public class SessionPersistenceTests : IDisposable
{
    private const string TestCwd = "/tmp/dsh-persist-proj";
    private readonly List<string> _roots = [];

    private string NewRoot()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"dsh-persist-{Guid.NewGuid():N}");
        _roots.Add(dir);
        return dir;
    }

    public void Dispose()
    {
        foreach (var root in _roots)
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    private static Session BuildSourceSession(string cwd)
    {
        var id = SessionId.Create(Guid.NewGuid().ToString("N"));
        var session = Session.Create(id, header: new SessionHeader
        {
            Version = SessionHeader.SessionFormatVersion,
            Id = id,
            CreatedAt = 1700000000000,
            Cwd = cwd,
            IsSeeded = false,
        });
        session.Append(new TurnStartPayload(1));
        session.Append(new StepStartPayload(1, 1));
        for (var index = 0; index < 5; index += 1)
            session.Append(new AssistantChunkPayload(1, 1, new StreamChunk.TextDelta(0, $"tok{index}")));
        var message = new AssistantMessagePayload(1, 1, MessageFactory.CreateAssistantMessage([new TextBlock("done")], "deepseek", "deepseek-chat"));
        session.Append(message, new SurfaceOp.Append(), [2, 3, 4, 6]);
        session.Append(new TurnEndPayload(1, new TurnEndReason.Completed()));
        return session;
    }

    private static void AssertEventsEqual(IReadOnlyList<SessionEvent> expected, IReadOnlyList<SessionEvent> actual)
    {
        Assert.Equal(expected.Count, actual.Count);
        foreach (var (left, right) in expected.Zip(actual))
            Assert.Equal(JsonSerializer.Serialize(left, DshJson.Options), JsonSerializer.Serialize(right, DshJson.Options));
    }

    private static string LogPathOf(string root, SessionHeader header, JsonlCompression compression)
        => JsonlLayout.LogPath(root, header.Cwd, header.Id, compression);

    [Theory]
    [InlineData(JsonlCompression.None)]
    [InlineData(JsonlCompression.Zstd)]
    public void RoundTrip_PackedChunksAndProvenance(JsonlCompression compression)
    {
        var root = NewRoot();
        using var persistence = new JsonlSessionPersistence(root, compression: compression);
        var session = BuildSourceSession(TestCwd);
        var events = session.SnapshotEvents();

        var handle = persistence.Create(session.Header);
        var logPath = LogPathOf(root, session.Header, compression);
        Assert.False(File.Exists(logPath));

        handle.Append(events.Take(2).ToArray());
        Assert.True(File.Exists(logPath));
        handle.Append(events.Skip(2).ToArray());
        handle.Flush();
        handle.Close();

        var snapshot = persistence.Stat(session.Header.Id);
        Assert.NotNull(snapshot);
        Assert.Equal(session.Header.Id, snapshot.Header.Id);
        Assert.NotNull(snapshot.SizeBytes);

        var reader = persistence.Open(session.Header.Id, SessionAccess.Read);
        AssertEventsEqual(events, reader.Read().ToArray());
        AssertEventsEqual(events.Skip(2).Take(3).ToArray(), reader.Read(2, 3).ToArray());
        Assert.Empty(reader.Read(events.Count));
        reader.Close();
    }

    [Theory]
    [InlineData(JsonlCompression.None)]
    [InlineData(JsonlCompression.Zstd)]
    public void RoundTrip_UnpackedKeepsOneEventPerLine(JsonlCompression compression)
    {
        var root = NewRoot();
        using var persistence = new JsonlSessionPersistence(root, packChunks: false, compression: compression);
        var session = BuildSourceSession(TestCwd);
        var events = session.SnapshotEvents();
        var handle = persistence.Create(session.Header);
        handle.Append(events);
        handle.Close();
        var reader = persistence.Open(session.Header.Id, SessionAccess.Read);
        AssertEventsEqual(events, reader.Read().ToArray());
    }

    [Fact]
    public void TornTail_PlainText_TruncatedOnNextAppend()
    {
        var root = NewRoot();
        using var persistence = new JsonlSessionPersistence(root, compression: JsonlCompression.None);
        var session = BuildSourceSession(TestCwd);
        var events = session.SnapshotEvents();
        var handle = persistence.Create(session.Header);
        handle.Append(events);
        handle.Close();

        var logPath = LogPathOf(root, session.Header, JsonlCompression.None);
        using (var stream = new FileStream(logPath, FileMode.Append, FileAccess.Write))
        {
            var torn = Encoding.UTF8.GetBytes("{\"type\":\"turn/end\",\"seq\":99");
            stream.Write(torn);
        }

        var reader = persistence.Open(session.Header.Id, SessionAccess.Read);
        AssertEventsEqual(events, reader.Read().ToArray());
        reader.Close();

        var extra = new SessionEvent
        {
            Type = SessionEventTypes.SessionEndSeed,
            Seq = events.Count,
            Time = 1700000001000,
            Data = new SessionEndSeedPayload(),
        };
        var writer = persistence.Open(session.Header.Id, SessionAccess.Write);
        writer.Append([extra]);
        writer.Close();

        var reopened = persistence.Open(session.Header.Id, SessionAccess.Read);
        AssertEventsEqual([.. events, extra], reopened.Read().ToArray());
    }

    [Fact]
    public void TornTail_Zstd_TruncatedOnNextAppend()
    {
        var root = NewRoot();
        using var persistence = new JsonlSessionPersistence(root, compression: JsonlCompression.Zstd);
        var session = BuildSourceSession(TestCwd);
        var events = session.SnapshotEvents();
        var handle = persistence.Create(session.Header);
        handle.Append(events);
        handle.Close();

        var logPath = LogPathOf(root, session.Header, JsonlCompression.Zstd);
        var tornFrame = ZstdFrames.CompressFrame(Encoding.UTF8.GetBytes("{\"type\":\"session/end-seed\",\"seq\":99,\"time\":1700000002000,\"data\":{}}\n"));
        using (var stream = new FileStream(logPath, FileMode.Append, FileAccess.Write))
        {
            stream.Write(tornFrame.AsSpan(0, tornFrame.Length / 2));
        }

        var reader = persistence.Open(session.Header.Id, SessionAccess.Read);
        AssertEventsEqual(events, reader.Read().ToArray());
        reader.Close();

        var extra = new SessionEvent
        {
            Type = SessionEventTypes.SessionEndSeed,
            Seq = events.Count,
            Time = 1700000001000,
            Data = new SessionEndSeedPayload(),
        };
        var writer = persistence.Open(session.Header.Id, SessionAccess.Write);
        writer.Append([extra]);
        writer.Close();

        var reopened = persistence.Open(session.Header.Id, SessionAccess.Read);
        AssertEventsEqual([.. events, extra], reopened.Read().ToArray());
    }

    /**
     * 复用同一个解压器连续解多帧, 结果必须与逐帧各 new 一个完全一致。
     * 这条守的是 W3 的捷径: 复用 Decompressor 只为省掉每帧的上下文重建, 不允许改变任何一个字节。
     */
    [Fact]
    public void DecompressFrames_MatchesPerFrameDecompression()
    {
        var payloads = Enumerable.Range(0, 8)
            .Select(index => Encoding.UTF8.GetBytes($"frame-{index}-中文-" + new string((char)('a' + index), 500)))
            .ToArray();
        var buffer = payloads.SelectMany(payload => ZstdFrames.CompressFrame(payload)).ToArray();
        var (frames, tornStart) = ZstdFrames.Scan(buffer);

        Assert.Equal(payloads.Length, frames.Count);
        Assert.Null(tornStart);
        var reused = ZstdFrames.DecompressFrames(frames, buffer);
        var perFrame = ZstdFrames.Scan(buffer).Frames
            .Select(frame => ZstdFrames.DecompressFrame(buffer.AsSpan(frame.Start, frame.End - frame.Start), frame.Start))
            .SelectMany(bytes => bytes)
            .ToArray();

        Assert.Equal(perFrame, reused);
        Assert.Equal(payloads.SelectMany(bytes => bytes).ToArray(), reused);
    }

    /** 定时落盘会在读取期间从另一线程追加, 读取必须放行写入(Windows 上 File.ReadAllBytes 只共享读, 会和追加流冲突)。 */
    private static int FrameCount(string logPath)
    {
        using var stream = new FileStream(logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return ZstdFrames.Scan(buffer.ToArray()).Frames.Count;
    }

    /** 流式 chunk 先缓冲, 遇到非 chunk 事件才连同之前的 chunk 写成一个帧; 最后一批写坏时之前的批次完好。 */
    [Fact]
    public void StreamChunks_AreBatchedUntilBoundaryEvent()
    {
        var root = NewRoot();
        using var persistence = new JsonlSessionPersistence(root, writeBatch: new JsonlWriteBatch(MaxEvents: 1000, Interval: TimeSpan.FromHours(1)));
        var session = BuildSourceSession(TestCwd);
        var events = session.SnapshotEvents();
        var logPath = LogPathOf(root, session.Header, JsonlCompression.Zstd);
        var handle = persistence.Create(session.Header);

        handle.Append(events.Take(2).ToArray());
        var framesAfterBoundary = FrameCount(logPath);
        foreach (var chunk in events.Skip(2).Take(5))
            handle.Append([chunk]);
        Assert.Equal(framesAfterBoundary, FrameCount(logPath));

        handle.Append([events[7]]);
        Assert.Equal(framesAfterBoundary + 1, FrameCount(logPath));

        var bytes = File.ReadAllBytes(logPath);
        var lastFrame = ZstdFrames.Scan(bytes).Frames[^1];
        File.WriteAllBytes(logPath, bytes[..(lastFrame.Start + (lastFrame.End - lastFrame.Start) / 2)]);
        var reader = persistence.Open(session.Header.Id, SessionAccess.Read);
        var recovered = reader.Read();
        Assert.True(recovered.Count >= 2);
        AssertEventsEqual(events.Take(recovered.Count).ToArray(), recovered.ToArray());
        reader.Close();
        handle.Close();
    }

    /** 缓冲的 chunk 在数量上限、定时器到点、Flush 与 Close 时都会落盘。 */
    [Fact]
    public async Task StreamChunks_DrainOnLimitTimerFlushAndClose()
    {
        var root = NewRoot();
        var session = BuildSourceSession(TestCwd);
        var events = session.SnapshotEvents();
        var chunks = events.Skip(2).Take(5).ToArray();
        var logPath = LogPathOf(root, session.Header, JsonlCompression.Zstd);
        using (var limited = new JsonlSessionPersistence(root, writeBatch: new JsonlWriteBatch(MaxEvents: 3, Interval: TimeSpan.FromHours(1))))
        {
            var handle = limited.Create(session.Header);
            handle.Append(events.Take(2).ToArray());
            var baseline = FrameCount(logPath);
            handle.Append(chunks[..3]);
            Assert.Equal(baseline + 1, FrameCount(logPath));
            handle.Append(chunks[3..4]);
            handle.Flush();
            Assert.Equal(baseline + 2, FrameCount(logPath));
            handle.Append(chunks[4..]);
            handle.Close();
            Assert.Equal(baseline + 3, FrameCount(logPath));
        }

        var timedRoot = NewRoot();
        var timedLog = LogPathOf(timedRoot, session.Header, JsonlCompression.Zstd);
        using var timed = new JsonlSessionPersistence(timedRoot, writeBatch: new JsonlWriteBatch(MaxEvents: 1000, Interval: TimeSpan.FromMilliseconds(20)));
        var timedHandle = timed.Create(session.Header);
        timedHandle.Append(events.Take(2).ToArray());
        var timedBaseline = FrameCount(timedLog);
        timedHandle.Append(chunks);
        for (var attempt = 0; attempt < 200 && FrameCount(timedLog) == timedBaseline; attempt++)
            await Task.Delay(10, TestContext.Current.CancellationToken);
        Assert.Equal(timedBaseline + 1, FrameCount(timedLog));
        timedHandle.Close();
    }

    /** 会话活着时日志可能正被追加流持有(追加流只共享读), 这时读取会话不能失败。 */
    [Fact]
    public void Read_WhileAnAppendStreamHoldsTheLog_Succeeds()
    {
        var root = NewRoot();
        using var persistence = new JsonlSessionPersistence(root, compression: JsonlCompression.Zstd);
        var session = BuildSourceSession(TestCwd);
        var events = session.SnapshotEvents();
        var handle = persistence.Create(session.Header);
        handle.Append(events);
        handle.Close();

        using (new FileStream(LogPathOf(root, session.Header, JsonlCompression.Zstd), FileMode.Append, FileAccess.Write, FileShare.Read))
        {
            var reader = persistence.Open(session.Header.Id, SessionAccess.Read);
            AssertEventsEqual(events, reader.Read().ToArray());
            reader.Close();
        }
    }

    [Fact]
    public void Open_ForeignFormatVersion_ThrowsFormatUnsupported()
    {
        var root = NewRoot();
        var id = SessionId.Create(Guid.NewGuid().ToString("N"));
        var path = JsonlLayout.LogPath(root, TestCwd, id, JsonlCompression.None);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, $"{{\"type\":\"session\",\"version\":1,\"id\":\"{id.Value}\",\"createdAt\":1,\"delegationDepth\":0}}\n");

        using var persistence = new JsonlSessionPersistence(root, compression: JsonlCompression.None);
        Assert.Throws<SessionFormatUnsupportedException>(() => persistence.Open(id, SessionAccess.Read));
        Assert.Equal(SessionErrorCodes.FormatUnsupported,
            Assert.Throws<SessionFormatUnsupportedException>(() => persistence.Open(id, SessionAccess.Read)).Code);
    }

    [Theory]
    [InlineData(".", "~002E")]
    [InlineData("..", "~002E~002E")]
    [InlineData("plain.Name_1-x", "plain.Name_1-x")]
    [InlineData("a/b", "a~002Fb")]
    [InlineData("a\\b", "a~005Cb")]
    [InlineData("~", "~007E")]
    [InlineData("a b", "a~0020b")]
    [InlineData("中", "~4E2D")]
    [InlineData("../escape", "..~002Fescape")]
    public void EncodeSegment_EscapesUnsafeCodeUnits(string raw, string expected)
        => Assert.Equal(expected, JsonlLayout.EncodeSegment(raw));

    [Fact]
    public void EncodeSegment_Empty_Throws()
        => Assert.Throws<ArgumentException>(() => JsonlLayout.EncodeSegment(""));

    [Theory]
    [InlineData("/home/user/proj", "--home-user-proj--")]
    [InlineData("/", "--root--")]
    [InlineData("C:\\src\\app", "--C-src-app--")]
    [InlineData("/a b/c", "--a~0020b-c--")]
    [InlineData("/tmp/", "--tmp---")]
    public void ProjectStorageKey_EncodesProjectPaths(string cwd, string expected)
        => Assert.Equal(expected, ProjectStorageKey.Of(cwd));

    [Fact]
    public void ProjectStorageKey_BoundedOf_StaysShortDeterministicAndDistinct()
    {
        var deep = "/" + string.Join('/', Enumerable.Repeat("segment", 40));
        var key = ProjectStorageKey.BoundedOf(deep);
        Assert.True(
            key.Length <= ProjectStorageKey.BoundedSlugLength + 2 + 1 + 16 + 2,
            $"键过长: {key.Length} => {key}");
        Assert.Equal(key, ProjectStorageKey.BoundedOf(deep));
        Assert.NotEqual(key, ProjectStorageKey.BoundedOf(deep + "/x"));
    }

    [Fact]
    public void ProjectStorageKey_TruncatesLongSlugs()
    {
        var key = ProjectStorageKey.Of("/" + new string('a', 300));
        Assert.Equal($"--{new string('a', ProjectStorageKey.MaxSlugLength)}--", key);
    }

    [Fact]
    public void Append_NonContiguousBatch_ThrowsCorruption()
    {
        var root = NewRoot();
        using var persistence = new JsonlSessionPersistence(root, compression: JsonlCompression.None);
        var session = BuildSourceSession(TestCwd);
        var events = session.SnapshotEvents();
        var handle = persistence.Create(session.Header);
        handle.Append(events.Take(2).ToArray());
        var error = Assert.Throws<SessionPersistenceCorruptionException>(() => handle.Append(events.Skip(5).ToArray()));
        Assert.Equal(SessionErrorCodes.Corruption, error.Code);
    }

    [Fact]
    public void Create_Duplicate_ThrowsAlreadyExists()
    {
        var root = NewRoot();
        using var persistence = new JsonlSessionPersistence(root, compression: JsonlCompression.None);
        var session = BuildSourceSession(TestCwd);
        persistence.Create(session.Header);
        Assert.Throws<SessionAlreadyExistsException>(() => persistence.Create(session.Header));
    }

    [Fact]
    public void Open_WriteWhileOwned_ThrowsAlreadyOwned()
    {
        var root = NewRoot();
        using var persistence = new JsonlSessionPersistence(root, compression: JsonlCompression.None);
        var session = BuildSourceSession(TestCwd);
        var handle = persistence.Create(session.Header);
        handle.Append(session.SnapshotEvents());
        Assert.Throws<SessionAlreadyOwnedException>(() => persistence.Open(session.Header.Id, SessionAccess.Write));
        handle.Close();
        var writer = persistence.Open(session.Header.Id, SessionAccess.Write);
        writer.Close();
    }

    [Fact]
    public void ReadHandle_RejectsMutations()
    {
        var root = NewRoot();
        using var persistence = new JsonlSessionPersistence(root, compression: JsonlCompression.None);
        var session = BuildSourceSession(TestCwd);
        var handle = persistence.Create(session.Header);
        handle.Append(session.SnapshotEvents());
        handle.Close();
        var reader = persistence.Open(session.Header.Id, SessionAccess.Read);
        Assert.Throws<SessionReadOnlyException>(() => reader.Append(session.SnapshotEvents()));
        Assert.Throws<SessionReadOnlyException>(() => reader.Flush());
        reader.Close();
        Assert.Throws<SessionHandleClosedException>(() => reader.Read());
    }

    /** 工具分组的展开状态要能落盘: 重开日志后仍是同一条记录, 不是未知事件。 */
    [Theory]
    [InlineData(JsonlCompression.None)]
    [InlineData(JsonlCompression.Zstd)]
    public void RoundTrip_ToolDisclosure(JsonlCompression compression)
    {
        var root = NewRoot();
        using var persistence = new JsonlSessionPersistence(root, compression: compression);
        var session = BuildSourceSession(TestCwd);
        session.Append(new ToolDisclosedPayload("mcp:rider"));
        var handle = persistence.Create(session.Header);
        handle.Append(session.SnapshotEvents());
        handle.Close();

        using var reader = persistence.Open(session.Header.Id, SessionAccess.Read);
        var disclosed = Assert.IsType<ToolDisclosedPayload>(
            Assert.Single(reader.Read(), item => item.Data is ToolDisclosedPayload).Data);

        Assert.Equal("mcp:rider", disclosed.Group);
    }

    [Fact]
    public void Open_MissingSession_ThrowsNotFound()
    {
        var root = NewRoot();
        using var persistence = new JsonlSessionPersistence(root, compression: JsonlCompression.None);
        Assert.Throws<SessionPersistenceNotFoundException>(
            () => persistence.Open(SessionId.Create("missing"), SessionAccess.Read));
    }

    [Fact]
    public void Flush_MaterializesEmptySession()
    {
        var root = NewRoot();
        using var persistence = new JsonlSessionPersistence(root, compression: JsonlCompression.Zstd);
        var session = BuildSourceSession(TestCwd);
        var handle = persistence.Create(session.Header);
        handle.Flush();
        var logPath = LogPathOf(root, session.Header, JsonlCompression.Zstd);
        Assert.True(File.Exists(logPath));
        handle.Close();
        var reader = persistence.Open(session.Header.Id, SessionAccess.Read);
        Assert.Empty(reader.Read());
        Assert.Equal(session.Header.Id, reader.Header.Id);
    }

    [Fact]
    public void Title_RoundTrips_Through_Persistence()
    {
        var root = NewRoot();
        using var persistence = new JsonlSessionPersistence(root, compression: JsonlCompression.None);
        var id = SessionId.Create(Guid.NewGuid().ToString("N"));
        var session = Session.Create(id, header: new SessionHeader
        {
            Version = SessionHeader.SessionFormatVersion,
            Id = id,
            CreatedAt = 1700000000000,
            Cwd = TestCwd,
            IsSeeded = false,
            Title = "My session title",
        });
        var handle = persistence.Create(session.Header);
        handle.Flush();
        handle.Close();

        var snapshot = persistence.Stat(id);

        Assert.NotNull(snapshot);
        Assert.Equal("My session title", snapshot.Header.Title);
    }

    [Fact]
    public void Header_Without_Title_Reads_With_Null_Title()
    {
        var root = NewRoot();
        var id = SessionId.Create(Guid.NewGuid().ToString("N"));
        var path = JsonlLayout.LogPath(root, null, id, JsonlCompression.None);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, $"{{\"type\":\"session\",\"version\":0,\"id\":\"{id.Value}\",\"createdAt\":1,\"delegationDepth\":0}}\n");

        using var persistence = new JsonlSessionPersistence(root, compression: JsonlCompression.None);
        var snapshot = persistence.Stat(id);

        Assert.NotNull(snapshot);
        Assert.Null(snapshot.Header.Title);
    }

    [Theory]
    [InlineData(JsonlCompression.None)]
    [InlineData(JsonlCompression.Zstd)]
    public void Rename_UpdatesPersistedHeader(JsonlCompression compression)
    {
        var root = NewRoot();
        using var persistence = new JsonlSessionPersistence(root, compression: compression);
        var session = BuildSourceSession(TestCwd);
        var handle = persistence.Create(session.Header);
        handle.Append(session.SnapshotEvents());
        handle.Close();

        persistence.Rename(session.Header.Id, "Renamed title");

        var snapshot = persistence.Stat(session.Header.Id);
        Assert.NotNull(snapshot);
        Assert.Equal("Renamed title", snapshot.Header.Title);
        var reader = persistence.Open(session.Header.Id, SessionAccess.Read);
        Assert.Equal("Renamed title", reader.Header.Title);
        AssertEventsEqual(session.SnapshotEvents(), reader.Read().ToArray());
        reader.Close();
    }

    [Theory]
    [InlineData(JsonlCompression.None)]
    [InlineData(JsonlCompression.Zstd)]
    public void Rename_UpdatesActiveHandleHeaders(JsonlCompression compression)
    {
        var root = NewRoot();
        using var persistence = new JsonlSessionPersistence(root, compression: compression);
        var session = BuildSourceSession(TestCwd);
        var writer = persistence.Create(session.Header);
        writer.Append(session.SnapshotEvents());
        var reader = persistence.Open(session.Header.Id, SessionAccess.Read);

        persistence.Rename(session.Header.Id, "Active renamed");

        Assert.Equal("Active renamed", writer.Header.Title);
        Assert.Equal("Active renamed", reader.Header.Title);
        writer.Close();
        reader.Close();
    }

    [Theory]
    [InlineData(JsonlCompression.None)]
    [InlineData(JsonlCompression.Zstd)]
    public void List_ForkedSession_ReportsInheritedEventCountLikeStat(JsonlCompression compression)
    {
        var root = NewRoot();
        using var persistence = new JsonlSessionPersistence(root, compression: compression);
        var source = BuildSourceSession(TestCwd);
        using (var handle = persistence.Create(source.Header))
            handle.Append(source.SnapshotEvents());
        const long forkPoint = 3;

        var forkId = SessionFork.Fork(persistence, source, forkPoint, "fork", TestCwd);

        var forkSnapshot = persistence.Stat(forkId);
        Assert.NotNull(forkSnapshot);
        Assert.True(File.Exists(LogPathOf(root, forkSnapshot.Header, compression)));
        Assert.Equal(forkPoint, forkSnapshot.InheritedEventCount);
        var listed = persistence.List().ToDictionary(snapshot => snapshot.Header.Id);
        Assert.Equal(forkSnapshot.InheritedEventCount, listed[forkId].InheritedEventCount);
        Assert.Equal(0, listed[source.Header.Id].InheritedEventCount);
    }

    [Theory]
    [InlineData(JsonlCompression.None)]
    [InlineData(JsonlCompression.Zstd)]
    public void Delete_RemovesFileAndClosesHandles(JsonlCompression compression)
    {
        var root = NewRoot();
        using var persistence = new JsonlSessionPersistence(root, compression: compression);
        var session = BuildSourceSession(TestCwd);
        var handle = persistence.Create(session.Header);
        handle.Append(session.SnapshotEvents());
        var logPath = LogPathOf(root, session.Header, compression);
        Assert.True(File.Exists(logPath));

        persistence.Delete(session.Header.Id);

        Assert.Null(persistence.Stat(session.Header.Id));
        Assert.False(File.Exists(logPath));
        Assert.Throws<SessionHandleClosedException>(() => handle.Read());
        Assert.Throws<SessionPersistenceNotFoundException>(() => persistence.Open(session.Header.Id, SessionAccess.Read));
    }
}
