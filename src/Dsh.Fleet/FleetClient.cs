using Dsh.RemoteHost;

namespace Dsh.Fleet;

/** A session together with the machine it runs on. */
public sealed record FleetSession(string Machine, RemoteSessionSummary Session);

/** Sessions of one project directory on one machine; ProjectPath is null for sessions without a project. */
public sealed record FleetProjectGroup(string? ProjectPath, IReadOnlyList<RemoteSessionSummary> Sessions);

/** All sessions of one machine, grouped by project. */
public sealed record FleetMachineGroup(string Machine, IReadOnlyList<FleetProjectGroup> Projects);

/** An approval or question waiting for an answer: exactly one of Approval and Question is set. */
public sealed record FleetPendingItem(string Machine, string RequestId, string SessionId, string Kind, RemoteApprovalRequest? Approval, RemoteQuestionRequest? Question);

/** What a client shows for a machine: its connection state, the host's release and the last failure or deployment step. */
public sealed record FleetMachineStatus(string Machine, string Kind, string Target, MachineConnectionState State, string? HostVersion, string? Detail);

/**
 * The aggregated view of every machine, independent of any UI framework.
 * It owns one MachineConnection per machine and folds their sessions, pending requests and states into flat and grouped snapshots.
 * Changed is raised from background threads whenever any machine's state, sessions or pending requests change; UIs marshal it themselves and re-read the snapshots.
 */
public sealed class FleetClient : IAsyncDisposable
{
    private readonly List<MachineConnection> _machines;
    private readonly object _disposeSync = new();
    private Task? _disposeTask;
    private readonly Task<IAsyncDisposable>? _outboundLease;
    private Task? _startTask;
    private bool _disposed;

    public FleetClient(IEnumerable<MachineSpec> machines, FleetOptions options)
    {
        _outboundLease = options.AcquireOutboundRoute?.Invoke(CancellationToken.None).AsTask();
        _machines = [.. machines.Select(machine => new MachineConnection(machine, options))];
        foreach (var machine in _machines)
        {
            machine.StateChanged += OnMachineChanged;
            machine.SessionsChanged += OnMachineChanged;
            machine.PendingChanged += OnMachineChanged;
        }
    }

    public event Action? Changed;
    internal event Action<FleetClient>? Disposed;

    public IReadOnlyList<MachineConnection> Machines => _machines;

    public MachineConnection? Find(string name) => _machines.FirstOrDefault(machine => string.Equals(machine.Machine.Name, name, StringComparison.Ordinal));

    public void Start()
    {
        lock (_disposeSync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _startTask ??= StartMachinesAsync();
        }
    }

    private async Task StartMachinesAsync()
    {
        if (_outboundLease is not null)
            await _outboundLease.ConfigureAwait(false);
        lock (_disposeSync)
        {
            if (_disposed)
                return;
            foreach (var machine in _machines)
                machine.Start();
        }
    }

    /** Completes once every machine has either connected or failed its first attempt. */
    public async Task WaitSettledAsync(CancellationToken cancellationToken)
    {
        Task? started;
        lock (_disposeSync)
            started = _startTask;
        if (started is not null)
            await started.WaitAsync(cancellationToken).ConfigureAwait(false);
        await Task.WhenAll(_machines.Select(machine => machine.WaitSettledAsync(cancellationToken))).ConfigureAwait(false);
    }

    public IReadOnlyList<FleetMachineStatus> Statuses()
        => [.. _machines.Select(machine => new FleetMachineStatus(
            machine.Machine.Name, machine.Machine.Kind, machine.Machine.Target, machine.State, machine.Info?.HostVersion, machine.Detail))];

    /** Every session, ordered by machine (in configuration order), then project, then creation time. */
    public IReadOnlyList<FleetSession> Sessions()
        => [.. _machines.SelectMany(machine => machine.Sessions
            .OrderBy(session => session.ProjectPath ?? "", StringComparer.Ordinal)
            .ThenBy(session => session.CreatedAt)
            .Select(session => new FleetSession(machine.Machine.Name, session)))];

    /** Machine → project → sessions, the dashboard's grouping. */
    public IReadOnlyList<FleetMachineGroup> Groups()
        => [.. _machines.Select(machine => new FleetMachineGroup(
            machine.Machine.Name,
            [.. machine.Sessions
                .GroupBy(session => session.ProjectPath)
                .OrderBy(group => group.Key ?? "", StringComparer.Ordinal)
                .Select(group => new FleetProjectGroup(group.Key, [.. group.OrderBy(session => session.CreatedAt)]))]))];

    public IReadOnlyList<FleetPendingItem> Pending()
        => [.. _machines.SelectMany(machine => PendingOf(machine.Machine.Name, machine.Pending))];

    public ValueTask DisposeAsync()
    {
        lock (_disposeSync)
        {
            _disposed = true;
            return new ValueTask(_disposeTask ??= DisposeMachinesAsync());
        }
    }

    private async Task DisposeMachinesAsync()
    {
        List<Exception>? errors = null;
        foreach (var machine in _machines)
        {
            machine.StateChanged -= OnMachineChanged;
            machine.SessionsChanged -= OnMachineChanged;
            machine.PendingChanged -= OnMachineChanged;
            try
            {
                await machine.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception error)
            {
                (errors ??= []).Add(error);
            }
        }
        try
        {
            if (errors is not null)
                throw new AggregateException(errors);
        }
        finally
        {
            try
            {
                if (_outboundLease is not null)
                    await (await _outboundLease.ConfigureAwait(false)).DisposeAsync().ConfigureAwait(false);
            }
            finally
            {
                Disposed?.Invoke(this);
            }
        }
    }

    private static IEnumerable<FleetPendingItem> PendingOf(string machine, RemotePendingList pending)
        => pending.Approvals
            .Select(approval => new FleetPendingItem(machine, approval.RequestId, approval.SessionId, PendingKinds.Approval, approval, null))
            .Concat(pending.Questions.Select(question => new FleetPendingItem(machine, question.RequestId, question.SessionId, PendingKinds.Question, null, question)));

    private void OnMachineChanged(MachineConnection machine) => Changed?.Invoke();
}

