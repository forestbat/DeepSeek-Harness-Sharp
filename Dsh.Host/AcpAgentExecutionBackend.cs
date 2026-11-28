using Dsh.Core;
using Dsh.Llm;
using Dsh.RemoteHost;

namespace Dsh.Host;

/** Adapts ExternalAgentSessions / AcpAgentSession into the unified 0.5.0 IAgentExecutionBackend contract. */
internal sealed class AcpAgentExecutionBackend(ExternalAgentSessions agents, HostSessionRegistry sessions) : IAgentExecutionBackend
{
    private const string TargetPrefix = "acp:";
    private const string SessionTargetPrefix = "acp:session:";

    public AgentBackendKind Kind => AgentBackendKind.Acp;

    public async Task<IReadOnlyList<RemoteExecutionTarget>> ListTargetsAsync(CancellationToken cancellationToken = default)
    {
        var catalog = await ExternalAgentSessions.CatalogAsync(cancellationToken).ConfigureAwait(false);
        List<RemoteExecutionTarget> targets = [];
        foreach (var entry in catalog)
        {
            if (!entry.SupportsAcp)
                continue;
            targets.Add(new RemoteExecutionTarget(
                $"{TargetPrefix}{entry.AgentKind}",
                AgentBackendKind.Acp,
                entry.DisplayName,
                Environment.MachineName,
                entry.AcpPath,
                entry.Available && entry.SupportsAcp,
                entry.SupportsResume ? ["acp", "resume"] : ["acp"],
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["agentKind"] = entry.AgentKind,
                    ["requiresCommand"] = entry.RequiresCommand ? "true" : "false",
                }));
        }
        foreach (var session in sessions.List())
        {
            if (session.Kind != HostSessionKinds.Acp)
                continue;
            targets.Add(new RemoteExecutionTarget(
                $"{SessionTargetPrefix}{session.SessionId}",
                AgentBackendKind.Acp,
                session.Title ?? session.AgentKind ?? session.SessionId,
                Environment.MachineName,
                session.ProjectPath,
                session.Status != HostSessionStatuses.Exited,
                ["acp", "resume"],
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["sessionId"] = session.SessionId,
                    ["agentKind"] = session.AgentKind ?? AgentKinds.Acp,
                    ["status"] = session.Status,
                }));
        }
        return targets;
    }

    public bool CanHandle(AgentDispatchRequest request)
        => request.PreferredBackend == AgentBackendKind.Acp
            || (request.TargetId is { } targetId && targetId.StartsWith(TargetPrefix, StringComparison.OrdinalIgnoreCase))
            || (request.SessionId is { } sessionId && agents.IsAcp(sessionId.Value));

    public async Task<IAgentExecutionHandle> DispatchAsync(AgentDispatchRequest request, CancellationToken cancellationToken = default)
    {
        if (request.SessionId is { } existing && agents.IsAcp(existing.Value))
            return await DispatchExistingAsync(existing, request.AgentId, request.TargetId, request.Prompt, cancellationToken).ConfigureAwait(false);

        if (request.TargetId is { } sessionTarget && sessionTarget.StartsWith(SessionTargetPrefix, StringComparison.OrdinalIgnoreCase))
        {
            var id = SessionId.Create(sessionTarget[SessionTargetPrefix.Length..]);
            return await DispatchExistingAsync(id, request.AgentId, sessionTarget, request.Prompt, cancellationToken).ConfigureAwait(false);
        }

        var agentKind = ResolveAgentKind(request);
        var cwd = string.IsNullOrWhiteSpace(request.Cwd) ? Environment.CurrentDirectory : request.Cwd;
        var command = ResolveCustomCommand(request);
        var startRequest = new RemoteAgentStartRequest(
            agentKind,
            cwd,
            InitialPrompt: request.Prompt,
            Mode: RemoteAgentModes.Acp,
            Command: command,
            Title: request.Name);
        var started = await agents.StartAsync(startRequest, cancellationToken).ConfigureAwait(false);
        var sessionId = SessionId.Create(started.SessionId);
        var agentId = request.AgentId ?? AgentId.FromSession(sessionId);
        return new AcpExecutionHandle(agents, sessions, agentId, sessionId, $"{TargetPrefix}{agentKind}");
    }

    public Task<IAgentExecutionHandle?> AttachAsync(AgentId agentId, string? targetId = null, CancellationToken cancellationToken = default)
    {
        var candidateSessionId = targetId is { } target && target.StartsWith(SessionTargetPrefix, StringComparison.OrdinalIgnoreCase)
            ? target[SessionTargetPrefix.Length..]
            : agentId.Value;
        var summary = sessions.Get(candidateSessionId);
        if (summary is null || summary.Kind != HostSessionKinds.Acp)
            return Task.FromResult<IAgentExecutionHandle?>(null);

        IAgentExecutionHandle handle = new AcpExecutionHandle(
            agents,
            sessions,
            agentId,
            SessionId.Create(summary.SessionId),
            targetId ?? $"{SessionTargetPrefix}{summary.SessionId}");
        return Task.FromResult<IAgentExecutionHandle?>(handle);
    }

    private async Task<IAgentExecutionHandle> DispatchExistingAsync(
        SessionId sessionId,
        AgentId? agentId,
        string? targetId,
        string? prompt,
        CancellationToken cancellationToken)
    {
        var summary = sessions.Get(sessionId.Value) ?? throw new InvalidOperationException($"unknown ACP session: {sessionId.Value}");
        if (summary.Status == HostSessionStatuses.Exited)
            summary = await agents.ResumeAsync(sessionId.Value, cancellationToken).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(prompt))
            agents.Prompt(summary.SessionId, prompt);
        return new AcpExecutionHandle(
            agents,
            sessions,
            agentId ?? AgentId.FromSession(sessionId),
            sessionId,
            targetId ?? $"{SessionTargetPrefix}{summary.SessionId}");
    }

    private static string ResolveAgentKind(AgentDispatchRequest request)
    {
        if (request.Metadata is { } metadata && metadata.TryGetValue("agentKind", out var metaKind) && !string.IsNullOrWhiteSpace(metaKind))
            return metaKind;
        if (request.TargetId is { } targetId && targetId.StartsWith(TargetPrefix, StringComparison.OrdinalIgnoreCase))
        {
            var suffix = targetId[TargetPrefix.Length..];
            if (!string.IsNullOrWhiteSpace(suffix))
                return suffix;
        }
        return !string.IsNullOrWhiteSpace(request.AgentPreset) ? request.AgentPreset : AgentKinds.Acp;
    }

    private static RemoteAgentCommand? ResolveCustomCommand(AgentDispatchRequest request)
    {
        if (request.Metadata is null || !request.Metadata.TryGetValue("executable", out var executable) || string.IsNullOrWhiteSpace(executable))
            return null;
        var arguments = request.Metadata.TryGetValue("arguments", out var rawArgs) && !string.IsNullOrWhiteSpace(rawArgs)
            ? rawArgs.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            : [];
        return new RemoteAgentCommand(executable, arguments);
    }

    private sealed class AcpExecutionHandle(
        ExternalAgentSessions agents,
        HostSessionRegistry sessions,
        AgentId agentId,
        SessionId sessionId,
        string targetId) : IAgentExecutionHandle
    {
        public AgentId AgentId { get; } = agentId;
        public SessionId? SessionId { get; } = sessionId;
        public AgentBackendKind BackendKind => AgentBackendKind.Acp;
        public string TargetId { get; } = targetId;

        public async Task<AgentExecutionSnapshot> SendAsync(
            string message,
            AgentMessageIntent intent = AgentMessageIntent.Task,
            MessagePrincipal? sender = null,
            CancellationToken cancellationToken = default)
        {
            var current = sessions.Get(sessionId.Value) ?? throw new InvalidOperationException($"unknown ACP session: {sessionId.Value}");
            if (current.Status == HostSessionStatuses.Exited)
                await agents.ResumeAsync(sessionId.Value, cancellationToken).ConfigureAwait(false);
            agents.Prompt(sessionId.Value, message);
            return await GetStatusAsync(cancellationToken).ConfigureAwait(false);
        }

        public Task<AgentExecutionSnapshot> GetStatusAsync(CancellationToken cancellationToken = default)
        {
            var summary = sessions.Get(sessionId.Value);
            var state = summary?.Status switch
            {
                HostSessionStatuses.Running => AgentLifecycleState.Active,
                HostSessionStatuses.Exited => AgentLifecycleState.Suspended,
                _ => AgentLifecycleState.Idle,
            };
            return Task.FromResult(new AgentExecutionSnapshot(
                AgentId,
                SessionId,
                AgentBackendKind.Acp,
                TargetId,
                state,
                summary?.Title,
                summary?.Status == HostSessionStatuses.Exited,
                UpdatedAt: summary?.LastActivityAt ?? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));
        }

        public Task CancelAsync(CancellationToken cancellationToken = default)
            => agents.CancelAsync(sessionId.Value, cancellationToken);

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
