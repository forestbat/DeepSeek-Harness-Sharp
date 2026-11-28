using System.Text.Json;
using Dsh.Core;
using Dsh.Llm;

namespace Dsh.Schedule;

public sealed record ScheduleWakeEntry(
    AgentId AgentId,
    SessionId SessionId,
    long DueAtMs,
    string? Cwd = null);

/** Persists the earliest due reminder timestamp per agent/session at `<home>/schedules/wake-index.json`. */
public sealed class ScheduleWakeIndex
{
    private const UnixFileMode OwnerDirectoryMode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
    private const UnixFileMode OwnerFileMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;

    private readonly Lock _gate = new();
    private readonly string? _indexFilePath;
    private readonly List<ScheduleWakeEntry> _entries = [];

    public ScheduleWakeIndex(string? indexFilePath = null)
    {
        _indexFilePath = indexFilePath;
        Load();
    }

    public IReadOnlyList<ScheduleWakeEntry> List()
    {
        lock (_gate)
            return [.. _entries.OrderBy(entry => entry.DueAtMs)];
    }

    public ScheduleWakeEntry? Get(AgentId agentId, SessionId sessionId)
    {
        lock (_gate)
            return _entries.Find(entry => entry.AgentId == agentId && entry.SessionId == sessionId);
    }

    public void Upsert(ScheduleWakeEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        lock (_gate)
        {
            var index = _entries.FindIndex(item => item.SessionId == entry.SessionId);
            if (index >= 0)
            {
                if (_entries[index] == entry)
                    return;
                _entries[index] = entry;
            }
            else
            {
                _entries.Add(entry);
            }
            PersistLocked();
        }
    }

    public bool Remove(AgentId agentId, SessionId sessionId)
    {
        lock (_gate)
        {
            var removed = _entries.RemoveAll(item => item.AgentId == agentId && item.SessionId == sessionId);
            if (removed == 0)
                return false;
            PersistLocked();
            return true;
        }
    }

    public bool RemoveBySession(SessionId sessionId)
    {
        lock (_gate)
        {
            var removed = _entries.RemoveAll(item => item.SessionId == sessionId);
            if (removed == 0)
                return false;
            PersistLocked();
            return true;
        }
    }

    public ScheduleWakeEntry? NextDue()
    {
        lock (_gate)
            return _entries.Count == 0 ? null : _entries.MinBy(entry => entry.DueAtMs);
    }

    public IReadOnlyList<ScheduleWakeEntry> DueAt(long nowMs)
    {
        lock (_gate)
        {
            return
            [
                .. _entries
                    .Where(entry => entry.DueAtMs <= nowMs)
                    .OrderBy(entry => entry.DueAtMs),
            ];
        }
    }

    private void Load()
    {
        if (_indexFilePath is null || !File.Exists(_indexFilePath))
            return;
        var bytes = File.ReadAllBytes(_indexFilePath);
        if (bytes.Length == 0)
            return;
        var loaded = JsonSerializer.Deserialize(bytes, DshScheduleJsonContext.Default.ListScheduleWakeEntry);
        if (loaded is null)
            return;
        lock (_gate)
        {
            _entries.Clear();
            _entries.AddRange(loaded);
        }
    }

    private void PersistLocked()
    {
        if (_indexFilePath is null)
            return;
        if (_entries.Count == 0)
        {
            TryDelete(_indexFilePath);
            return;
        }
        var directory = Path.GetDirectoryName(_indexFilePath)
            ?? throw new InvalidOperationException($"invalid wake index path \"{_indexFilePath}\"");
        EnsureDirectory(directory);
        var tempPath = $"{_indexFilePath}.{Guid.NewGuid():N}.tmp";
        try
        {
            var options = new FileStreamOptions
            {
                Mode = FileMode.CreateNew,
                Access = FileAccess.Write,
                Share = FileShare.None,
            };
            if (!OperatingSystem.IsWindows())
                options.UnixCreateMode = OwnerFileMode;
            using (var stream = new FileStream(tempPath, options))
            {
                JsonSerializer.Serialize(stream, _entries, DshScheduleJsonContext.Default.ListScheduleWakeEntry);
                stream.Flush(flushToDisk: true);
            }
            File.Move(tempPath, _indexFilePath, overwrite: true);
        }
        catch
        {
            TryDelete(tempPath);
            throw;
        }
    }

    private static void EnsureDirectory(string directory)
    {
        if (OperatingSystem.IsWindows())
        {
            Directory.CreateDirectory(directory);
            return;
        }
        Directory.CreateDirectory(directory, OwnerDirectoryMode);
        File.SetUnixFileMode(directory, OwnerDirectoryMode);
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
