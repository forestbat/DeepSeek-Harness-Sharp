using Dsh.Core;
using Dsh.Llm;
using Dsh.Runtime;

namespace Dsh.Agent;

/** Executes agent dispatches inside the local process via AgentRegistry and IPersistentAgentManager. */
public sealed class LocalAgentExecutionBackend(Context ctx) : IAgentExecutionBackend
{
    public const string LocalTargetId = "local";

    public AgentBackendKind Kind => AgentBackendKind.Local;

    public Task<IReadOnlyList<RemoteExecutionTarget>> ListTargetsAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        IReadOnlyList<RemoteExecutionTarget> targets =
        [
            new RemoteExecutionTarget(
                LocalTargetId,
                AgentBackendKind.Local,
                "Local Runtime",
                Environment.MachineName,
                Available: true),
        ];
        return Task.FromResult(targets);
    }

    public bool CanHandle(AgentDispatchRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.PreferredBackend is not (null or AgentBackendKind.Local))
            return false;
        return request.TargetId is null
            || string.Equals(request.TargetId, LocalTargetId, StringComparison.OrdinalIgnoreCase)
            || string.Equals(request.TargetId, Environment.MachineName, StringComparison.OrdinalIgnoreCase);
    }

    public async Task<IAgentExecutionHandle> DispatchAsync(AgentDispatchRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        var agent = await ResolveOrCreateAgentAsync(request, cancellationToken).ConfigureAwait(false);
        var handle = new LocalAgentExecutionHandle(ctx, agent);

        if (request.Prompt is { Length: > 0 } prompt)
        {
            await handle.SendAsync(prompt, AgentMessageIntent.Task, request.Sender, cancellationToken).ConfigureAwait(false);
            if (request.WaitForCompletion)
                await agent.WhenIdle().WaitAsync(cancellationToken).ConfigureAwait(false);
        }

        return handle;
    }

    public async Task<IAgentExecutionHandle?> AttachAsync(
        AgentId agentId,
        string? targetId = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (targetId is not null
            && !string.Equals(targetId, LocalTargetId, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(targetId, Environment.MachineName, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var agents = ctx.Get<AgentRegistry>(AgentRegistry.ServiceName, false);
        var manager = ctx.Get<IPersistentAgentManager>(IPersistentAgentManager.ServiceName, false);

        var live = (manager?.Get(agentId)?.ActiveSessionId is { } activeSid ? agents?.Get(activeSid) : null)
            ?? agents?.GetByAgent(agentId);
        if (live is not null)
            return new LocalAgentExecutionHandle(ctx, live);

        if (manager?.Get(agentId) is { BackendKind: AgentBackendKind.Local, State: not AgentLifecycleState.Disposed })
        {
            var woken = await manager.WakeAsync(agentId, cancellationToken).ConfigureAwait(false);
            return new LocalAgentExecutionHandle(ctx, woken);
        }

        return null;
    }

    private async Task<IAgent> ResolveOrCreateAgentAsync(AgentDispatchRequest request, CancellationToken cancellationToken)
    {
        var agents = ctx.Get<AgentRegistry>(AgentRegistry.ServiceName, false)
            ?? throw new InvalidOperationException("local execution backend requires the agents service");
        var manager = ctx.Get<IPersistentAgentManager>(IPersistentAgentManager.ServiceName, false);

        if (request.AgentId is { } explicitId)
        {
            if (agents.GetByAgent(explicitId) is { } existingLive)
                return existingLive;
            if (manager is not null)
            {
                if (manager.Get(explicitId) is null)
                {
                    await manager.CreateAsync(
                        new AgentProfileRegistration(
                            request.Name ?? explicitId.Value,
                            BackendKind: AgentBackendKind.Local,
                            AgentId: explicitId,
                            InitialSessionId: request.SessionId,
                            Cwd: request.Cwd,
                            AgentPreset: request.AgentPreset,
                            Metadata: request.Metadata),
                        cancellationToken).ConfigureAwait(false);
                }
                return await manager.WakeAsync(explicitId, cancellationToken).ConfigureAwait(false);
            }
            var createdWithId = await agents.Create(
                new CreateAgentOptions(
                    SessionId: request.SessionId,
                    Cwd: request.Cwd,
                    AgentPreset: request.AgentPreset,
                    AgentId: explicitId),
                cancellationToken).ConfigureAwait(false);
            return createdWithId.Agent;
        }

        if (manager is not null)
        {
            var profile = await manager.CreateAsync(
                new AgentProfileRegistration(
                    request.Name ?? $"agent-{Guid.NewGuid():N}"[..14],
                    BackendKind: AgentBackendKind.Local,
                    InitialSessionId: request.SessionId,
                    Cwd: request.Cwd,
                    AgentPreset: request.AgentPreset,
                    Metadata: request.Metadata),
                cancellationToken).ConfigureAwait(false);
            return await manager.WakeAsync(profile.AgentId, cancellationToken).ConfigureAwait(false);
        }

        var created = await agents.Create(
            new CreateAgentOptions(
                SessionId: request.SessionId,
                Cwd: request.Cwd,
                AgentPreset: request.AgentPreset),
            cancellationToken).ConfigureAwait(false);
        return created.Agent;
    }

    private sealed class LocalAgentExecutionHandle(Context ctx, IAgent agent) : IAgentExecutionHandle
    {
        public AgentId AgentId => agent.AgentId;
        public SessionId? SessionId => agent.Id;
        public AgentBackendKind BackendKind => AgentBackendKind.Local;
        public string TargetId => LocalTargetId;

        public async Task<AgentExecutionSnapshot> SendAsync(
            string message,
            AgentMessageIntent intent = AgentMessageIntent.Task,
            MessagePrincipal? sender = null,
            CancellationToken cancellationToken = default)
        {
            var bus = ctx.Get<IAgentMessageBus>(IAgentMessageBus.ServiceName, false);
            if (bus is not null)
            {
                var trust = AgentTurnAttribution.Current?.TrustLevel == MessageTrustLevel.ExternalAgent
                    ? MessageTrustLevel.ExternalAgent
                    : MessageTrustLevel.TrustedPeerAgent;
                var envelope = new AgentEnvelope(
                    $"env-{Guid.NewGuid():N}",
                    agent.AgentId,
                    agent.Id,
                    sender ?? new MessagePrincipal(PrincipalKind.System, "execution-router", "LocalAgentExecutionBackend"),
                    trust,
                    intent,
                    message,
                    CreatedAt: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
                await bus.SendAsync(envelope, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                var userMessage = MessageFactory.CreateUserText(message);
                if (intent == AgentMessageIntent.Steer)
                    agent.Steer(userMessage);
                else
                    agent.Followup(userMessage);
            }
            return await GetStatusAsync(cancellationToken).ConfigureAwait(false);
        }

        public Task<AgentExecutionSnapshot> GetStatusAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var state = agent.Status == AgentStatus.Running ? AgentLifecycleState.Active : AgentLifecycleState.Idle;
            var output = LastAssistantText(agent.Session);
            var error = LastErrorText(agent.Session);
            var snapshot = new AgentExecutionSnapshot(
                agent.AgentId,
                agent.Id,
                AgentBackendKind.Local,
                LocalTargetId,
                state,
                Completed: agent.Status == AgentStatus.Idle,
                Output: output,
                Error: error,
                UpdatedAt: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            return Task.FromResult(snapshot);
        }

        public async Task CancelAsync(CancellationToken cancellationToken = default)
        {
            agent.Cancel(new AgentCancelCause.User(), keepInbox: false);
            await agent.WhenIdle().WaitAsync(cancellationToken).ConfigureAwait(false);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        private static string? LastAssistantText(Session session)
        {
            var events = session.ResidentEvents();
            for (var index = events.Count - 1; index >= 0; index--)
            {
                if (events[index].Data is AssistantMessagePayload assistant)
                    return string.Concat(assistant.Message.Content.OfType<TextBlock>().Select(block => block.Text));
            }
            return null;
        }

        private static string? LastErrorText(Session session)
        {
            var events = session.ResidentEvents();
            for (var index = events.Count - 1; index >= 0; index--)
            {
                if (events[index].Data is TurnEndPayload { Reason: TurnEndReason.Error error })
                    return error.Failure.Message;
            }
            return null;
        }
    }
}
