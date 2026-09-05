using Cordis;
using Dsh.Core;

namespace Dsh.Jobs;

public sealed record LocalJobsConfig
{
    public int MaxConcurrentJobsPerOwner { get; init; } = DefaultMaxConcurrentJobsPerOwner;

    public const int DefaultMaxConcurrentJobsPerOwner = 10;
}

public sealed class LocalJobsService : JobsService
{
    private sealed class TrackedTask
    {
        public required string Id { get; init; }
        public required string Kind { get; init; }
        public required string Label { get; init; }
        public int? OutputLimitBytes { get; init; }
        public IAgent? Owner { get; init; }
        public required Action<string?> Cancel { get; init; }
        public Func<string>? ReadOutput { get; init; }
        public JobStatus Status { get; set; }
        public string? Detail { get; set; }
        public string? Output { get; set; }
        public required long StartedAt { get; init; }
        public long? FinishedAt { get; set; }
        public bool Reported { get; set; }
        public required TaskCompletionSource Settled { get; init; }
        public int Waiters { get; set; }
        public List<TaskCompletionSource> WaitResolvers { get; } = [];
    }

    private sealed class JobLayer
    {
        public AnonymousEntries<object> Controllers { get; } = new();
        public AnonymousEntries<JobDoneListener> Listeners { get; } = new();
        public AnonymousEntries<JobsChangedListener> Changed { get; } = new();
    }

    private readonly int _maxConcurrentJobsPerOwner;
    private readonly Dictionary<string, TrackedTask> _store = [];
    private readonly Dictionary<string, int> _counters = [];
    private readonly ScopedLayers<JobLayer> _layers;
    private readonly Dictionary<IAgent, EffectHandle> _ownerCleanups = [];
    private readonly object _gate = new();
    private bool _listenersClosed;

    public LocalJobsService(Context ctx, LocalJobsConfig? config = null) : base(ctx)
    {
        _maxConcurrentJobsPerOwner = (config ?? new LocalJobsConfig()).MaxConcurrentJobsPerOwner;
        if (_maxConcurrentJobsPerOwner < 1)
            throw new ArgumentException("maxConcurrentJobsPerOwner must be a positive integer");
        _layers = new ScopedLayers<JobLayer>(_ => new JobLayer(), () => { });
        ctx.Effect(() => (Func<Task>)DisposeAllAsync, "jobs teardown");
    }

    public override string Start(JobStart spec)
    {
        JobHooks hooks;
        TrackedTask job;
        lock (_gate)
        {
            if (!ServesOwner(spec.Owner))
                throw new InvalidOperationException("background jobs unavailable: no job controller serves this agent (load @deepseek-ai/dsh-tool-jobs in its composition)");
            if (spec.Kind.Length == 0)
                throw new ArgumentException("invalid job kind: expected a non-empty string");
            if (spec.Label.Length == 0)
                throw new ArgumentException("invalid job label: expected a non-empty string");
            if (spec.OutputLimitBytes is <= 0)
                throw new ArgumentException($"invalid outputLimitBytes: expected a positive safe integer, got {spec.OutputLimitBytes}");
            if (spec.Owner is not null)
                EnsureOwnerCleanup(spec.Owner);
            var active = ActiveCount(spec.Owner);
            if (active >= _maxConcurrentJobsPerOwner)
                throw new InvalidOperationException(
                    $"background job limit reached for this owner (limit: {_maxConcurrentJobsPerOwner}); use job_kill to stop an unneeded job, wait for it to finish, then retry");
            hooks = spec.Run();
            var count = _counters.GetValueOrDefault(spec.Kind) + 1;
            _counters[spec.Kind] = count;
            var id = $"{spec.Kind}-{count}";
            job = new TrackedTask
            {
                Id = id,
                Kind = spec.Kind,
                Label = spec.Label,
                OutputLimitBytes = spec.OutputLimitBytes,
                Owner = spec.Owner,
                Cancel = hooks.Cancel,
                ReadOutput = hooks.ReadOutput,
                Status = JobStatus.Running,
                StartedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                Reported = false,
                Settled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously),
            };
            _store.Add(id, job);
        }
        _ = ObserveDone(job, hooks.Done);
        NotifyChanged(job.Owner);
        return job.Id;
    }

    public override IReadOnlyList<JobSnapshot> List(IAgent? caller = null)
    {
        lock (_gate)
            return _store.Values
                .Where(job => job.Owner is null || job.Owner.Id == caller?.Id)
                .Select(Snapshot)
                .ToList();
    }

    public override JobSnapshot Get(string id, IAgent? caller = null)
    {
        lock (_gate)
        {
            var job = Expect(id);
            AssertAccess(job, caller);
            return Snapshot(job);
        }
    }

    public override JobRead Read(string id, IAgent? caller = null)
    {
        lock (_gate)
        {
            var job = Expect(id);
            AssertAccess(job, caller);
            var text = job.ReadOutput is not null
                ? job.ReadOutput()
                : JobStatusWire.IsTerminal(job.Status) ? job.Output ?? "" : "";
            if (JobStatusWire.IsTerminal(job.Status)) job.Reported = true;
            return new JobRead(text, Snapshot(job));
        }
    }

    public override JobKillOutcome Kill(string id, IAgent? caller = null, string? reason = null)
    {
        IAgent? owner;
        lock (_gate)
        {
            var job = Expect(id);
            AssertAccess(job, caller);
            if (JobStatusWire.IsTerminal(job.Status))
            {
                job.Reported = true;
                return JobKillOutcome.AlreadyFinished;
            }
            job.Cancel(reason);
            job.Status = JobStatus.Stopping;
            job.Reported = true;
            owner = job.Owner;
        }
        NotifyChanged(owner);
        return JobKillOutcome.Requested;
    }

    public override async Task<JobSnapshot> WaitAsync(string id, double timeoutMs, IAgent? caller = null, CancellationToken signal = default)
    {
        TrackedTask job;
        lock (_gate)
        {
            job = Expect(id);
            AssertAccess(job, caller);
        }
        if (!double.IsFinite(timeoutMs) || timeoutMs <= 0)
            throw new ArgumentException($"invalid wait timeout: expected a positive number of milliseconds, got {timeoutMs}");
        if (!JobStatusWire.IsTerminal(job.Status))
        {
            if (signal.IsCancellationRequested)
                throw new InvalidOperationException("wait aborted");
            var resolver = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_gate)
            {
                job.Waiters += 1;
                job.WaitResolvers.Add(resolver);
            }
            try
            {
                var delay = Task.Delay(TimeSpan.FromMilliseconds(timeoutMs), signal);
                var finished = await Task.WhenAny(resolver.Task, delay);
                if (finished == delay)
                {
                    if (delay.IsCanceled)
                        throw new InvalidOperationException("wait aborted");
                }
                else
                {
                    await resolver.Task;
                }
            }
            finally
            {
                lock (_gate)
                {
                    job.Waiters -= 1;
                    job.WaitResolvers.Remove(resolver);
                }
            }
        }
        lock (_gate)
        {
            if (JobStatusWire.IsTerminal(job.Status)) job.Reported = true;
            return Snapshot(job);
        }
    }

    public override IDisposable OnJobDone(JobDoneListener listener)
        => _layers.Effect(Ctx, null,
            layer => layer.Listeners.Append(listener),
            layer => layer.Listeners.Remove(listener),
            notify: false);

    public override IDisposable OnJobsChanged(JobsChangedListener listener)
        => _layers.Effect(Ctx, null,
            layer => layer.Changed.Append(listener),
            layer => layer.Changed.Remove(listener),
            notify: false);

    public override IDisposable AttachController(string name)
    {
        var token = new object();
        return _layers.Effect(Ctx, null,
            layer => layer.Controllers.Append(token),
            layer => layer.Controllers.Remove(token),
            notify: false);
    }

    private bool ServesOwner(IAgent? owner)
    {
        if (!_layers.Global.Controllers.IsEmpty) return true;
        return _layers.ChainLayers(owner?.ScopeKey).Any(layer => !layer.Controllers.IsEmpty);
    }

    private int ActiveCount(IAgent? owner)
    {
        var count = 0;
        foreach (var job in _store.Values)
        {
            if (ReferenceEquals(job.Owner, owner) && job.Status is JobStatus.Running or JobStatus.Stopping)
                count += 1;
        }
        return count;
    }

    private List<JobDoneListener> ListenersFor(IAgent? owner)
    {
        var result = _layers.Global.Listeners.Values.ToList();
        foreach (var layer in _layers.ChainLayers(owner?.ScopeKey))
            result.AddRange(layer.Listeners.Values);
        return result;
    }

    private List<JobsChangedListener> ChangedFor(IAgent? owner)
    {
        var result = _layers.Global.Changed.Values.ToList();
        foreach (var layer in _layers.ChainLayers(owner?.ScopeKey))
            result.AddRange(layer.Changed.Values);
        return result;
    }

    private TrackedTask Expect(string id)
        => _store.TryGetValue(id, out var job) ? job : throw new KeyNotFoundException($"unknown job {id}");

    private static void AssertAccess(TrackedTask job, IAgent? caller)
    {
        if (job.Owner is not null && job.Owner.Id != caller?.Id)
            throw new InvalidOperationException($"job {job.Id} belongs to another session");
    }

    private static JobSnapshot Snapshot(TrackedTask job) => new()
    {
        Id = job.Id,
        Kind = job.Kind,
        Label = job.Label,
        OutputLimitBytes = job.OutputLimitBytes,
        OwnerSession = job.Owner?.Id,
        Status = job.Status,
        Detail = job.Detail,
        StartedAt = job.StartedAt,
        FinishedAt = job.FinishedAt,
        Reported = job.Reported,
    };

    private void NotifyChanged(IAgent? owner)
    {
        List<JobsChangedListener> listeners;
        lock (_gate)
            listeners = ChangedFor(owner);
        foreach (var listener in listeners)
        {
            try
            {
                listener(owner);
            }
            catch (Exception error)
            {
                Ctx.Logger.Warn($"jobs: onJobsChanged listener threw: {error}");
            }
        }
    }

    private async Task ObserveDone(TrackedTask job, Task<JobOutcome> done)
    {
        try
        {
            var outcome = await done.ConfigureAwait(false);
            Settle(job, outcome);
        }
        catch (Exception error)
        {
            Ctx.Logger.Warn($"jobs: job {job.Id} producer done promise rejected (producer contract violation): {error}");
            Settle(job, new JobOutcome(JobStatus.Failed, Detail: error.ToString()));
        }
    }

    private void Settle(TrackedTask job, JobOutcome outcome)
    {
        List<TaskCompletionSource> waitResolvers;
        List<JobDoneListener> listeners;
        JobSnapshot? snapshot = null;
        lock (_gate)
        {
            if (JobStatusWire.IsTerminal(job.Status)) return;
            job.Status = outcome.Status;
            job.Detail = outcome.Detail;
            job.Output = outcome.Output;
            job.FinishedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            if (job.Waiters > 0) job.Reported = true;
            snapshot = Snapshot(job);
            waitResolvers = [..job.WaitResolvers];
            job.WaitResolvers.Clear();
            job.Settled.TrySetResult();
            listeners = _listenersClosed ? [] : ListenersFor(job.Owner);
        }
        foreach (var resolveWait in waitResolvers)
            resolveWait.TrySetResult();
        NotifyChanged(job.Owner);
        foreach (var listener in listeners)
        {
            try
            {
                listener(snapshot, job.Owner);
            }
            catch (Exception error)
            {
                Ctx.Logger.Warn($"jobs: onJobDone listener threw for {job.Id}: {error}");
            }
        }
    }

    private void EnsureOwnerCleanup(IAgent owner)
    {
        if (_ownerCleanups.ContainsKey(owner)) return;
        var agents = Ctx.Get<AgentRegistry>(AgentRegistry.ServiceName, false)
            ?? throw new InvalidOperationException("background job ownership requires the agent registry (load @deepseek-ai/dsh-agent)");
        if (!ReferenceEquals(agents.Get(owner.Id), owner))
            throw new InvalidOperationException($"agent \"{owner.Id}\" is not the registered agent instance (background job owner must be live)");
        var detach = owner.Ctx.Effect(() => (Func<Task>)(async () =>
        {
            lock (_gate) _ownerCleanups.Remove(owner);
            await DisposeOwnedAsync(owner);
        }), "jobs.ownerCleanup()");
        _ownerCleanups[owner] = detach;
    }

    private async Task DisposeOwnedAsync(IAgent owner)
    {
        List<TrackedTask> owned;
        lock (_gate)
            owned = _store.Values.Where(job => ReferenceEquals(job.Owner, owner)).ToList();
        CancelForTeardown(owned, "owner disposed");
        await Task.WhenAll(owned.Select(job => job.Settled.Task));
        lock (_gate)
        {
            foreach (var job in owned)
                _store.Remove(job.Id);
        }
        if (owned.Count > 0)
            NotifyChanged(owner);
    }

    private async Task DisposeAllAsync()
    {
        List<TrackedTask> all;
        List<EffectHandle> ownerCleanups;
        lock (_gate)
        {
            _listenersClosed = true;
            all = [.._store.Values];
        }
        CancelForTeardown(all, "jobs service disposed");
        await Task.WhenAll(all.Select(job => job.Settled.Task));
        HashSet<IAgent?> emptied;
        lock (_gate)
        {
            emptied = [..all.Select(job => job.Owner)];
            _store.Clear();
            ownerCleanups = [.._ownerCleanups.Values];
            _ownerCleanups.Clear();
        }
        foreach (var owner in emptied)
            NotifyChanged(owner);
        foreach (var cleanup in ownerCleanups)
            await cleanup.DisposeAsync();
    }

    private void CancelForTeardown(IReadOnlyList<TrackedTask> jobs, string reason)
    {
        foreach (var job in jobs)
        {
            IAgent? owner;
            lock (_gate)
            {
                if (JobStatusWire.IsTerminal(job.Status)) continue;
                job.Reported = true;
            }
            try
            {
                job.Cancel(reason);
                lock (_gate)
                    job.Status = JobStatus.Stopping;
                owner = job.Owner;
                NotifyChanged(owner);
            }
            catch (Exception error)
            {
                var detail = $"cancel threw during teardown; work may be orphaned: {error}";
                Ctx.Logger.Warn($"jobs: cancel of {job.Id} threw during teardown; job record forced failed and work may be orphaned: {error}");
                Settle(job, new JobOutcome(JobStatus.Failed, Detail: detail));
            }
        }
    }
}
