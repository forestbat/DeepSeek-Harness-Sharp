using Dsh.RemoteHost;

namespace Dsh.Host;

/** Starts and controls harness, terminal-mode and ACP-mode agents. */
internal sealed class HarnessRemoteAgentBackend(
    HarnessRemoteRuntime runtime,
    IRemoteSessionBackend session,
    HostPtySessions ptys,
    ExternalAgentSessions agents) : IRemoteAgentBackend
{
    public Task<IReadOnlyList<RemoteAgentCatalogEntry>> AgentCatalogAsync(CancellationToken cancellationToken)
        => ExternalAgentSessions.CatalogAsync(cancellationToken);

    public async Task<RemoteSessionSummary> StartAgentAsync(RemoteAgentStartRequest request, CancellationToken cancellationToken)
    {
        if (request.AgentKind != HostAgentKinds.Dsharp)
            return await agents.StartAsync(request, cancellationToken).ConfigureAwait(false);
        var directory = await ExternalAgentSessions.PrepareDirectoryAsync(request.ProjectPath, request.Worktree, cancellationToken).ConfigureAwait(false);
        var created = await session.CreateSessionAsync(directory, cancellationToken).ConfigureAwait(false);
        if (request.InitialPrompt is { } prompt)
            await session.SendMessageAsync(created.Id, prompt, [], cancellationToken).ConfigureAwait(false);
        return runtime.Sessions.Get(created.Id) ?? throw new InvalidOperationException($"session {created.Id} was not registered");
    }

    public Task PromptAgentAsync(string sessionId, string text, CancellationToken cancellationToken)
    {
        if (agents.IsAcp(sessionId))
        {
            agents.Prompt(sessionId, text);
            return Task.CompletedTask;
        }
        return KindOf(sessionId) == HostSessionKinds.Harness
            ? session.SendMessageAsync(sessionId, text, [], cancellationToken)
            : throw new InvalidOperationException($"session {sessionId} takes input through its terminal (host.pty.write)");
    }

    public Task CancelAgentAsync(string sessionId, CancellationToken cancellationToken)
        => KindOf(sessionId) switch
        {
            HostSessionKinds.Harness => session.InterruptAsync(sessionId, cancellationToken),
            HostSessionKinds.Acp => agents.CancelAsync(sessionId, cancellationToken),
            _ => throw new InvalidOperationException($"session {sessionId} is cancelled through its terminal (host.pty.write)"),
        };

    /** Releasing a harness handle ends its agent; terminal/external sessions end their process. */
    public Task StopAgentAsync(string sessionId, CancellationToken cancellationToken)
    {
        switch (KindOf(sessionId))
        {
            case HostSessionKinds.Harness:
                runtime.Release(sessionId);
                return Task.CompletedTask;
            case HostSessionKinds.Terminal:
                return ptys.StopAsync(sessionId, cancellationToken);
            default:
                return agents.StopAsync(sessionId, cancellationToken);
        }
    }

    public async Task<RemoteSessionSummary> ResumeAgentAsync(string sessionId, CancellationToken cancellationToken)
    {
        if (KindOf(sessionId) != HostSessionKinds.Harness)
            return await agents.ResumeAsync(sessionId, cancellationToken).ConfigureAwait(false);
        await session.ResumeSessionAsync(sessionId, cancellationToken).ConfigureAwait(false);
        return runtime.Sessions.Get(sessionId) ?? throw new InvalidOperationException($"session {sessionId} was not registered");
    }

    private string KindOf(string sessionId)
        => runtime.Sessions.Get(sessionId)?.Kind ?? throw new InvalidOperationException($"unknown session: {sessionId}");
}
