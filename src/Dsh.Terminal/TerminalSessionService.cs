using Cordis;
using Dsh.Core;

namespace Dsh.Terminal;

public sealed class TerminalSessionService : Service
{
    public const string ServiceName = "terminals";

    private readonly Dictionary<string, TerminalBackend> _backends = [];
    private readonly Dictionary<string, SessionRecord> _sessions = [];
    private readonly Dictionary<IAgent, HashSet<string>> _reservedNames = [];
    private readonly Dictionary<IAgent, HashSet<PendingSpawn>> _pendingSpawns = [];
    private readonly Dictionary<IAgent, EffectHandle> _ownerCleanups = [];
    private readonly HashSet<IAgent> _disposedOwners = [];
    private readonly object _gate = new();
    private int _nextId;
    private bool _disposing;

    public TerminalSessionService(Context ctx) : base(ctx, ServiceName)
    {
        Ctx.Effect(() => (Func<Task>)DisposeAllAsync, "pty teardown");
    }

    public Action RegisterBackend(TerminalBackend backend)
    {
        if (backend.Type.Length == 0)
            throw new InvalidOperationException("pty backend type must be non-empty");
        lock (_gate)
        {
            if (_backends.ContainsKey(backend.Type))
                throw new TerminalError($"a PTY backend named \"{backend.Type}\" is already registered", TerminalErrorCodes.DuplicateBackend);
        }

        var handle = Ctx.Effect(() =>
        {
            lock (_gate)
            {
                _backends[backend.Type] = backend;
            }
            return (Action)(() =>
            {
                lock (_gate)
                {
                    if (_backends.TryGetValue(backend.Type, out var current) && ReferenceEquals(current, backend))
                        _backends.Remove(backend.Type);
                }
            });
        }, "pty.registerBackend()");
        return () => handle.Dispose();
    }

    public IReadOnlyList<string> ListBackends()
    {
        lock (_gate)
            return _backends.Keys.ToList();
    }

    public async Task<TerminalSpawnResult> Spawn(IAgent owner, TerminalSpawnRequest request, CancellationToken signal = default)
    {
        lock (_gate) AssertActive();
        signal.ThrowIfCancellationRequested();
        EnsureOwnerCleanup(owner);
        TerminalBackend backend;
        lock (_gate)
        {
            if (!_backends.TryGetValue(request.Type, out backend!))
                throw new TerminalError($"no PTY backend registered for \"{request.Type}\"", TerminalErrorCodes.NoBackend);
        }
        if (request.Name is { Length: 0 })
            throw new InvalidOperationException("PTY session name must be non-empty");
        var releaseName = ReserveName(owner, request.Name);
        var spawnReservation = ReserveSpawn(owner);
        var backendSignal = signal.CanBeCanceled
            ? CancellationTokenSource.CreateLinkedTokenSource(signal, spawnReservation.Signal).Token
            : spawnReservation.Signal;
        var sessionId = new TerminalSessionId($"pty-{Interlocked.Increment(ref _nextId)}");
        TerminalBackendSession? session = null;
        Exception? cleanupFailure = null;
        try
        {
            session = await backend.Spawn(new TerminalBackendSpawnSpec(
                sessionId,
                owner,
                request.Type,
                request.Name,
                request.Cwd,
                backendSignal));
            signal.ThrowIfCancellationRequested();
            lock (_gate)
            {
                if (_disposing)
                    throw new TerminalError("PTY service is disposing", TerminalErrorCodes.ServiceDisposing);
            }
            if (!IsLiveOwner(owner))
                throw new TerminalError("PTY owner is no longer live", TerminalErrorCodes.OwnerNotLive);
            var record = new SessionRecord
            {
                Id = sessionId.Value,
                Owner = owner,
                Name = request.Name,
                Type = request.Type,
                Session = session,
            };
            lock (_gate)
                _sessions[sessionId.Value] = record;
            return SpawnSnapshot(record, session.Motd);
        }
        catch (Exception error)
        {
            if (error is TerminalBackendCleanupError cleanupError)
                cleanupFailure = cleanupError.CleanupError;
            Exception? rollbackFailure = null;
            if (session is not null && !ContainsSession(sessionId.Value))
            {
                try
                {
                    await session.Close("PTY spawn rolled back");
                }
                catch (Exception closeError)
                {
                    rollbackFailure = closeError;
                    cleanupFailure = closeError;
                }
            }
            Exception failure = error;
            if (signal.IsCancellationRequested)
                failure = new OperationCanceledException(signal);
            else if (spawnReservation.Signal.IsCancellationRequested)
                failure = spawnReservation.AbortError is { } abortError
                    ? abortError
                    : new OperationCanceledException(spawnReservation.Signal);
            if (rollbackFailure is not null && !signal.IsCancellationRequested)
                throw new AggregateException("PTY spawn and rollback both failed", failure, rollbackFailure);
            throw failure;
        }
        finally
        {
            spawnReservation.Release(cleanupFailure);
            releaseName();
        }
    }

    public bool HasOwnerActivity(IAgent owner)
    {
        lock (_gate)
        {
            if (_pendingSpawns.TryGetValue(owner, out var pending) && pending.Count > 0)
                return true;
            return _sessions.Values.Any(record => ReferenceEquals(record.Owner, owner));
        }
    }

    public TerminalSendOperation StartSend(IAgent owner, TerminalSessionId id, TerminalSendRequest request)
    {
        var record = ExpectOwned(owner, id);
        if (record.Closing is not null)
            throw new InvalidOperationException($"PTY session {id} is closing");
        if (record.Active is not null)
        {
            if (record.Active.Done.IsCompleted)
                record.Active = null;
            else
                throw new TerminalError($"PTY session {id} already has an active send", TerminalErrorCodes.SendActive);
        }
        var operation = record.Session.StartSend(request);
        record.Active = operation;
        _ = ObserveOperationDone(record, operation);
        return operation;
    }

    public TerminalReadResult Read(IAgent owner, TerminalSessionId id, TerminalReadRequest request)
        => ExpectOwned(owner, id).Session.Read(request);

    public TerminalReadResult Read(IAgent owner, TerminalSessionId id)
        => Read(owner, id, new TerminalReadRequest());

    public Task<TerminalSignalResult> Signal(IAgent owner, TerminalSessionId id, TerminalSignal signal)
        => ExpectOwned(owner, id).Session.Signal(signal);

    public async Task<bool> Kill(IAgent owner, TerminalSessionId id, string reason = "model request")
    {
        var record = ExpectOwned(owner, id);
        if (record.Closing is not null)
        {
            await record.Closing;
            return false;
        }
        var closing = record.Session.Close(reason);
        record.Closing = closing;
        try
        {
            await closing;
            lock (_gate)
                _sessions.Remove(id.Value);
            return true;
        }
        catch (Exception)
        {
            record.Closing = null;
            throw;
        }
    }

    public IReadOnlyList<TerminalSessionSnapshot> List(IAgent owner)
    {
        lock (_gate)
            return _sessions.Values
                .Where(record => ReferenceEquals(record.Owner, owner))
                .Select(record => (TerminalSessionSnapshot)Snapshot(record))
                .ToList();
    }

    private sealed class SessionRecord
    {
        public required string Id { get; init; }
        public required IAgent Owner { get; init; }
        public string? Name { get; init; }
        public required string Type { get; init; }
        public required TerminalBackendSession Session { get; init; }
        public TerminalSendOperation? Active { get; set; }
        public Task? Closing { get; set; }
    }

    private sealed class PendingSpawn
    {
        public required IAgent Owner { get; init; }
        public required CancellationTokenSource Controller { get; init; }
        public required TaskCompletionSource Settled { get; init; }
        public Exception? CleanupFailure { get; set; }
        public TerminalError? AbortError { get; set; }
    }

    private sealed class SpawnReservation
    {
        public required PendingSpawn Pending { get; init; }
        public CancellationToken Signal => Pending.Controller.Token;
        public TerminalError? AbortError => Pending.AbortError;
        public required Action<Exception?> Release { get; init; }
    }

    private void AssertActive()
    {
        if (_disposing)
            throw new TerminalError("PTY service is disposing", TerminalErrorCodes.ServiceDisposing);
    }

    private bool IsLiveOwner(IAgent owner)
    {
        lock (_gate)
        {
            if (_disposedOwners.Contains(owner))
                return false;
        }
        var agents = Ctx.Get<AgentRegistry>(AgentRegistry.ServiceName, false);
        return agents?.Get(owner.Id) is { } current && ReferenceEquals(current, owner);
    }

    private void EnsureOwnerCleanup(IAgent owner)
    {
        if (!IsLiveOwner(owner))
            throw new TerminalError($"agent \"{owner.Id}\" is not the registered PTY owner", TerminalErrorCodes.OwnerNotLive);
        lock (_gate)
        {
            if (_ownerCleanups.ContainsKey(owner))
                return;
        }
        var handle = owner.Ctx.Effect(() => (Func<Task>)(async () =>
        {
            lock (_gate)
            {
                _disposedOwners.Add(owner);
                _ownerCleanups.Remove(owner);
            }
            await DisposeOwnedAsync(owner);
        }), "pty.ownerCleanup()");
        lock (_gate)
        {
            if (_ownerCleanups.ContainsKey(owner))
            {
                handle.Dispose();
                return;
            }
            _ownerCleanups[owner] = handle;
        }
    }

    private Action ReserveName(IAgent owner, string? name)
    {
        if (name is null)
            return () => { };
        lock (_gate)
        {
            if (_sessions.Values.Any(record => ReferenceEquals(record.Owner, owner) && record.Name == name))
                throw new TerminalError($"PTY session name \"{name}\" already exists for this owner", TerminalErrorCodes.DuplicateName);
            if (!_reservedNames.TryGetValue(owner, out var reserved))
            {
                reserved = [];
                _reservedNames[owner] = reserved;
            }
            if (reserved.Contains(name))
                throw new TerminalError($"PTY session name \"{name}\" is already being created", TerminalErrorCodes.DuplicateName);
            reserved.Add(name);
            return () =>
            {
                lock (_gate)
                {
                    reserved.Remove(name);
                    if (reserved.Count == 0)
                        _reservedNames.Remove(owner);
                }
            };
        }
    }

    private SpawnReservation ReserveSpawn(IAgent owner)
    {
        var controller = new CancellationTokenSource();
        var settled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var pending = new PendingSpawn
        {
            Owner = owner,
            Controller = controller,
            Settled = settled,
        };
        lock (_gate)
        {
            if (!_pendingSpawns.TryGetValue(owner, out var owned))
            {
                owned = [];
                _pendingSpawns[owner] = owned;
            }
            owned.Add(pending);
        }
        var reservation = new SpawnReservation
        {
            Pending = pending,
            Release = cleanupFailure =>
            {
                pending.CleanupFailure = cleanupFailure;
                if (cleanupFailure is null)
                    RemovePendingSpawn(pending);
                pending.Settled.TrySetResult();
            },
        };
        return reservation;
    }

    private void RemovePendingSpawn(PendingSpawn pending)
    {
        lock (_gate)
        {
            if (!_pendingSpawns.TryGetValue(pending.Owner, out var owned))
                return;
            owned.Remove(pending);
            if (owned.Count == 0)
                _pendingSpawns.Remove(pending.Owner);
        }
    }

    private async Task AbortPendingSpawnsAsync(IAgent? owner, TerminalError reason)
    {
        List<PendingSpawn> pending;
        lock (_gate)
        {
            pending = owner is null
                ? _pendingSpawns.Values.SelectMany(owned => owned).ToList()
                : _pendingSpawns.TryGetValue(owner, out var owned) ? [..owned] : [];
        }
        foreach (var spawn in pending)
        {
            spawn.AbortError = reason;
            spawn.Controller.Cancel();
        }
        await Task.WhenAll(pending.Select(spawn => spawn.Settled.Task));
        var failures = pending
            .Select(spawn => spawn.CleanupFailure)
            .Where(failure => failure is not null)
            .Cast<Exception>()
            .ToList();
        foreach (var spawn in pending)
            RemovePendingSpawn(spawn);
        if (failures.Count > 0)
            throw new AggregateException("failed to roll back unpublished PTY setup", failures);
    }

    private SessionRecord ExpectOwned(IAgent owner, TerminalSessionId id)
    {
        lock (_gate)
        {
            if (!_sessions.TryGetValue(id.Value, out var record))
                throw new TerminalError($"unknown PTY session {id}", TerminalErrorCodes.NoSession);
            if (!ReferenceEquals(record.Owner, owner))
                throw new TerminalError($"PTY session {id} belongs to another agent", TerminalErrorCodes.ForeignSession);
            return record;
        }
    }

    private bool ContainsSession(string id)
    {
        lock (_gate)
            return _sessions.ContainsKey(id);
    }

    private TerminalSessionSnapshot Snapshot(SessionRecord record)
        => new(new TerminalSessionId(record.Id), record.Name, record.Type, record.Session.Pid, record.Session.Status());

    private TerminalSpawnResult SpawnSnapshot(SessionRecord record, string motd)
        => new(new TerminalSessionId(record.Id), record.Name, record.Type, record.Session.Pid, record.Session.Status(), motd);

    private async Task ObserveOperationDone(SessionRecord record, TerminalSendOperation operation)
    {
        try
        {
            await operation.Done;
        }
        catch
        {
        }
        finally
        {
            if (ReferenceEquals(record.Active, operation))
                record.Active = null;
        }
    }

    private async Task AbortAndCloseAsync(IAgent? owner, TerminalError abortReason, string closeReason)
    {
        var failures = new List<Exception>();
        try
        {
            await AbortPendingSpawnsAsync(owner, abortReason);
        }
        catch (Exception error)
        {
            failures.Add(error);
        }
        List<SessionRecord> records;
        lock (_gate)
            records = _sessions.Values.Where(record => owner is null || ReferenceEquals(record.Owner, owner)).ToList();
        try
        {
            await CloseRecordsAsync(records, closeReason);
        }
        catch (Exception error)
        {
            failures.Add(error);
        }
        if (failures.Count > 0)
            throw new AggregateException("failed to clean up PTY lifecycle", failures);
    }

    private async Task DisposeOwnedAsync(IAgent owner)
    {
        try
        {
            await AbortAndCloseAsync(
                owner,
                new TerminalError("PTY owner is no longer live", TerminalErrorCodes.OwnerNotLive),
                "PTY owner disposed");
        }
        finally
        {
            lock (_gate)
                _reservedNames.Remove(owner);
        }
    }

    private async Task DisposeAllAsync()
    {
        lock (_gate)
            _disposing = true;
        try
        {
            await AbortAndCloseAsync(
                null,
                new TerminalError("PTY service is disposing", TerminalErrorCodes.ServiceDisposing),
                "PTY service disposed");
        }
        finally
        {
            List<EffectHandle> cleanups;
            lock (_gate)
            {
                _backends.Clear();
                _reservedNames.Clear();
                _pendingSpawns.Clear();
                cleanups = [.._ownerCleanups.Values];
                _ownerCleanups.Clear();
            }
            await Task.WhenAll(cleanups.Select(cleanup => cleanup.DisposeAsync()));
        }
    }

    private async Task CloseRecordsAsync(IReadOnlyList<SessionRecord> records, string reason)
    {
        await Task.WhenAll(records.Select<SessionRecord, Task>(async record =>
        {
            var closing = record.Closing ?? record.Session.Close(reason);
            record.Closing = closing;
            try
            {
                await closing;
                lock (_gate)
                    _sessions.Remove(record.Id);
            }
            catch (Exception)
            {
                if (ReferenceEquals(record.Closing, closing))
                    record.Closing = null;
                throw;
            }
        }));
    }
}
