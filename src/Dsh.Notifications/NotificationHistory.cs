using System.Text.Json;

namespace Dsh.Notifications;

/** Low-frequency notification state: one in-memory list and an atomically replaced JSON snapshot, including read marks. */
public sealed class NotificationHistory
{
    private const int AtomicFileRetries = 3;
    private static readonly TimeSpan AtomicFileRetryDelay = TimeSpan.FromMilliseconds(10);

    private readonly string _path;
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly Lock _stateLock = new();
    private List<NotificationRecord> _records;

    private NotificationHistory(string path, List<NotificationRecord> records)
    {
        _path = path;
        _records = records;
    }

    public event Action? Changed;

    public string FilePath => _path;

    /** Opens only the notification snapshot; unrelated files are neither read nor changed. */
    public static async Task<NotificationHistory> OpenAsync(string path, CancellationToken cancellationToken = default)
    {
        var fullPath = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        if (File.Exists(fullPath))
        {
            await using var stream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            var records = await JsonSerializer.DeserializeAsync(stream, NotificationsJsonContext.Default.IReadOnlyListNotificationRecord, cancellationToken)
                ?? throw new JsonException("the notification snapshot must be an array");
            return new NotificationHistory(fullPath, [.. records]);
        }
        return new NotificationHistory(fullPath, []);
    }

    /** Matching notifications, newest first. */
    public IReadOnlyList<NotificationRecord> Query(NotificationFilter? filter = null)
    {
        filter ??= NotificationFilter.All;
        lock (_stateLock)
            return _records.Where(filter.Matches).Reverse().ToList();
    }

    public int CountUnread(NotificationFilter? filter = null)
    {
        var unread = (filter ?? NotificationFilter.All) with { UnreadOnly = true };
        lock (_stateLock)
            return _records.Count(unread.Matches);
    }

    /** Duplicate producer ids do not change the snapshot or emit Changed. */
    public async Task<bool> AppendAsync(NotificationRecord notification, CancellationToken cancellationToken = default)
    {
        await _writeGate.WaitAsync(cancellationToken);
        try
        {
            List<NotificationRecord> next;
            lock (_stateLock)
            {
                if (_records.Exists(record => record.Id == notification.Id))
                    return false;
                next = [.. _records];
            }
            var index = next.FindLastIndex(record => record.CreatedAt <= notification.CreatedAt);
            next.Insert(index + 1, notification);
            await SaveAsync(next, cancellationToken);
            lock (_stateLock)
                _records = next;
        }
        finally
        {
            _writeGate.Release();
        }
        Changed?.Invoke();
        return true;
    }

    public Task<IReadOnlyList<string>> MarkReadAsync(IEnumerable<string> ids, CancellationToken cancellationToken = default)
    {
        var wanted = ids.ToHashSet(StringComparer.Ordinal);
        return MarkAsync(record => wanted.Contains(record.Id), cancellationToken);
    }

    public Task<IReadOnlyList<string>> MarkAllReadAsync(NotificationFilter? filter = null, CancellationToken cancellationToken = default)
    {
        var matcher = filter ?? NotificationFilter.All;
        return MarkAsync(matcher.Matches, cancellationToken);
    }

    private async Task<IReadOnlyList<string>> MarkAsync(Func<NotificationRecord, bool> predicate, CancellationToken cancellationToken)
    {
        await _writeGate.WaitAsync(cancellationToken);
        List<string> changed = [];
        try
        {
            List<NotificationRecord> next;
            lock (_stateLock)
                next = _records.Select(record =>
                {
                    if (record.IsRead || !predicate(record))
                        return record;
                    changed.Add(record.Id);
                    return record with { IsRead = true };
                }).ToList();
            if (changed.Count > 0)
            {
                await SaveAsync(next, cancellationToken);
                lock (_stateLock)
                    _records = next;
            }
        }
        finally
        {
            _writeGate.Release();
        }
        if (changed.Count > 0)
            Changed?.Invoke();
        return changed;
    }

    private async Task SaveAsync(IReadOnlyList<NotificationRecord> records, CancellationToken cancellationToken)
    {
        var temporary = $"{_path}.tmp";
        try
        {
            await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(records, NotificationsJsonContext.Default.IReadOnlyListNotificationRecord), cancellationToken);
            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    if (File.Exists(_path))
                        File.Replace(temporary, _path, null);
                    else
                        File.Move(temporary, _path);
                    break;
                }
                catch (IOException) when (attempt < AtomicFileRetries)
                {
                    await Task.Delay(AtomicFileRetryDelay, cancellationToken);
                }
            }
        }
        finally
        {
            if (File.Exists(temporary))
                File.Delete(temporary);
        }
    }

}
