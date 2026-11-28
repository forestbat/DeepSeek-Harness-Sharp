using System.Text.Json;
using Dsh.Core;
using Dsh.Llm;
using Dsh.Persistence;
using Dsh.Persistence.Database;
using Dsh.Agent;
using Dsh.Runtime;

namespace Dsh.Tests;

/**
 * 数据库后端的契约行为: 与 JSONL 后端跑同一份断言(建/追加/落盘/重开/窗口/切片/导入导出/并发提交)。
 */
public sealed class DatabaseSessionPersistenceTests : IDisposable
{
    private const string TestCwd = "/tmp/dsh-db-persist";
    private readonly List<string> _roots = [];

    private string NewRoot()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"dsh-db-{Guid.NewGuid():N}");
        _roots.Add(dir);
        Directory.CreateDirectory(dir);
        return dir;
    }

    public void Dispose()
    {
        foreach (var root in _roots)
        {
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    private DatabaseSessionPersistence Open(string root, EventStoreOptions? options = null)
        => new(Path.Combine(root, "sessions.db"), options, warn: _ => { });

    private static SessionHeader NewHeader(string? cwd = TestCwd)
    {
        var id = SessionId.Create(Guid.NewGuid().ToString("N"));
        return new SessionHeader
        {
            Version = SessionHeader.SessionFormatVersion,
            Id = id,
            CreatedAt = 1700000000000,
            Cwd = cwd,
            IsSeeded = false,
        };
    }

    private static Session BuildSourceSession()
    {
        var header = NewHeader();
        var session = Session.Create(header.Id, header: header);
        session.Append(new TurnStartPayload(1));
        session.Append(new StepStartPayload(1, 1));
        for (var index = 0; index < 5; index += 1)
            session.Append(new AssistantChunkPayload(1, 1, new StreamChunk.TextDelta(0, $"tok{index}")));
        session.Append(new AssistantMessagePayload(1, 1,
            MessageFactory.CreateAssistantMessage([new TextBlock("done")], "deepseek", "deepseek-chat")),
            new SurfaceOp.Append(), [2, 3, 4, 6]);
        session.Append(new TurnEndPayload(1, new TurnEndReason.Completed()));
        return session;
    }

    private static void AssertEventsEqual(IReadOnlyList<SessionEvent> expected, IReadOnlyList<SessionEvent> actual)
    {
        Assert.Equal(expected.Count, actual.Count);
        foreach (var (left, right) in expected.Zip(actual))
            Assert.Equal(JsonSerializer.Serialize(left, DshJson.Options), JsonSerializer.Serialize(right, DshJson.Options));
    }

    [Fact]
    public void RoundTrip_AppendFlushReadReopen()
    {
        var root = NewRoot();
        var session = BuildSourceSession();
        var events = session.SnapshotEvents();
        using (var persistence = Open(root))
        {
            using var handle = persistence.Create(session.Header);
            handle.Append([.. events.Take(2)]);
            handle.Append([.. events.Skip(2)]);
            handle.Flush();
            Assert.Equal(events.Count, persistence.StoredEventCount(session.Header.Id));
            handle.Close();
        }
        using (var reopened = Open(root))
        {
            var snapshot = reopened.Stat(session.Header.Id);
            Assert.NotNull(snapshot);
            Assert.Equal(events.Count, snapshot.EventCount);
            Assert.Equal(session.Header.Cwd, snapshot.Header.Cwd);
            using var reader = reopened.Open(session.Header.Id, SessionAccess.Read);
            AssertEventsEqual(events, reader.Read());
            AssertEventsEqual([.. events.Skip(2).Take(3)], reader.Read(2, 3));
            Assert.Empty(reader.Read(events.Count));
            Assert.Equal(events.Count, reader.Count);
            Assert.Single(reopened.List(), entry => entry.Header.Id == session.Header.Id);
        }
    }

    [Fact]
    public void BoundaryEvents_CommitWithoutExplicitFlush()
    {
        var root = NewRoot();
        var session = BuildSourceSession();
        using var persistence = Open(root);
        using var handle = persistence.Create(session.Header);
        var turnStart = session.SnapshotEvents()[0];
        handle.Append([turnStart]);
        // 回合边界不攒批: Append 返回时事件已在库里。
        Assert.Equal(1, persistence.StoredEventCount(session.Header.Id));
    }

    [Fact]
    public void WindowedOpen_MatchesFullRead()
    {
        var root = NewRoot();
        var session = BuildChunkedSession(120);
        var events = session.SnapshotEvents();
        // 帧按提交批次切: 一次追加只落一帧, 所以这里把条数/字节预算调小, 切出足够多帧,
        // 窗口才可能真的窄于整份日志(生产里大日志天然是多帧)。
        using var persistence = new DatabaseSessionPersistence(Path.Combine(root, "sessions.db"),
            writeBatch: new DatabaseWriteBatch(MaxEvents: 2, MaxPlaintextBytes: 1024, Interval: TimeSpan.FromMilliseconds(1)),
            warn: _ => { });
        using (var handle = persistence.Create(session.Header))
        {
            handle.Append(events);
            handle.Flush();
        }
        using var reader = persistence.Open(session.Header.Id, SessionAccess.Read);
        var window = persistence.TryOpenWindow(session.Header.Id, 20);
        Assert.NotNull(window);
        var tail = window.Value;
        Assert.Equal(events.Count, tail.TotalEvents);
        Assert.True(tail.ResidentFrom > 0);
        AssertEventsEqual([.. events.Skip((int)tail.ResidentFrom)], tail.Resident);
        var paged = tail.ReadPage is null ? [] : tail.ReadPage(0, (int)tail.ResidentFrom);
        AssertEventsEqual([.. events.Take((int)tail.ResidentFrom)], paged);
        _ = reader;
    }

    [Fact]
    public void LogSlice_MatchesJsonlContentHash()
    {
        var root = NewRoot();
        var session = BuildSourceSession();
        var events = session.SnapshotEvents();
        var jsonlRoot = Path.Combine(root, "jsonl");
        using var jsonl = new JsonlSessionPersistence(jsonlRoot);
        using (var handle = jsonl.Create(session.Header))
        {
            handle.Append(events);
            handle.Flush();
        }
        var jsonlSlice = jsonl.TryReadLogSlice(session.Header.Id, 0, int.MaxValue);
        Assert.NotNull(jsonlSlice);

        using var database = Open(root);
        using (var handle = database.Create(session.Header))
        {
            handle.Append(events);
            handle.Flush();
        }
        var databaseSlice = database.TryReadLogSlice(session.Header.Id, 0, int.MaxValue);
        Assert.NotNull(databaseSlice);
        Assert.Equal(jsonlSlice.ContentHash, databaseSlice.ContentHash);
        Assert.Equal(jsonlSlice.Bytes, databaseSlice.Bytes);
        Assert.Equal(jsonlSlice.EventCount, databaseSlice.EventCount);
    }

    [Fact]
    public void ImportFromJsonl_ThenExportBack_KeepsEvents()
    {
        var root = NewRoot();
        var session = BuildSourceSession();
        var events = session.SnapshotEvents();
        var jsonlRoot = Path.Combine(root, "jsonl");
        using (var jsonl = new JsonlSessionPersistence(jsonlRoot))
        using (var handle = jsonl.Create(session.Header))
        {
            handle.Append(events);
            handle.Flush();
        }

        using (var database = Open(root))
        {
            Assert.Equal(1, database.ImportFrom(jsonlRoot));
            using var reader = database.Open(session.Header.Id, SessionAccess.Read);
            AssertEventsEqual(events, reader.Read());
            var exported = database.ExportTo(Path.Combine(root, "exported"), session.Header.Id);
            Assert.True(File.Exists(exported));
        }

        using var roundTrip = new JsonlSessionPersistence(Path.Combine(root, "exported"));
        using var reopened = roundTrip.Open(session.Header.Id, SessionAccess.Read);
        AssertEventsEqual(events, reopened.Read());
    }

    [Fact]
    public void RenameAndDelete()
    {
        var root = NewRoot();
        var session = BuildSourceSession();
        var events = session.SnapshotEvents();
        using var persistence = Open(root);
        using (var handle = persistence.Create(session.Header))
        {
            handle.Append(events);
            handle.Flush();
        }
        persistence.Rename(session.Header.Id, "renamed");
        using (var reader = persistence.Open(session.Header.Id, SessionAccess.Read))
            Assert.Equal("renamed", reader.Header.Title);
        Assert.Equal("renamed", persistence.Stat(session.Header.Id)?.Header.Title);

        persistence.Delete(session.Header.Id);
        Assert.Null(persistence.Stat(session.Header.Id));
        Assert.Equal(0, persistence.StoredEventCount(session.Header.Id));
        Assert.DoesNotContain(persistence.List(), entry => entry.Header.Id == session.Header.Id);
    }

    [Fact]
    public void SurfaceFold_RoundTrip()
    {
        var root = NewRoot();
        var session = BuildSourceSession();
        var events = session.SnapshotEvents();
        using var persistence = Open(root);
        using (var handle = persistence.Create(session.Header))
        {
            handle.Append(events);
            handle.Flush();
        }
        var fold = new SurfaceFoldSnapshot([0L, 2L, 4L], events.Count, events.Count, 0);
        persistence.SaveSurfaceFold(session.Header.Id, fold);
        var loaded = persistence.TryLoadSurfaceFold(session.Header.Id);
        Assert.NotNull(loaded);
        Assert.Equal([0L, 2L, 4L], loaded.Nodes);
        persistence.DropSurfaceFold(session.Header.Id);
        Assert.Null(persistence.TryLoadSurfaceFold(session.Header.Id));
    }

    /** 折叠检查点的节点编码自身要能往返(它是 SaveSurfaceFold 读不回来的第一嫌疑人)。 */
    [Fact]
    public void SurfaceFoldCodec_RoundTripsIncreasingNodes()
    {
        var nodes = Enumerable.Range(0, 400).Select(index => (long)(index * 7)).ToList();
        var payload = SurfaceFoldCodec.EncodeNodes(nodes);
        var decoded = SurfaceFoldCodec.DecodeAll(payload);
        Assert.Equal(nodes, decoded);
    }

    /** 端到端: 按产品恢复路径打开一次、写下检查点, 再读必须一模一样地拿回来。 */
    [Fact]
    public void FoldCheckpoint_FromRestorePath_RoundTrips()
    {
        var root = NewRoot();
        var session = BuildSourceSession();
        var events = session.SnapshotEvents();
        using var persistence = new DatabaseSessionPersistence(Path.Combine(root, "sessions.db"),
            warn: message => Console.Error.WriteLine($"[warn] {message}"));
        using (var handle = persistence.Create(session.Header))
        {
            handle.Append(events);
            handle.Flush();
        }
        SurfaceFoldSnapshot fold;
        using (var handle = persistence.Open(session.Header.Id, SessionAccess.Read))
        {
            var opened = SessionRestore.Restore(session.Header.Id, persistence, handle);
            _ = opened.DeriveMessages();
            // 与宿主 resume 同一步: 恢复出来的会话比日志多一条(end-seed 标记), 先落盘再写检查点。
            using (var writer = persistence.Open(session.Header.Id, SessionAccess.Write))
            {
                writer.Append(opened.SnapshotEvents(writer.Count));
                writer.Flush();
            }
            fold = opened.FoldSnapshot() ?? throw new InvalidOperationException("折叠没有跟上日志末尾");
            persistence.SaveSurfaceFold(session.Header.Id, fold);
        }
        var loaded = persistence.TryLoadSurfaceFold(session.Header.Id);
        Assert.NotNull(loaded);
        Assert.Equal(fold.Nodes, loaded.Nodes);
        // 检查点记的是"折到第几条": 读回来必须与写下去的一致(读侧据此接着折)。
        // 恢复出来的会话会多一条 end-seed 标记, 宿主把它落盘之后再写检查点, 所以这里比原日志多一条。
        Assert.Equal(fold.TotalEvents, loaded.TotalEvents);
        Assert.Equal(events.Count + 1, loaded.TotalEvents);
    }

    /** 部分进度(只折了一部分)的检查点也必须能存能读: 只读打开不会折到日志末尾。 */
    [Fact]
    public void SurfaceFold_PartialProgress_RoundTrips()
    {
        var root = NewRoot();
        var session = BuildSourceSession();
        var events = session.SnapshotEvents();
        using var persistence = Open(root);
        using (var handle = persistence.Create(session.Header))
        {
            handle.Append(events);
            handle.Flush();
        }
        var partial = new SurfaceFoldSnapshot([0L, 2L], 0, 2, 0);
        persistence.SaveSurfaceFold(session.Header.Id, partial);
        var loaded = persistence.TryLoadSurfaceFold(session.Header.Id);
        Assert.NotNull(loaded);
        Assert.Equal([0L, 2L], loaded.Nodes);
        Assert.Equal(2, loaded.TotalEvents);
    }

    [Fact]
    public void StaleNumber_RenumbersBufferedTail()
    {
        var root = NewRoot();
        var header = NewHeader();
        // 关掉定时落盘: 本用例要制造"缓冲还没落盘, 号就被别人用掉"这一幕。
        var manual = new DatabaseWriteBatch(int.MaxValue, int.MaxValue, TimeSpan.FromHours(1));
        using var mine = new DatabaseSessionPersistence(Path.Combine(root, "sessions.db"), writeBatch: manual, warn: _ => { });
        using var handle = mine.Create(header);
        var session = Session.Create(header.Id, header: header);
        handle.TailSource = from => [.. session.SnapshotEvents(from)];
        handle.RebaseSource = session.RebaseUnsaved;
        session.AttachSequencer(handle.Sequencer);

        handle.Append([session.Append(new AssistantChunkPayload(1, 1, new StreamChunk.TextDelta(0, "a"))),
            session.Append(new AssistantChunkPayload(1, 1, new StreamChunk.TextDelta(0, "b")))]);
        handle.Flush();
        Assert.Equal(2, mine.StoredEventCount(header.Id));

        // 本进程接着发出 2、3 两号并交给句柄(句柄缓冲, 还没落盘)。
        handle.Append([session.Append(new AssistantChunkPayload(1, 1, new StreamChunk.TextDelta(0, "c"))),
            session.Append(new AssistantChunkPayload(1, 1, new StreamChunk.TextDelta(0, "d")))]);
        Assert.Equal(4, session.Seq);

        // 另一个宿主把 2 号写进了同一个库(它按库里的条数发号, 顺序完全合法)。
        using (var rival = new DatabaseSessionPersistence(Path.Combine(root, "sessions.db"), writeBatch: manual, warn: _ => { }))
        using (var rivalHandle = rival.Open(header.Id, SessionAccess.Write))
        {
            rivalHandle.Append([new SessionEvent
            {
                Type = new AssistantChunkPayload(1, 1, new StreamChunk.TextDelta(0, "rival")).Type,
                Seq = 2,
                Time = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                Data = new AssistantChunkPayload(1, 1, new StreamChunk.TextDelta(0, "rival")),
            }]);
            rivalHandle.Flush();
        }

        var renumbered = new List<(long From, long Delta)>();
        session.Renumbered += (_, from, delta) => renumbered.Add((from, delta));
        handle.Flush();

        Assert.Equal([(2L, 1L)], renumbered);
        using var reader = mine.Open(header.Id, SessionAccess.Read);
        Assert.Equal([0L, 1L, 2L, 3L, 4L], reader.Read().Select(sessionEvent => sessionEvent.Seq));
    }
    [Fact]
    public void ConcurrentSessions_AllCommittedWithGroupCommit()
    {
        var root = NewRoot();
        const int sessions = 32;
        const int perSession = 12;
        using var persistence = Open(root, new EventStoreOptions
        {
            Lanes = CommitLaneOptions.Default with { LaneCount = 4, WindowMilliseconds = 5 },
        });
        var headers = new SessionHeader[sessions];
        var sessionsList = new Session[sessions];
        for (var index = 0; index < sessions; index += 1)
        {
            sessionsList[index] = BuildChunkedSession(perSession);
            headers[index] = sessionsList[index].Header;
        }
        Parallel.For(0, sessions, index =>
        {
            using var handle = persistence.Create(headers[index]);
            handle.Append(sessionsList[index].SnapshotEvents());
            handle.Flush();
        });
        for (var index = 0; index < sessions; index += 1)
        {
            using var reader = persistence.Open(headers[index].Id, SessionAccess.Read);
            Assert.Equal(perSession + 2, reader.Count);
            AssertEventsEqual(sessionsList[index].SnapshotEvents(), reader.Read());
        }
        var (commits, batches) = persistence.CommitCounters;
        Assert.True(commits > 0);
        Assert.True(batches >= sessions);
        Assert.True(commits < batches, $"expected group commit to merge batches ({commits} commits for {batches} batches)");
    }

    [Fact]
    public void Handoff_TransfersSliceAndContinuesSequence()
    {
        var sourceRoot = NewRoot();
        var targetRoot = NewRoot();
        var session = BuildSourceSession();
        var events = session.SnapshotEvents();
        using var source = Open(sourceRoot);
        using (var handle = source.Create(session.Header))
        {
            handle.Append(events);
            handle.Flush();
        }
        // 交接就是"把源端的帧视图交给目标端": 与导入/导出同一条共享契约, 没有另一套切片格式。
        var frames = source.TryReadLogFrames(session.Header.Id);
        Assert.NotNull(frames);
        Assert.True(frames.TotalEventCount > 0);

        using var target = Open(targetRoot);
        target.ImportLog(frames, generation: 1);
        Assert.Equal(events.Count, target.StoredEventCount(session.Header.Id));
        using (var reader = target.Open(session.Header.Id, SessionAccess.Read))
            AssertEventsEqual(events, reader.Read());

        using var writer = target.Open(session.Header.Id, SessionAccess.Write);
        var next = new SessionEvent
        {
            Type = new TurnStartPayload(2).Type,
            Seq = events.Count,
            Time = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            Data = new TurnStartPayload(2),
        };
        writer.Append([next]);
        writer.Flush();
        using var reloaded = target.Open(session.Header.Id, SessionAccess.Read);
        Assert.Equal(events.Count + 1, reloaded.Count);
        Assert.Equal(events.Count, reloaded.Read(events.Count, 1).Single().Seq);
    }

    [Fact]
    public void Backup_ProducesOpenableCopy()
    {
        var root = NewRoot();
        var session = BuildSourceSession();
        var events = session.SnapshotEvents();
        using var persistence = Open(root);
        using (var handle = persistence.Create(session.Header))
        {
            handle.Append(events);
            handle.Flush();
        }
        var copyPath = Path.Combine(root, "backup.db");
        persistence.Backup(copyPath);
        Assert.True(File.Exists(copyPath));
        using var copy = new DatabaseSessionPersistence(copyPath, warn: _ => { });
        using var reader = copy.Open(session.Header.Id, SessionAccess.Read);
        AssertEventsEqual(events, reader.Read());
    }

    [Fact]
    public void IoUringDriver_FallsBackToSyncWhenUnavailable()
    {
        var root = NewRoot();
        var warnings = new List<string>();
        using var persistence = new DatabaseSessionPersistence(Path.Combine(root, "sessions.db"),
            new EventStoreOptions { Driver = TursoIoDriver.IoUring }, warn: warnings.Add);
        var session = BuildSourceSession();
        var events = session.SnapshotEvents();
        using (var handle = persistence.Create(session.Header))
        {
            handle.Append(events);
            handle.Flush();
        }
        using var reader = persistence.Open(session.Header.Id, SessionAccess.Read);
        AssertEventsEqual(events, reader.Read());
        // Windows 与内核 <5.19 上 io_uring 不可用: 必须退回同步并把原因喊出来。
        if (OperatingSystem.IsWindows())
            Assert.Contains(warnings, message => message.Contains("io_uring", StringComparison.Ordinal));
    }

    [Fact]
    public void ManySessions_EachFlushIsDurable()
    {
        var root = NewRoot();
        const int count = 200;
        using var persistence = Open(root);
        var sessions = Enumerable.Range(0, count).Select(_ => BuildChunkedSession(4)).ToArray();
        Parallel.For(0, count, index =>
        {
            using var handle = persistence.Create(sessions[index].Header);
            handle.Append(sessions[index].SnapshotEvents());
            handle.Flush();
        });
        Assert.Equal(count, persistence.List().Count);
        var (commits, batches) = persistence.CommitCounters;
        Assert.True(batches >= count);
        Assert.True(commits <= batches);
    }
    [Fact]
    public void Plugin_WiresDatabaseBackendFromSettings()
    {
        var root = NewRoot();
        var context = new Context();
        var (created, owned) = Dsh.Persistence.Plugin.CreatePersistence(
            DshPaths.Resolve(root),
            new SessionStorageSettings { Backend = SessionStorageSettings.DatabaseBackend },
            context.LoggerFor("test"));
        using var bundle = owned;
        var persistence = Assert.IsType<DatabaseSessionPersistence>(created);
        var session = BuildSourceSession();
        var events = session.SnapshotEvents();
        using (var handle = persistence.Create(session.Header))
        {
            handle.Append(events);
            handle.Flush();
        }
        using var reader = persistence.Open(session.Header.Id, Dsh.Core.SessionAccess.Read);
        AssertEventsEqual(events, reader.Read());
        Assert.True(File.Exists(Path.Combine(root, "sessions.db")));
    }
    [Fact]
    public void EscrowedFrames_AreReplayedOnNextOpen()
    {
        var root = NewRoot();
        var session = BuildSourceSession();
        var events = session.SnapshotEvents();
        string escrowPath;
        using (var persistence = Open(root))
        {
            using var handle = persistence.Create(session.Header);
            handle.Append([.. events.Take(2)]);
            handle.Flush();
            // 引擎写不进去(盘满/IO/损坏)时的动作: 这一段编成帧另存, 下次打开重放。
            persistence.SaveUnsavedTail(session.Header, 0, [.. events.Skip(2)], new IOException("disk full"));
            escrowPath = Assert.Single(Directory.GetFiles(root, "sessions.db.pending-*.log"));
        }
        Assert.True(File.Exists(escrowPath));

        using (var reopened = Open(root))
        {
            Assert.Equal(events.Count, reopened.StoredEventCount(session.Header.Id));
            using var reader = reopened.Open(session.Header.Id, SessionAccess.Read);
            AssertEventsEqual(events, reader.Read());
        }
        Assert.False(File.Exists(escrowPath));
    }

    [Fact]
    public void EscrowedFrames_AreKeptWhenTheyNoLongerFitTheLog()
    {
        var root = NewRoot();
        var session = BuildSourceSession();
        var events = session.SnapshotEvents();
        using var persistence = Open(root);
        using (var handle = persistence.Create(session.Header))
        {
            handle.Append(events);
            handle.Flush();
        }
        // 会话已经写到末尾了, 再重放同一段就是重复: 文件必须原样留着, 不能悄悄插进去。
        persistence.SaveUnsavedTail(session.Header, 0, [.. events.Skip(2)], new IOException("disk full"));
        var escrowPath = Assert.Single(Directory.GetFiles(root, "sessions.db.pending-*.log"));
        using var reopened = Open(root);
        Assert.Equal(events.Count, reopened.StoredEventCount(session.Header.Id));
        Assert.True(File.Exists(escrowPath));
    }
    private static Session BuildChunkedSession(int chunkCount)
    {
        var header = NewHeader();
        var session = Session.Create(header.Id, header: header);
        session.Append(new TurnStartPayload(1));
        session.Append(new StepStartPayload(1, 1));
        for (var index = 0; index < chunkCount; index += 1)
            session.Append(new AssistantChunkPayload(1, 1, new StreamChunk.TextDelta(0, $"tok{index}")));
        return session;
    }
}





















