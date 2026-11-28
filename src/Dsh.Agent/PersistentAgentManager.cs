using System.Collections.Concurrent;
using Dsh.Boot;
using Dsh.Core;
using Dsh.Llm;
using Dsh.Runtime;
using Dsh.Runtime.Events;

namespace Dsh.Agent;

/** Manages long-lived persistent agent identities, suspension, wake/resume, and multi-session attachment. */
public sealed class PersistentAgentManager : Service, IPersistentAgentManager, IDisposable
{
    private readonly Lock _fallbackGate = new();
    private readonly Dictionary<AgentId, AgentProfile> _fallbackProfiles = [];
    private readonly ConcurrentDictionary<AgentId, SemaphoreSlim> _agentLocks = [];
    private readonly ConcurrentDictionary<AgentId, byte> _suspending = [];
    private readonly Func<bool> _disposeSubscription;

    public PersistentAgentManager(Context ctx) : base(ctx, IPersistentAgentManager.ServiceName)
    {
        _disposeSubscription = ctx.On<AgentDisposedNotification>(notification => OnAgentDisposed(notification));
    }

    public void Dispose()
    {
        _disposeSubscription();
    }

    public Task<AgentProfile> CreateAsync(AgentProfileRegistration registration, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(registration);
        ArgumentException.ThrowIfNullOrWhiteSpace(registration.Name);
        cancellationToken.ThrowIfCancellationRequested();

        var agentId = registration.AgentId ?? AgentId.New();
        if (Get(agentId) is not null)
            throw new InvalidOperationException($"agent profile \"{agentId}\" already exists");

        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var agents = Ctx.Get<AgentRegistry>(AgentRegistry.ServiceName, false);
        var state = AgentLifecycleState.Suspended;
        List<SessionId> sessionIds = [];
        SessionId? activeSessionId = null;

        if (registration.InitialSessionId is { } initialSessionId)
        {
            activeSessionId = initialSessionId;
            sessionIds.Add(initialSessionId);
            if (agents?.Get(initialSessionId) is { } live)
            {
                state = live.Status == AgentStatus.Running
                    ? AgentLifecycleState.Active
                    : AgentLifecycleState.Idle;
            }
        }

        var profile = new AgentProfile(
            agentId,
            registration.Name,
            registration.Description,
            registration.BackendKind,
            activeSessionId,
            sessionIds,
            state,
            registration.TargetMachine,
            registration.TargetUri,
            registration.Cwd,
            registration.Options,
            registration.AgentPreset,
            now,
            now,
            registration.Capabilities,
            registration.Metadata);

        SaveProfile(profile);
        return Task.FromResult(profile);
    }

    public AgentProfile? Get(AgentId id)
    {
        if (Ctx.Get<IAgentDirectoryStore>(IAgentDirectoryStore.ServiceName, false) is { } store)
            return store.Get(id);
        lock (_fallbackGate)
            return _fallbackProfiles.GetValueOrDefault(id);
    }

    public IReadOnlyList<AgentProfile> List()
    {
        if (Ctx.Get<IAgentDirectoryStore>(IAgentDirectoryStore.ServiceName, false) is { } store)
            return store.List();
        lock (_fallbackGate)
            return [.. _fallbackProfiles.Values.OrderBy(profile => profile.CreatedAt)];
    }

    public async Task<IAgent> WakeAsync(AgentId id, CancellationToken cancellationToken = default)
    {
        var gate = LockFor(id);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var profile = Get(id)
                ?? throw new InvalidOperationException($"agent profile \"{id}\" not found");
            if (profile.State == AgentLifecycleState.Disposed)
                throw new InvalidOperationException($"agent profile \"{id}\" is disposed");

            var agents = Ctx.Get<AgentRegistry>(AgentRegistry.ServiceName, false)
                ?? throw new InvalidOperationException("persistent agent manager requires the agents service");
            var bus = Ctx.Get<IAgentMessageBus>(IAgentMessageBus.ServiceName, false);

            var live = (profile.ActiveSessionId is { } activeSid ? agents.Get(activeSid) : null)
                ?? agents.GetByAgent(id);
            if (live is not null)
            {
                UpdateProfileOnWake(profile, live);
                if (bus is not null)
                    await bus.FlushOfflineAsync(id, cancellationToken).ConfigureAwait(false);
                return live;
            }

            var woken = await OpenSessionAgentAsync(agents, profile, cancellationToken).ConfigureAwait(false);
            var updated = UpdateProfileOnWake(profile, woken);
            if (bus is not null)
            {
                await bus.FlushOfflineAsync(id, cancellationToken).ConfigureAwait(false);
                await bus.PublishEventAsync(
                    new AgentRuntimeEvent(
                        $"evt-{Guid.NewGuid():N}",
                        id,
                        woken.Id,
                        AgentRuntimeEventKind.AgentWoken,
                        $"Agent '{updated.Name}' woken",
                        DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()),
                    cancellationToken).ConfigureAwait(false);
            }
            return woken;
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<IAgent> WakeSessionAsync(AgentId id, SessionId sessionId, CancellationToken cancellationToken = default)
    {
        var gate = LockFor(id);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var profile = Get(id)
                ?? throw new InvalidOperationException($"agent profile \"{id}\" not found");
            if (profile.State == AgentLifecycleState.Disposed)
                throw new InvalidOperationException($"agent profile \"{id}\" is disposed");

            var agents = Ctx.Get<AgentRegistry>(AgentRegistry.ServiceName, false)
                ?? throw new InvalidOperationException("persistent agent manager requires the agents service");
            var bus = Ctx.Get<IAgentMessageBus>(IAgentMessageBus.ServiceName, false);

            if (agents.Get(sessionId) is { } live)
            {
                UpdateProfileOnWake(profile, live, preserveExistingActive: true, agents);
                if (bus is not null)
                    await bus.FlushOfflineAsync(id, cancellationToken).ConfigureAwait(false);
                return live;
            }

            var persistence = Ctx.Get<ISessionPersistence>(ISessionPersistence.ServiceName, false);
            if (persistence?.Stat(sessionId) is null)
                throw new InvalidOperationException($"session \"{sessionId}\" for agent \"{id}\" is not persisted");

            var resolvedOptions = ResolveAgentOptions(profile.Options);
            var resumed = await agents.Resume(
                new ResumeAgentOptions(sessionId, resolvedOptions, AgentId: profile.AgentId),
                cancellationToken).ConfigureAwait(false);
            var woken = resumed.Agent;
            var updated = UpdateProfileOnWake(profile, woken, preserveExistingActive: true, agents);
            if (bus is not null)
            {
                await bus.FlushOfflineAsync(id, cancellationToken).ConfigureAwait(false);
                await bus.PublishEventAsync(
                    new AgentRuntimeEvent(
                        $"evt-{Guid.NewGuid():N}",
                        id,
                        woken.Id,
                        AgentRuntimeEventKind.AgentWoken,
                        $"Agent '{updated.Name}' woken",
                        DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()),
                    cancellationToken).ConfigureAwait(false);
            }
            return woken;
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task SuspendAsync(AgentId id, CancellationToken cancellationToken = default)
    {
        var currentAgents = Ctx.Get<AgentRegistry>(AgentRegistry.ServiceName, false);
        if (currentAgents?.CurrentInitiator() is { } initiator && initiator.AgentId == id)
            throw new InvalidOperationException($"agent \"{id}\" cannot suspend itself from within its own running turn");

        var gate = LockFor(id);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var profile = Get(id)
                ?? throw new InvalidOperationException($"agent profile \"{id}\" not found");
            var agents = Ctx.Get<AgentRegistry>(AgentRegistry.ServiceName, false);
            _suspending[id] = 0;
            try
            {
                if (agents is not null)
                {
                    foreach (var live in agents.ListByAgent(id))
                    {
                        if (live.Status == AgentStatus.Running)
                        {
                            live.Cancel(new AgentCancelCause.Disposed(), keepInbox: true);
                            await live.WhenIdle().ConfigureAwait(false);
                        }
                        agents.Remove(live.Id);
                    }
                }
            }
            finally
            {
                _suspending.TryRemove(id, out _);
            }

            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var updated = profile with
            {
                State = AgentLifecycleState.Suspended,
                UpdatedAt = now,
            };
            SaveProfile(updated);

            if (Ctx.Get<IAgentMessageBus>(IAgentMessageBus.ServiceName, false) is { } bus)
            {
                await bus.PublishEventAsync(
                    new AgentRuntimeEvent(
                        $"evt-{Guid.NewGuid():N}",
                        id,
                        updated.ActiveSessionId,
                        AgentRuntimeEventKind.AgentSuspended,
                        $"Agent '{updated.Name}' suspended",
                        now),
                    cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<AgentProfile> AttachSessionAsync(AgentId id, SessionId sessionId, CancellationToken cancellationToken = default)
    {
        var gate = LockFor(id);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var profile = Get(id)
                ?? throw new InvalidOperationException($"agent profile \"{id}\" not found");
            List<SessionId> sessions = [.. profile.SessionIds];
            if (!sessions.Contains(sessionId))
                sessions.Add(sessionId);

            var agents = Ctx.Get<AgentRegistry>(AgentRegistry.ServiceName, false);
            var live = agents?.Get(sessionId);
            var nextState = live is not null
                ? (live.Status == AgentStatus.Running ? AgentLifecycleState.Active : AgentLifecycleState.Idle)
                : profile.State;

            var updated = profile with
            {
                ActiveSessionId = sessionId,
                SessionIds = sessions,
                State = nextState,
                UpdatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            };
            SaveProfile(updated);
            return updated;
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<bool> DeleteAsync(AgentId id, CancellationToken cancellationToken = default)
    {
        var currentAgents = Ctx.Get<AgentRegistry>(AgentRegistry.ServiceName, false);
        if (currentAgents?.CurrentInitiator() is { } initiator && initiator.AgentId == id)
            throw new InvalidOperationException($"agent \"{id}\" cannot delete itself from within its own running turn");

        var gate = LockFor(id);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (Get(id) is null)
                return false;
            var agents = Ctx.Get<AgentRegistry>(AgentRegistry.ServiceName, false);
            _suspending[id] = 0;
            try
            {
                if (agents is not null)
                {
                    foreach (var live in agents.ListByAgent(id))
                    {
                        if (live.Status == AgentStatus.Running)
                        {
                            live.Cancel(new AgentCancelCause.Disposed(), keepInbox: false);
                            await live.WhenIdle().ConfigureAwait(false);
                        }
                        agents.Remove(live.Id);
                    }
                }
            }
            finally
            {
                _suspending.TryRemove(id, out _);
            }

            _ = Ctx.Get<IOfflineMailboxStore>(IOfflineMailboxStore.ServiceName, false)?.Drain(id);
            return DeleteProfile(id);
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<IAgent> OpenSessionAgentAsync(AgentRegistry agents, AgentProfile profile, CancellationToken cancellationToken)
    {
        var persistence = Ctx.Get<ISessionPersistence>(ISessionPersistence.ServiceName, false);
        var sessions = Ctx.Get<SessionStore>(SessionStore.ServiceName, false);
        var resolvedOptions = ResolveAgentOptions(profile.Options);

        if (profile.ActiveSessionId is { } targetSessionId)
        {
            if (persistence?.Stat(targetSessionId) is not null)
            {
                var resumed = await agents.Resume(
                    new ResumeAgentOptions(targetSessionId, resolvedOptions, AgentId: profile.AgentId),
                    cancellationToken).ConfigureAwait(false);
                return resumed.Agent;
            }
            if (sessions?.Get(targetSessionId) is null)
            {
                var createdWithId = await agents.Create(
                    new CreateAgentOptions(
                        SessionId: targetSessionId,
                        Cwd: profile.Cwd,
                        AgentOptions: resolvedOptions,
                        AgentPreset: profile.AgentPreset,
                        AgentId: profile.AgentId),
                    cancellationToken).ConfigureAwait(false);
                return createdWithId.Agent;
            }
        }

        var created = await agents.Create(
            new CreateAgentOptions(
                Cwd: profile.Cwd,
                AgentOptions: resolvedOptions,
                AgentPreset: profile.AgentPreset,
                AgentId: profile.AgentId),
            cancellationToken).ConfigureAwait(false);
        return created.Agent;
    }

    private AgentOptions? ResolveAgentOptions(AgentOptions? configured)
    {
        if (configured is { Provider.Length: > 0, Model.Length: > 0 })
            return configured;
        var harnessOptions = Ctx.GetProp("harnessOptions") as HarnessOptions;
        var settingsDefault = harnessOptions is not null
            ? HarnessSettings.Load(harnessOptions.Home).ResolveDefaultModel()
            : null;
        var provider = !string.IsNullOrWhiteSpace(configured?.Provider)
            ? configured.Provider
            : harnessOptions?.Provider ?? settingsDefault?.Provider;
        var model = !string.IsNullOrWhiteSpace(configured?.Model)
            ? configured.Model
            : harnessOptions?.Model ?? settingsDefault?.Model;
        if (string.IsNullOrWhiteSpace(provider) && string.IsNullOrWhiteSpace(model))
            return configured;
        return new AgentOptions(provider, model, configured?.ReasoningEffort, configured?.MaxTokens);
    }

    private AgentProfile UpdateProfileOnWake(
        AgentProfile profile,
        IAgent agent,
        bool preserveExistingActive = false,
        AgentRegistry? agents = null)
    {
        List<SessionId> sessions = [.. profile.SessionIds];
        if (!sessions.Contains(agent.Id))
            sessions.Add(agent.Id);
        var activeSessionId = preserveExistingActive
            ? profile.ActiveSessionId ?? agent.Id
            : agent.Id;
        var isRunning = agents is not null
            ? agents.ListByAgent(profile.AgentId).Any(live => live.Status == AgentStatus.Running)
            : agent.Status == AgentStatus.Running;
        var updated = profile with
        {
            ActiveSessionId = activeSessionId,
            SessionIds = sessions,
            State = isRunning ? AgentLifecycleState.Active : AgentLifecycleState.Idle,
            UpdatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        };
        SaveProfile(updated);
        return updated;
    }

    private void OnAgentDisposed(AgentDisposedNotification notification)
    {
        var agent = notification.Agent;
        if (_suspending.ContainsKey(agent.AgentId))
            return;
        if (Get(agent.AgentId) is not { } profile)
            return;
        if (profile.State is AgentLifecycleState.Suspended or AgentLifecycleState.Disposed)
            return;
        var agents = Ctx.Get<AgentRegistry>(AgentRegistry.ServiceName, false);
        if (agents?.ListByAgent(agent.AgentId).Count > 0)
            return;
        SaveProfile(profile with
        {
            State = AgentLifecycleState.Suspended,
            UpdatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        });
    }

    private SemaphoreSlim LockFor(AgentId id) => _agentLocks.GetOrAdd(id, static _ => new SemaphoreSlim(1, 1));

    private void SaveProfile(AgentProfile profile)
    {
        if (Ctx.Get<IAgentDirectoryStore>(IAgentDirectoryStore.ServiceName, false) is { } store)
        {
            store.Save(profile);
            return;
        }
        lock (_fallbackGate)
            _fallbackProfiles[profile.AgentId] = profile;
    }

    private bool DeleteProfile(AgentId id)
    {
        if (Ctx.Get<IAgentDirectoryStore>(IAgentDirectoryStore.ServiceName, false) is { } store)
            return store.Delete(id);
        lock (_fallbackGate)
            return _fallbackProfiles.Remove(id);
    }
}
