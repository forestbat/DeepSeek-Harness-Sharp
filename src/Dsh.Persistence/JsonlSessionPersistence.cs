using System.Security.Cryptography;
using System.Text;
using Dsh.Core;
using Dsh.Llm;

namespace Dsh.Persistence;

public sealed class JsonlSessionPersistence : ISessionPersistence, IDisposable
{
    private const int FirstLineChunkSize = 8192;
    private const string BackendName = "session-persistence-jsonl";
    private const string ArtifactKind = "jsonl";

    /**
     * 读日志一律放行写入与删除: 会话活着时定时落盘会从另一线程追加, Windows 上只共享读的读取会和追加流互相拒绝(CI 上 IOException)。
     * 带上 Delete, 截断与改标题用临时文件 File.Move 覆盖日志时也不会被正在读的句柄挡住。
     */
    private const FileShare LiveLogReadShare = FileShare.ReadWrite | FileShare.Delete;

    private readonly string _root;
    private readonly bool _packChunks;
    private readonly JsonlCompression _compression;
    private readonly object _trackerGate = new();
    private readonly Dictionary<SessionId, JsonlSessionHandle?> _writers = [];
    private readonly Dictionary<SessionId, PendingSession> _pending = [];
    private readonly HashSet<JsonlSessionHandle> _openHandles = [];
    private int _pendingCounter;
    private readonly Lazy<bool> _rootEncodingCheck;

    internal JsonlWriteBatch WriteBatch { get; }

    private sealed record PendingSession(SessionHeader Header, string Revision, long InheritedEventCount);

    private sealed record ParsedLog(SessionHeader Meta, List<SessionEvent> Events, long? TornTruncateTo, List<SessionEvent> RecoveredTail, long InheritedEventCount);

    internal sealed record StoredLog(SessionHeader Meta, List<SessionEvent> Events, long? TornTruncateTo, List<SessionEvent> RecoveredTail, long InheritedEventCount, string Revision);

    public JsonlSessionPersistence(
        string root,
        bool packChunks = true,
        JsonlCompression compression = JsonlCompression.Zstd,
        JsonlWriteBatch? writeBatch = null)
    {
        WriteBatch = writeBatch ?? JsonlWriteBatch.Default;
        if (WriteBatch.MaxEvents < 1 || WriteBatch.Interval <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(writeBatch), "write batch needs a positive event limit and interval");
        _root = Path.GetFullPath(root);
        _packChunks = packChunks;
        _compression = compression;
        if (File.Exists(_root)) throw new IOException($"session root \"{_root}\" exists and is not a directory");
        if (Directory.Exists(_root)) Directory.EnumerateFileSystemEntries(_root).GetEnumerator().Dispose();
        _rootEncodingCheck = new Lazy<bool>(() => { CheckRootEncoding(); return true; });
    }

    public ISessionHandle Create(SessionHeader header, long? inheritedEventCount = null)
    {
        ArgumentNullException.ThrowIfNull(header);
        header.Validate();
        SessionLogHeader.ValidateSeedCut(header.IsSeeded, inheritedEventCount);
        EnsureRootEncoding();
        lock (_trackerGate)
        {
            if (_pending.ContainsKey(header.Id) || _writers.ContainsKey(header.Id) || FindLog(header.Id) is not null)
                throw new SessionAlreadyExistsException(header.Id);
            _writers[header.Id] = null;
            var cut = inheritedEventCount ?? 0;
            _pending[header.Id] = new PendingSession(header, $"memory:{BackendName}:{++_pendingCounter}", cut);
            return AdoptLocked(new JsonlSessionHandle(this, header.Id, header, SessionAccess.Write,
                new StorageHandleState { Cursor = 0, Materialized = false, InheritedEventCount = cut }));
        }
    }

    public ISessionHandle Open(SessionId id, SessionAccess access)
    {
        EnsureRootEncoding();
        if (access == SessionAccess.Read)
        {
            lock (_trackerGate)
            {
                if (_pending.TryGetValue(id, out var pending))
                    return AdoptLocked(new JsonlSessionHandle(this, id, pending.Header, SessionAccess.Read,
                        new StorageHandleState { Cursor = 0, Materialized = false, InheritedEventCount = pending.InheritedEventCount }));
            }
            var snapshot = Stat(id);
            if (snapshot is null)
            {
                var stored = RequireStoredLog(id);
                lock (_trackerGate)
                    return AdoptLocked(new JsonlSessionHandle(this, id, stored.Meta, SessionAccess.Read,
                        new StorageHandleState { Cursor = 0, Materialized = true, InheritedEventCount = stored.InheritedEventCount }));
            }
            AssertVersion(snapshot.Header, Locate(snapshot.Header));
            lock (_trackerGate)
                return AdoptLocked(new JsonlSessionHandle(this, id, snapshot.Header, SessionAccess.Read,
                    new StorageHandleState { Cursor = 0, Materialized = true, InheritedEventCount = snapshot.InheritedEventCount }));
        }
        ClaimWrite(id);
        try
        {
            var stored = RequireStoredLog(id);
            var storedLength = new FileInfo(JsonlLayout.LogPath(_root, stored.Meta.Cwd, id, _compression)).Length;
            lock (_trackerGate)
                return AdoptLocked(new JsonlSessionHandle(this, id, stored.Meta, SessionAccess.Write,
                    new StorageHandleState
                    {
                        Cursor = stored.Events.Count,
                        Materialized = true,
                        TornTruncateTo = stored.TornTruncateTo,
                        RecoveredTail = stored.RecoveredTail,
                        InheritedEventCount = stored.InheritedEventCount,
                        Primed = stored.Events,
                        WrittenLength = storedLength,
                    }));
        }
        catch
        {
            ReleaseClaim(id);
            throw;
        }
    }

    public void Flush()
    {
        List<JsonlSessionHandle?> writers;
        lock (_trackerGate) writers = [.. _writers.Values];
        var errors = new List<Exception>();
        foreach (var writer in writers)
        {
            if (writer is null) continue;
            try { writer.Flush(); }
            catch (SessionHandleClosedException) { }
            catch (Exception error) { errors.Add(error); }
        }
        if (errors.Count > 0) throw new AggregateException($"{BackendName} flush failed", errors);
    }

    public SessionPersistenceSnapshot? Stat(SessionId id)
    {
        EnsureRootEncoding();
        lock (_trackerGate)
        {
            // 未落盘的内存会话事件数为 0; 已在磁盘上的日志这里不数事件数(null = 未知)。
            if (_pending.TryGetValue(id, out var pending))
                return new SessionPersistenceSnapshot { Header = pending.Header, Revision = pending.Revision, EventCount = 0, InheritedEventCount = pending.InheritedEventCount };
        }
        var path = FindLog(id);
        return path is null ? null : ReadLogSnapshot(path, id);
    }

    public IReadOnlyList<SessionPersistenceSnapshot> List()
    {
        List<KeyValuePair<SessionId, PendingSession>> pending;
        lock (_trackerGate) pending = [.. _pending];
        var snapshots = ListStoredSnapshots();
        var listed = snapshots.Select(snapshot => snapshot.Header.Id).ToHashSet();
        foreach (var (id, entry) in pending)
            if (!listed.Contains(id))
                snapshots.Add(new SessionPersistenceSnapshot { Header = entry.Header, Revision = entry.Revision, EventCount = 0, InheritedEventCount = entry.InheritedEventCount });
        return snapshots;
    }

    public void Rename(SessionId id, string title)
    {
        ArgumentNullException.ThrowIfNull(title);
        EnsureRootEncoding();
        var snapshot = Stat(id) ?? throw new SessionPersistenceNotFoundException(id);
        var renamed = snapshot.Header with { Title = title };
        var path = FindLog(id);
        if (path is not null)
        {
            // 写句柄的定时落盘会并发追加, 重写头部期间必须持有它的锁。
            JsonlSessionHandle? writer;
            lock (_trackerGate) writer = _writers.GetValueOrDefault(id);
            if (writer is null)
                RewriteHeader(path, renamed, snapshot.InheritedEventCount);
            else
                writer.WhileDrained(() => RewriteHeader(path, renamed, snapshot.InheritedEventCount));
        }
        List<JsonlSessionHandle> handles;
        lock (_trackerGate)
        {
            if (_pending.TryGetValue(id, out var pending))
                _pending[id] = pending with { Header = renamed };
            handles = [.. _openHandles.Where(handle => handle.Id == id)];
        }
        // 句柄锁在持有期间可能要进 _trackerGate(首次落盘), 这里在 _trackerGate 之外更新, 保持同一加锁顺序。
        foreach (var handle in handles)
            handle.UpdateHeader(renamed);
    }

    public void Delete(SessionId id)
    {
        EnsureRootEncoding();
        var path = FindLog(id);
        var sessionDir = path is null ? null : Path.GetDirectoryName(path);
        List<JsonlSessionHandle> handles;
        lock (_trackerGate)
            handles = [.. _openHandles.Where(handle => handle.Id == id)];
        foreach (var handle in handles)
            handle.Close();
        lock (_trackerGate)
        {
            _pending.Remove(id);
            _writers.Remove(id);
        }
        if (sessionDir is not null && Directory.Exists(sessionDir))
        {
            var trash = Path.Combine(_root, ".trash");
            Directory.CreateDirectory(trash);
            var trashPath = Path.Combine(trash, $"{JsonlLayout.EncodeSegment(id.Value)}-{Guid.NewGuid():N}");
            Directory.Move(sessionDir, trashPath);
            Directory.Delete(trashPath, recursive: true);
        }
    }

    public void Dispose()
    {
        List<JsonlSessionHandle> handles;
        lock (_trackerGate) handles = [.. _openHandles];
        var errors = new List<Exception>();
        foreach (var handle in handles)
        {
            try { handle.Close(); }
            catch (Exception error) { errors.Add(error); }
        }
        if (errors.Count > 0) throw new AggregateException($"{BackendName} dispose failed", errors);
    }

    private void RewriteHeader(string path, SessionHeader header, long inheritedEventCount)
    {
        var headerLine = Encoding.UTF8.GetBytes(
            SessionLogHeader.WriteHeaderLine(header, header.IsSeeded ? inheritedEventCount : null) + "\n");
        byte[] replacement;
        if (_compression == JsonlCompression.None)
        {
            var original = ReadLiveLog(path);
            var newline = Array.IndexOf(original, (byte)'\n');
            if (newline < 0)
                throw new SessionPersistenceCorruptionException($"session \"{header.Id}\": log has no header line: {path}");
            replacement = [.. headerLine, .. original.AsSpan(newline + 1).ToArray()];
        }
        else
        {
            var original = ReadLiveLog(path);
            var (frames, _) = ZstdFrames.Scan(original);
            if (frames.Count == 0)
                throw new SessionPersistenceCorruptionException($"session \"{header.Id}\": log has no header frame: {path}");
            replacement = [.. ZstdFrames.CompressFrame(headerLine), .. original.AsSpan(frames[0].End).ToArray()];
        }
        var temp = $"{path}.{Convert.ToHexString(RandomNumberGenerator.GetBytes(6)).ToLowerInvariant()}.tmp";
        File.WriteAllBytes(temp, replacement);
        File.Move(temp, path, overwrite: true);
    }

    internal string? ResolveLog(SessionId id)
    {
        EnsureRootEncoding();
        return FindLog(id);
    }

    internal bool HasPendingSession(SessionId id)
    {
        lock (_trackerGate) return _pending.ContainsKey(id);
    }

    internal StoredLog ReadStoredLog(string path, SessionId expectedId)
    {
        var (buffer, revision) = ReadStableFile(path);
        ParsedLog parsed;
        try
        {
            parsed = _compression == JsonlCompression.Zstd ? ReadZstdPrefix(buffer) : ReadPlainPrefix(buffer);
        }
        catch (SessionFormatUnsupportedException error)
        {
            throw new SessionFormatUnsupportedException($"{error.Message} (raw log: {path})", new SessionLocation(ArtifactKind, path));
        }
        catch (SessionPersistenceCorruptionException)
        {
            throw;
        }
        catch (Exception error)
        {
            throw new SessionPersistenceCorruptionException($"session \"{expectedId}\": stored log is corrupt: {error.Message} (raw log: {path})", error);
        }
        AssertStoredIdentity(path, parsed.Meta, expectedId);
        if (parsed.Meta.Id != expectedId)
            throw new InvalidDataException($"stored session identity mismatch: requested \"{expectedId}\", header contains \"{parsed.Meta.Id}\"");
        var location = Locate(parsed.Meta);
        AssertVersion(parsed.Meta, location);
        ValidateStoredEvents(parsed.Meta, parsed.Events, location);
        return new StoredLog(parsed.Meta, parsed.Events, parsed.TornTruncateTo, parsed.RecoveredTail, parsed.InheritedEventCount, revision);
    }

    internal void PersistBatch(SessionHeader header, IReadOnlyList<SessionEvent> events, StorageHandleState state)
    {
        EnsureRootEncoding();
        if (state.Materialized)
        {
            AppendLines(header, events, state);
            return;
        }
        state.WrittenLength = Materialize(header, state.InheritedEventCount, events);
        MarkMaterialized(header.Id);
    }

    internal void PersistHeader(SessionHeader header, StorageHandleState state)
    {
        EnsureRootEncoding();
        state.WrittenLength = Materialize(header, state.InheritedEventCount, []);
        MarkMaterialized(header.Id);
    }

    internal void TruncateTornTail(SessionHeader header, long truncateTo)
    {
        var path = JsonlLayout.LogPath(_root, header.Cwd, header.Id, _compression);
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.Read);
        stream.SetLength(truncateTo);
        stream.Flush(true);
    }

    /** 就地重写日志到前 eventCount 个事件(D3 revert): 头行 + 保留事件整体原子替换, 失败不改原文件。 */
    internal long? TruncateLog(SessionHeader header, long eventCount)
    {
        var path = FindLog(header.Id);
        if (path is null)
            return null;
        var stored = ReadStoredLog(path, header.Id);
        var keep = (int)Math.Clamp(eventCount, 0, stored.Events.Count);
        if (keep == stored.Events.Count)
            return null;
        var kept = stored.Events.GetRange(0, keep);
        var content = EncodeMaterialization(stored.Meta, stored.InheritedEventCount, kept);
        var temp = $"{path}.{Convert.ToHexString(RandomNumberGenerator.GetBytes(6)).ToLowerInvariant()}.tmp";
        using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            stream.Write(content);
            stream.Flush(true);
        }
        File.Move(temp, path, overwrite: true);
        return content.Length;
    }

    internal void ReleaseHandle(JsonlSessionHandle handle, bool materialized)
    {
        lock (_trackerGate)
        {
            _openHandles.Remove(handle);
            if (handle.Access != SessionAccess.Write) return;
            _writers.Remove(handle.Id);
            if (!materialized) _pending.Remove(handle.Id);
        }
    }

    private StoredLog RequireStoredLog(SessionId id)
        => FindLog(id) is { } path
            ? ReadStoredLog(path, id)
            : throw new SessionPersistenceNotFoundException(id);

    private ParsedLog ReadPlainPrefix(byte[] buffer)
    {
        var scan = SessionLogScannerCompat.ScanLog(buffer);
        return new ParsedLog(
            scan.Meta,
            scan.Events,
            scan.CommittedBytes < buffer.Length ? scan.CommittedBytes : null,
            [],
            scan.InheritedEventCount);
    }

    private ParsedLog ReadZstdPrefix(byte[] buffer)
    {
        var (frames, tornStart) = ZstdFrames.Scan(buffer);
        if (frames.Count == 0) throw new FormatException("empty or header-less Zstandard session log");
        var headerPlaintext = DecompressFrame(buffer, frames[0]);
        AssertZstdHeaderFrame(headerPlaintext);
        var scanner = new SessionLogScanner(headerPlaintext);
        // 复用同一个解压器把余下各帧一次解出: 逐帧 new Decompressor 在长会话上是上百次上下文重建。
        scanner.Write(ZstdFrames.DecompressFrames(frames, buffer, skip: 1));
        var (inputBytes, committedBytes, eventCount) = scanner.Checkpoint();
        if (committedBytes != inputBytes)
            throw new FormatException("corrupt Zstandard session log: complete frame contains a torn JSONL record");
        if (tornStart is null)
        {
            var complete = scanner.Finish();
            return new ParsedLog(complete.Meta, complete.Events, null, [], complete.InheritedEventCount);
        }
        var recovered = ZstdFrames.DecompressPrefix(buffer[tornStart.Value..]);
        scanner.Write(recovered);
        var prefix = scanner.Finish();
        return new ParsedLog(
            prefix.Meta,
            prefix.Events,
            tornStart.Value,
            [.. prefix.Events.Skip((int)eventCount)],
            prefix.InheritedEventCount);
    }

    private static byte[] DecompressFrame(byte[] buffer, ZstdFrames.FrameRange frame)
        => ZstdFrames.DecompressFrame(buffer.AsSpan(frame.Start, frame.End - frame.Start), frame.Start);

    private static void AssertZstdHeaderFrame(byte[] plaintext)
    {
        if (plaintext.Length == 0 || Array.IndexOf(plaintext, (byte)'\n') != plaintext.Length - 1)
            throw new FormatException("corrupt Zstandard session log: first frame is not exactly one header line");
    }

    private long Materialize(SessionHeader meta, long inheritedEventCount, IReadOnlyList<SessionEvent> events)
    {
        var dir = JsonlLayout.SessionDir(_root, meta.Cwd, meta.Id);
        var finalPath = JsonlLayout.LogPath(_root, meta.Cwd, meta.Id, _compression);
        RejectOppositeArtifact(meta.Cwd, meta.Id);
        var content = EncodeMaterialization(meta, inheritedEventCount, events);
        Directory.CreateDirectory(dir);
        if (File.Exists(finalPath))
            throw new InvalidOperationException($"refusing to materialize \"{meta.Id}\": a log already exists on disk (open it instead)");
        var temp = $"{finalPath}.{Convert.ToHexString(RandomNumberGenerator.GetBytes(6)).ToLowerInvariant()}.tmp";
        using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            stream.Write(content);
            stream.Flush(true);
        }
        try
        {
            File.Move(temp, finalPath);
        }
        catch
        {
            try { File.Delete(temp); } catch (Exception cleanupError) when (cleanupError is IOException or UnauthorizedAccessException) { }
            if (File.Exists(finalPath)) throw new SessionAlreadyExistsException(meta.Id);
            throw;
        }
        return content.Length;
    }

    private byte[] EncodeMaterialization(SessionHeader meta, long inheritedEventCount, IReadOnlyList<SessionEvent> events)
    {
        var headerLine = SessionLogHeader.WriteHeaderLine(meta, meta.IsSeeded ? inheritedEventCount : null) + "\n";
        var headerBytes = Encoding.UTF8.GetBytes(headerLine);
        if (events.Count == 0)
            return _compression == JsonlCompression.None ? headerBytes : ZstdFrames.CompressFrame(headerBytes);
        var bodyBytes = Encoding.UTF8.GetBytes(ChunkRows.EncodeEventLines(events, _packChunks) + "\n");
        if (_compression == JsonlCompression.None)
        {
            var plain = new byte[headerBytes.Length + bodyBytes.Length];
            headerBytes.CopyTo(plain, 0);
            bodyBytes.CopyTo(plain, headerBytes.Length);
            return plain;
        }
        var headerFrame = ZstdFrames.CompressFrame(headerBytes);
        var eventFrame = ZstdFrames.CompressFrame(bodyBytes);
        return [.. headerFrame, .. eventFrame];
    }

    private byte[] EncodeEventBatch(IReadOnlyList<SessionEvent> events)
    {
        var body = Encoding.UTF8.GetBytes(ChunkRows.EncodeEventLines(events, _packChunks) + "\n");
        return _compression == JsonlCompression.Zstd ? ZstdFrames.CompressFrame(body) : body;
    }

    private void AppendLines(SessionHeader meta, IReadOnlyList<SessionEvent> events, StorageHandleState state)
    {
        var content = EncodeEventBatch(events);
        var path = JsonlLayout.LogPath(_root, meta.Cwd, meta.Id, _compression);
        long before;
        using (var probe = new FileStream(path, FileMode.Open, FileAccess.Read, LiveLogReadShare))
        {
            before = probe.Length;
        }
        // 日志在本写入者两次落盘之间被别人推进过: 本写入者的序号已经落后于文件内容。
        // 这时继续追加会把两套序号交织进同一个文件, 之后整份日志都读不回来。
        if (state.WrittenLength is { } expected && before != expected)
            throw new SessionPersistenceCorruptionException(
                $"session \"{meta.Id}\": the log grew from {expected} to {before} bytes while this writer held it — another process appended to the same session; refusing to interleave events");
        try
        {
            using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read);
            stream.Write(content);
            stream.Flush(true);
        }
        catch
        {
            state.WrittenLength = before;
            RollbackAppend(path, before);
            throw;
        }
        state.WrittenLength = before + content.Length;
    }

    private static void RollbackAppend(string path, long size)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.Read);
        stream.SetLength(size);
        stream.Flush(true);
    }

    /**
     * 读一份快照, 并确认读取期间文件没被追加。
     * 正常情况下只读一次; 只有在读的当口写入方又追加了才重读一遍, 第二次仍不稳定就按已知长度截断返回。
     */
    private (byte[] Buffer, string Revision) ReadStableFile(string path)
    {
        var info = new FileInfo(path);
        for (var attempt = 0; ; attempt += 1)
        {
            info.Refresh();
            var before = FileRevision(info);
            var buffer = ReadLiveLog(path);
            var afterInfo = new FileInfo(path);
            afterInfo.Refresh();
            if (before == FileRevision(afterInfo)) return (buffer, before);
            if (attempt == 1) return (buffer[..(int)Math.Min(buffer.Length, info.Length)], before);
            info = afterInfo;
        }
    }

    /**
     * 一次分配读完整个日志。
     * 旧实现走 MemoryStream + CopyTo + ToArray: 内容先在 MemoryStream 的内部缓冲里存一份,
     * ToArray 再复制一份, 34 MB 的日志因此峰值占两份内存。按已知长度直接分配目标数组后 ReadExactly,
     * 边读边把实际读到的长度缩减, 就只需要一块缓冲。
     */
    private static byte[] ReadLiveLog(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, LiveLogReadShare);
        var length = stream.Length;
        if (length == 0)
            return [];
        if (length > int.MaxValue)
            throw new FormatException($"session log is larger than addressable memory: {length} bytes");
        var buffer = new byte[length];
        var read = 0;
        while (read < buffer.Length)
        {
            // 读取期间写入方可能追加, Length 是那时快照; 读不满就按实际读到的截断。
            var chunk = stream.Read(buffer, read, buffer.Length - read);
            if (chunk == 0)
                break;
            read += chunk;
        }
        return read == buffer.Length ? buffer : buffer[..read];
    }

    private static string FileRevision(FileInfo info)
    {
        info.Refresh();
        return $"file:{info.Length}:{info.LastWriteTimeUtc.Ticks}:{info.CreationTimeUtc.Ticks}";
    }

    private string? ReadFirstLineAny(string path)
        => _compression == JsonlCompression.Zstd ? ReadFirstZstdLine(path) : ReadFirstLine(path);

    private static string? ReadFirstLine(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, LiveLogReadShare);
        using var accumulated = new MemoryStream();
        var buffer = new byte[FirstLineChunkSize];
        int read;
        while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
        {
            var newline = Array.IndexOf(buffer, (byte)'\n', 0, read);
            if (newline >= 0)
            {
                accumulated.Write(buffer, 0, newline);
                return Encoding.UTF8.GetString(accumulated.ToArray());
            }
            accumulated.Write(buffer, 0, read);
        }
        return null;
    }

    private static string? ReadFirstZstdLine(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, LiveLogReadShare);
        using var accumulated = new MemoryStream();
        var chunk = new byte[FirstLineChunkSize];
        int read;
        while ((read = stream.Read(chunk, 0, chunk.Length)) > 0)
        {
            accumulated.Write(chunk, 0, read);
            var content = accumulated.ToArray();
            var (frames, _) = ZstdFrames.Scan(content, maxFrames: 1);
            if (frames.Count == 0) continue;
            byte[] plaintext;
            try
            {
                plaintext = ZstdFrames.DecompressFrame(content.AsSpan(frames[0].Start, frames[0].End - frames[0].Start), frames[0].Start);
            }
            catch (FormatException error)
            {
                throw new FormatException("corrupt Zstandard session log: header frame failed validation", error);
            }
            AssertZstdHeaderFrame(plaintext);
            return Encoding.UTF8.GetString(plaintext, 0, plaintext.Length - 1);
        }
        return null;
    }

    private string? FindLog(SessionId id)
    {
        var matches = new List<string>();
        foreach (var project in ListProjectDirs())
        {
            RejectLegacyFlatArtifact(project, id);
            var dir = Path.Join(project, JsonlLayout.EncodeSegment(id.Value));
            var path = Path.Join(dir, $"session{JsonlLayout.LogSuffix(_compression)}");
            var opposite = Path.Join(dir, $"session{JsonlLayout.LogSuffix(OppositeCompression())}");
            if (ExistsFile(opposite)) throw EncodingMismatch(opposite);
            if (ExistsFile(path)) matches.Add(path);
        }
        if (matches.Count > 1)
            throw new InvalidDataException($"duplicate JSONL session id \"{id}\" appears in multiple project directories");
        return matches.Count == 0 ? null : matches[0];
    }

    private List<SessionPersistenceSnapshot> ListStoredSnapshots()
    {
        var snapshots = new List<SessionPersistenceSnapshot>();
        var ids = new HashSet<SessionId>();
        foreach (var project in ListProjectDirs())
        {
            foreach (var dir in ListSessionDirs(project))
            {
                var opposite = Path.Join(dir, $"session{JsonlLayout.LogSuffix(OppositeCompression())}");
                if (ExistsFile(opposite)) throw EncodingMismatch(opposite);
                var path = Path.Join(dir, $"session{JsonlLayout.LogSuffix(_compression)}");
                if (!ExistsFile(path)) continue;
                SessionPersistenceSnapshot? snapshot;
                try
                {
                    snapshot = ReadLogSnapshot(path, null);
                }
                catch (SessionFormatUnsupportedException)
                {
                    continue;
                }
                if (snapshot is null) continue;
                if (!ids.Add(snapshot.Header.Id))
                    throw new InvalidDataException($"duplicate JSONL session id \"{snapshot.Header.Id}\" appears in multiple project directories");
                snapshots.Add(snapshot);
            }
        }
        return snapshots;
    }

    /** Stat and List both build stored snapshots here, reading only the log's header line or frame. */
    private SessionPersistenceSnapshot? ReadLogSnapshot(string path, SessionId? expectedId)
    {
        string? first;
        try
        {
            first = ReadFirstLineAny(path);
        }
        catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException)
        {
            return null;
        }
        if (first is null) return null;
        SessionStorageMetadata? stored;
        try
        {
            stored = SessionLogHeader.ParseHeader(first);
        }
        catch (SessionFormatUnsupportedException error)
        {
            throw new SessionFormatUnsupportedException($"{error.Message} (raw log: {path})", new SessionLocation(ArtifactKind, path));
        }
        if (stored is null) return null;
        AssertStoredIdentity(path, stored.Meta, expectedId);
        var info = new FileInfo(path);
        info.Refresh();
        if (!info.Exists) return null;
        return new SessionPersistenceSnapshot
        {
            Header = stored.Meta,
            Revision = FileRevision(info),
            SizeBytes = info.Length,
            InheritedEventCount = stored.InheritedEventCount,
        };
    }

    private List<string> ListProjectDirs()
        => Directory.Exists(_root) ? [.. Directory.GetDirectories(_root)] : [];

    private static List<string> ListSessionDirs(string project)
    {
        var entries = Directory.GetFileSystemEntries(project);
        foreach (var entry in entries)
        {
            if (File.Exists(entry) && (entry.EndsWith(".jsonl", StringComparison.Ordinal) || entry.EndsWith(".jsonl.zstd", StringComparison.Ordinal)))
                throw LegacyLayout(entry);
        }
        return [.. entries.Where(Directory.Exists)];
    }

    private void CheckRootEncoding()
    {
        foreach (var project in ListProjectDirs())
        {
            foreach (var dir in ListSessionDirs(project))
            {
                var incompatible = Path.Join(dir, $"session{JsonlLayout.LogSuffix(OppositeCompression())}");
                if (ExistsFile(incompatible)) throw EncodingMismatch(incompatible);
            }
        }
    }

    private void EnsureRootEncoding() => _ = _rootEncodingCheck.Value;

    private void RejectLegacyFlatArtifact(string project, SessionId id)
    {
        var encoded = JsonlLayout.EncodeSegment(id.Value);
        foreach (var compression in new[] { JsonlCompression.Zstd, JsonlCompression.None })
        {
            var path = Path.Join(project, encoded + JsonlLayout.LogSuffix(compression));
            if (ExistsFile(path)) throw LegacyLayout(path);
        }
    }

    private void RejectOppositeArtifact(string? cwd, SessionId id)
    {
        var path = JsonlLayout.LogPath(_root, cwd, id, OppositeCompression());
        if (ExistsFile(path)) throw EncodingMismatch(path);
    }

    private JsonlCompression OppositeCompression()
        => _compression == JsonlCompression.Zstd ? JsonlCompression.None : JsonlCompression.Zstd;

    private Exception EncodingMismatch(string path)
        => new InvalidOperationException(
            $"session artifact \"{path}\" uses {JsonlLayout.LogSuffix(OppositeCompression())}, "
            + $"but this backend is configured for compression \"{_compression.ToString().ToLowerInvariant()}\"; "
            + "use a separate root or select the matching compression mode");

    private static Exception LegacyLayout(string path)
        => new InvalidOperationException(
            $"session artifact \"{path}\" uses the unsupported flat-file layout; "
            + "use a separate root or move it into a project/session directory before loading");

    private static bool ExistsFile(string path) => File.Exists(path);

    private SessionLocation Locate(SessionHeader meta)
        => new(ArtifactKind, JsonlLayout.LogPath(_root, meta.Cwd, meta.Id, _compression));

    private void AssertStoredIdentity(string path, SessionHeader meta, SessionId? expectedId)
    {
        if (expectedId is { } expected && meta.Id != expected)
            throw new InvalidDataException($"corrupt session log \"{path}\": requested id \"{expected}\" does not match header id \"{meta.Id}\"");
        var expectedPath = JsonlLayout.LogPath(_root, meta.Cwd, meta.Id, _compression);
        if (!string.Equals(Path.GetFullPath(path), Path.GetFullPath(expectedPath), StringComparison.Ordinal))
            throw new InvalidDataException($"corrupt session log \"{path}\": header id \"{meta.Id}\" and cwd identify \"{expectedPath}\"");
    }

    private static void AssertVersion(SessionHeader meta, SessionLocation? location)
    {
        if (meta.Version != SessionHeader.SessionFormatVersion)
            throw Unsupported(SessionRefusals.FormatVersionRefusal(meta.Id.Value, meta.Version), location);
    }

    private static void ValidateStoredEvents(SessionHeader meta, List<SessionEvent> events, SessionLocation? location)
    {
        foreach (var sessionEvent in events)
        {
            if (!SessionEventCodec.IsRegistered(sessionEvent.Type) && !sessionEvent.Ignorable)
                throw Unsupported(
                    $"session \"{meta.Id}\" contains event type \"{sessionEvent.Type}\" (seq {sessionEvent.Seq}) unknown to this harness and not marked ignorable; refusing to interpret the log — it was likely written by a newer harness",
                    location);
            if (sessionEvent.Data is RequestHeaderPayload { Reason: "fallback" })
                throw Unsupported(
                    $"session \"{meta.Id}\" contains a request/header event (seq {sessionEvent.Seq}) with the unsupported legacy reason \"fallback\"; refusing to interpret the log — it was written by a retired pre-release harness",
                    location);
        }
    }

    private static SessionFormatUnsupportedException Unsupported(string reason, SessionLocation? location)
        => new(location is null ? reason : $"{reason} (raw log: {location.Path})", location);

    private void ClaimWrite(SessionId id)
    {
        lock (_trackerGate)
        {
            if (_writers.ContainsKey(id)) throw new SessionAlreadyOwnedException(id);
            _writers[id] = null;
        }
    }

    private void ReleaseClaim(SessionId id)
    {
        lock (_trackerGate) _writers.Remove(id);
    }

    private void MarkMaterialized(SessionId id)
    {
        lock (_trackerGate) _pending.Remove(id);
    }

    private JsonlSessionHandle AdoptLocked(JsonlSessionHandle handle)
    {
        _openHandles.Add(handle);
        if (handle.Access == SessionAccess.Write) _writers[handle.Id] = handle;
        return handle;
    }
}
