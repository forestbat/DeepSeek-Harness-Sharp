using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;
using Dsh.Transport;

namespace Dsh.RemoteHost;

/**
 * 仅保存 Host 创建或恢复的基础登记; pending 数量与 WaitingForUser 始终即时查询权威队列。
 * 基础登记持久化供重启对账, 每次真实变化直接发布, 不缓存第二份投影或活动推送时间。
 */
public sealed class HostSessionRegistry : IAsyncDisposable
{
    private const int AtomicFileRetries = 3;
    private static readonly TimeSpan AtomicFileRetryDelay = TimeSpan.FromMilliseconds(10);

    private readonly Lock _gate = new();
    private readonly HostBroadcast _broadcast = new();
    private readonly OrderedDictionary<string, HostSessionRegistration> _entries = new(StringComparer.Ordinal);
    private readonly HostPendingDecisions _pending;
    private readonly string _storePath;
    private readonly Channel<bool> _saveRequests = Channel.CreateBounded<bool>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });
    private readonly SemaphoreSlim _saveGate = new(1, 1);
    private readonly Task _saver;

    private HostSessionRegistry(string storePath, HostPendingDecisions pending, IEnumerable<HostSessionRegistration> records)
    {
        _storePath = storePath;
        _pending = pending;
        foreach (var record in records)
            _entries[record.SessionId] = record;
        pending.CountChanged += OnPendingChanged;
        _saver = SaveLoopAsync();
    }

    /** Raised under the registry lock; handlers only hand the projection off. */
    public event Action<RemoteSessionSummary>? SessionExited;

    /** Host-owned processes exited; surviving daemon PTYs are restored by HostPtySessions reconciliation. */
    public static async Task<HostSessionRegistry> OpenAsync(string storePath, HostPendingDecisions pending, CancellationToken cancellationToken)
    {
        var records = await LoadAsync(storePath, cancellationToken).ConfigureAwait(false);
        return new HostSessionRegistry(storePath, pending, records.Select(record => record with { Status = HostSessionStatuses.Exited }));
    }

    public IReadOnlyList<RemoteSessionSummary> List()
    {
        lock (_gate)
            return [.. _entries.Values.Select(Project)];
    }

    public RemoteSessionSummary? Get(string sessionId)
    {
        lock (_gate)
            return _entries.TryGetValue(sessionId, out var record) ? Project(record) : null;
    }

    /** Private launch facts never enter RemoteSessionSummary or session-change notifications. */
    public HostAgentLaunch? AgentLaunch(string sessionId)
    {
        lock (_gate)
            return _entries.TryGetValue(sessionId, out var record) ? record.AgentLaunch : null;
    }

    /** Re-registration preserves the original creation time and identity fields from the new registration. */
    public void Register(RemoteSessionSummary record, HostAgentLaunch? agentLaunch = null)
    {
        lock (_gate)
        {
            var registration = HostSessionRegistration.From(record);
            if (_entries.TryGetValue(record.SessionId, out var existing))
            {
                registration = registration with { CreatedAt = existing.CreatedAt, AgentLaunch = agentLaunch ?? existing.AgentLaunch };
                if (registration == existing)
                    return;
            }
            else
                registration = registration with { AgentLaunch = agentLaunch };
            _entries[record.SessionId] = registration;
            Publish(registration, existing?.Status);
            _saveRequests.Writer.TryWrite(true);
        }
    }

    public void SetStatus(string sessionId, string status)
        => Update(sessionId, record => record with { Status = status, ExitCode = status == HostSessionStatuses.Exited ? record.ExitCode : null });

    /** Structured agent lifecycle changes cannot revive an already exited session. */
    public void SetLiveStatus(string sessionId, string status)
        => Update(sessionId, record => record.Status == HostSessionStatuses.Exited ? record : record with { Status = status });

    public void SetTitle(string sessionId, string? title) => Update(sessionId, record => record with { Title = title });

    public void Touch(string sessionId, long time)
        => Update(sessionId, record => time > record.LastActivityAt ? record with { LastActivityAt = time } : record);

    public void MarkExited(string sessionId, int? exitCode)
    {
        Update(sessionId, record => record with { Status = HostSessionStatuses.Exited, ExitCode = exitCode });
        _pending.CancelSession(sessionId);
    }

    public void Remove(string sessionId)
    {
        lock (_gate)
        {
            if (!_entries.Remove(sessionId, out var record))
                return;
            Publish(Project(record) with { Deleted = true });
            _saveRequests.Writer.TryWrite(true);
        }
    }

    public HostSubscription Subscribe()
    {
        lock (_gate)
            return _broadcast.Subscribe([HostNotification.Create(
                HostProtocol.NotificationSessionsChanged, new RemoteSessionChange(Snapshot: List()), HostProtocolJsonContext.Default.RemoteSessionChange)]);
    }

    public async ValueTask DisposeAsync()
    {
        _pending.CountChanged -= OnPendingChanged;
        _saveRequests.Writer.TryComplete();
        await _saver.ConfigureAwait(false);
        await FlushAsync().ConfigureAwait(false);
        _saveGate.Dispose();
    }

    private void OnPendingChanged(string sessionId)
    {
        lock (_gate)
        {
            if (_entries.TryGetValue(sessionId, out var record))
                Publish(record);
        }
    }

    private void Update(string sessionId, Func<HostSessionRegistration, HostSessionRegistration> change)
    {
        lock (_gate)
        {
            if (!_entries.TryGetValue(sessionId, out var before))
                return;
            var after = change(before);
            if (after == before)
                return;
            _entries[sessionId] = after;
            Publish(after, before.Status);
            _saveRequests.Writer.TryWrite(true);
        }
    }

    private void Publish(HostSessionRegistration record, string? previousStatus = null)
    {
        var projected = Project(record);
        Publish(projected);
        if (previousStatus is not null and not HostSessionStatuses.Exited && record.Status == HostSessionStatuses.Exited)
            SessionExited?.Invoke(projected);
    }

    private void Publish(RemoteSessionSummary projected)
    {
        _broadcast.Publish(HostNotification.Create(
            HostProtocol.NotificationSessionsChanged, new RemoteSessionChange(Session: projected), HostProtocolJsonContext.Default.RemoteSessionChange));
    }

    private RemoteSessionSummary Project(HostSessionRegistration record)
    {
        var pending = _pending.CountFor(record.SessionId);
        var status = record.Status != HostSessionStatuses.Exited && pending > 0 ? HostSessionStatuses.WaitingForUser : record.Status;
        return new RemoteSessionSummary(record.SessionId, record.Kind, record.AgentKind, record.ProjectPath, record.Title,
            status, record.CreatedAt, record.LastActivityAt, pending, record.ExitCode);
    }

    private async Task SaveLoopAsync()
    {
        await foreach (var _ in _saveRequests.Reader.ReadAllAsync().ConfigureAwait(false))
            await FlushAsync().ConfigureAwait(false);
    }

    /** Persist all registration and launch facts together, including credentials, with private creation permissions. */
    public async Task FlushAsync()
    {
        await _saveGate.WaitAsync().ConfigureAwait(false);
        try
        {
            IReadOnlyList<HostSessionRegistration> records;
            lock (_gate)
                records = [.. _entries.Values];
            var json = JsonSerializer.Serialize(records, HostSessionRegistryJsonContext.Default.IReadOnlyListHostSessionRegistration);
            OwnerOnlyFiles.EnsureDirectory(Path.GetDirectoryName(_storePath)!);
            var temporary = $"{_storePath}.{Guid.NewGuid():N}.tmp";
            OwnerOnlyFiles.Write(temporary, json);
            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    if (File.Exists(_storePath))
                        File.Replace(temporary, _storePath, null);
                    else
                        File.Move(temporary, _storePath);
                    break;
                }
                catch (IOException) when (attempt < AtomicFileRetries)
                {
                    await Task.Delay(AtomicFileRetryDelay).ConfigureAwait(false);
                }
            }
        }
        finally
        {
            _saveGate.Release();
        }
    }

    private static async Task<IReadOnlyList<HostSessionRegistration>> LoadAsync(string storePath, CancellationToken cancellationToken)
    {
        if (!File.Exists(storePath))
            return [];
        await using var stream = new FileStream(storePath, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        return await JsonSerializer.DeserializeAsync(stream, HostSessionRegistryJsonContext.Default.IReadOnlyListHostSessionRegistration, cancellationToken).ConfigureAwait(false)
            ?? throw new JsonException("the host session registry must be a JSON array");
    }
}

/** Only facts needed to restart an agent; identity, mode, directory and title belong to its registration. */
public sealed record HostAgentLaunch(
    string Executable, IReadOnlyList<string> Arguments,
    IReadOnlyDictionary<string, string>? Environment = null, string? AcpSessionId = null);

/** Persisted facts only: no pending count, waiting projection, or client deletion marker. */
internal sealed record HostSessionRegistration(
    string SessionId, string Kind, string? AgentKind, string? ProjectPath, string? Title,
    string Status, long CreatedAt, long LastActivityAt, int? ExitCode, HostAgentLaunch? AgentLaunch = null)
{
    public static HostSessionRegistration From(RemoteSessionSummary record)
        => new(record.SessionId, record.Kind ?? HostSessionKinds.Harness, record.AgentKind, record.ProjectPath, record.Title,
            record.Status == HostSessionStatuses.WaitingForUser ? HostSessionStatuses.Running : record.Status,
            record.CreatedAt, record.LastActivityAt, record.ExitCode);
}

[JsonSerializable(typeof(IReadOnlyList<HostSessionRegistration>))]
internal partial class HostSessionRegistryJsonContext : JsonSerializerContext;
