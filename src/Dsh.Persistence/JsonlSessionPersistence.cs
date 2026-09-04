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

    private readonly string _root;
    private readonly bool _packChunks;
    private readonly JsonlCompression _compression;
    private readonly object _trackerGate = new();
    private readonly Dictionary<SessionId, JsonlSessionHandle?> _writers = [];
    private readonly Dictionary<SessionId, PendingSession> _pending = [];
    private readonly HashSet<JsonlSessionHandle> _openHandles = [];
    private int _pendingCounter;
    private readonly Lazy<bool> _rootEncodingCheck;

    private sealed record PendingSession(SessionHeader Header, string Revision, long InheritedEventCount);

    private sealed record ParsedLog(SessionHeader Meta, List<SessionEvent> Events, long? TornTruncateTo, List<SessionEvent> RecoveredTail, long InheritedEventCount);

    internal sealed record StoredLog(SessionHeader Meta, List<SessionEvent> Events, long? TornTruncateTo, List<SessionEvent> RecoveredTail, long InheritedEventCount, string Revision);

    public JsonlSessionPersistence(string root, bool packChunks = true, JsonlCompression compression = JsonlCompression.Zstd)
    {
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
        lock (_trackerGate) writers = [.._writers.Values];
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
            if (_pending.TryGetValue(id, out var pending))
                return new SessionPersistenceSnapshot { Header = pending.Header, Revision = pending.Revision, InheritedEventCount = pending.InheritedEventCount };
        }
        var path = FindLog(id);
        if (path is null) return null;
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
        AssertStoredIdentity(path, stored.Meta, id);
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

    public IReadOnlyList<SessionPersistenceSnapshot> List()
    {
        List<KeyValuePair<SessionId, PendingSession>> pending;
        lock (_trackerGate) pending = [.._pending];
        var snapshots = new List<SessionPersistenceSnapshot>();
        var listed = new HashSet<SessionId>();
        foreach (var (meta, path) in ListArtifacts())
        {
            var info = new FileInfo(path);
            info.Refresh();
            if (!info.Exists) continue;
            listed.Add(meta.Id);
            snapshots.Add(new SessionPersistenceSnapshot { Header = meta, Revision = FileRevision(info), SizeBytes = info.Length });
        }
        foreach (var (id, entry) in pending)
            if (!listed.Contains(id))
                snapshots.Add(new SessionPersistenceSnapshot { Header = entry.Header, Revision = entry.Revision, InheritedEventCount = entry.InheritedEventCount });
        return snapshots;
    }

    public void Dispose()
    {
        List<JsonlSessionHandle> handles;
        lock (_trackerGate) handles = [.._openHandles];
        var errors = new List<Exception>();
        foreach (var handle in handles)
        {
            try { handle.Close(); }
            catch (Exception error) { errors.Add(error); }
        }
        if (errors.Count > 0) throw new AggregateException($"{BackendName} dispose failed", errors);
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

    internal void PersistBatch(SessionHeader header, IReadOnlyList<SessionEvent> events, bool isMaterialized, long inheritedEventCount)
    {
        EnsureRootEncoding();
        if (isMaterialized)
        {
            AppendLines(header, events);
            return;
        }
        Materialize(header, inheritedEventCount, events);
        MarkMaterialized(header.Id);
    }

    internal void PersistHeader(SessionHeader header, long inheritedEventCount)
    {
        EnsureRootEncoding();
        Materialize(header, inheritedEventCount, []);
        MarkMaterialized(header.Id);
    }

    internal void TruncateTornTail(SessionHeader header, long truncateTo)
    {
        var path = JsonlLayout.LogPath(_root, header.Cwd, header.Id, _compression);
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.Read);
        stream.SetLength(truncateTo);
        stream.Flush(true);
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
        for (var index = 1; index < frames.Count; index += 1)
            scanner.Write(DecompressFrame(buffer, frames[index]));
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
            [..prefix.Events.Skip((int)eventCount)],
            prefix.InheritedEventCount);
    }

    private static byte[] DecompressFrame(byte[] buffer, ZstdFrames.FrameRange frame)
        => ZstdFrames.DecompressFrame(buffer.AsSpan(frame.Start, frame.End - frame.Start), frame.Start);

    private static void AssertZstdHeaderFrame(byte[] plaintext)
    {
        if (plaintext.Length == 0 || Array.IndexOf(plaintext, (byte)'\n') != plaintext.Length - 1)
            throw new FormatException("corrupt Zstandard session log: first frame is not exactly one header line");
    }

    private void Materialize(SessionHeader meta, long inheritedEventCount, IReadOnlyList<SessionEvent> events)
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
            try { File.Delete(temp); } catch { }
            if (File.Exists(finalPath)) throw new SessionAlreadyExistsException(meta.Id);
            throw;
        }
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
        return [..headerFrame, ..eventFrame];
    }

    private byte[] EncodeEventBatch(IReadOnlyList<SessionEvent> events)
    {
        var body = Encoding.UTF8.GetBytes(ChunkRows.EncodeEventLines(events, _packChunks) + "\n");
        return _compression == JsonlCompression.Zstd ? ZstdFrames.CompressFrame(body) : body;
    }

    private void AppendLines(SessionHeader meta, IReadOnlyList<SessionEvent> events)
    {
        var content = EncodeEventBatch(events);
        var path = JsonlLayout.LogPath(_root, meta.Cwd, meta.Id, _compression);
        long before;
        using (var probe = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        {
            before = probe.Length;
        }
        try
        {
            using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read);
            stream.Write(content);
            stream.Flush(true);
        }
        catch
        {
            RollbackAppend(path, before);
            throw;
        }
    }

    private static void RollbackAppend(string path, long size)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.Read);
        stream.SetLength(size);
        stream.Flush(true);
    }

    private (byte[] Buffer, string Revision) ReadStableFile(string path)
    {
        var info = new FileInfo(path);
        for (var attempt = 0; ; attempt += 1)
        {
            info.Refresh();
            var before = FileRevision(info);
            var buffer = File.ReadAllBytes(path);
            var afterInfo = new FileInfo(path);
            afterInfo.Refresh();
            if (before == FileRevision(afterInfo)) return (buffer, before);
            if (attempt == 1) return (buffer[..(int)Math.Min(buffer.Length, info.Length)], before);
            info = afterInfo;
        }
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
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
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
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
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

    private List<(SessionHeader Meta, string Path)> ListArtifacts()
    {
        var artifacts = new List<(SessionHeader, string)>();
        var ids = new HashSet<SessionId>();
        foreach (var project in ListProjectDirs())
        {
            foreach (var dir in ListSessionDirs(project))
            {
                var opposite = Path.Join(dir, $"session{JsonlLayout.LogSuffix(OppositeCompression())}");
                if (ExistsFile(opposite)) throw EncodingMismatch(opposite);
                var path = Path.Join(dir, $"session{JsonlLayout.LogSuffix(_compression)}");
                if (!ExistsFile(path)) continue;
                var first = ReadFirstLineAny(path);
                if (first is null) continue;
                SessionHeader? meta;
                try
                {
                    meta = SessionLogHeader.ParseHeader(first)?.Meta;
                }
                catch (SessionFormatUnsupportedException)
                {
                    continue;
                }
                if (meta is null) continue;
                AssertStoredIdentity(path, meta, null);
                if (!ids.Add(meta.Id))
                    throw new InvalidDataException($"duplicate JSONL session id \"{meta.Id}\" appears in multiple project directories");
                artifacts.Add((meta, path));
            }
        }
        return artifacts;
    }

    private List<string> ListProjectDirs()
        => Directory.Exists(_root) ? [..Directory.GetDirectories(_root)] : [];

    private static List<string> ListSessionDirs(string project)
    {
        var entries = Directory.GetFileSystemEntries(project);
        foreach (var entry in entries)
        {
            if (File.Exists(entry) && (entry.EndsWith(".jsonl", StringComparison.Ordinal) || entry.EndsWith(".jsonl.zstd", StringComparison.Ordinal)))
                throw LegacyLayout(entry);
        }
        return [..entries.Where(Directory.Exists)];
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
