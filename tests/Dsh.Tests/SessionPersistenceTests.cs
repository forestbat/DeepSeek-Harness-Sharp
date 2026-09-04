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
        AssertEventsEqual([..events, extra], reopened.Read().ToArray());
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
        AssertEventsEqual([..events, extra], reopened.Read().ToArray());
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
    public void ProjectKey_EncodesProjectPaths(string cwd, string expected)
        => Assert.Equal(expected, JsonlLayout.ProjectKey(cwd));

    [Fact]
    public void ProjectKey_TruncatesLongSlugs()
    {
        var key = JsonlLayout.ProjectKey("/" + new string('a', 300));
        Assert.Equal($"--{new string('a', JsonlLayout.MaxProjectSlugLength)}--", key);
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
}
