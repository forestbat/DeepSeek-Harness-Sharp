using Dsh.Core;
using Dsh.Interaction;
using Dsh.Llm;
using Dsh.RemoteHost;
using Dsh.Runtime;

namespace Dsh.Host;

/** Commands, persisted session mutations and subagent inspection. */
internal sealed class HarnessRemoteSessionEditingBackend(HarnessRemoteRuntime runtime, IRemoteAgentBackend agents) : IRemoteSessionEditingBackend
{
    public async Task<RemoteCommandResult> ExecuteSessionCommandAsync(string sessionId, string line, CancellationToken cancellationToken)
    {
        var commands = runtime.App.Ctx.Get<CommandsService>(CommandsService.ServiceName, false);
        var execution = commands is null ? null : await commands.Execute(runtime.Require(sessionId), line).ConfigureAwait(false);
        return execution?.Result switch
        {
            CommandResult.Success result => new(true, result.Text, result.FollowupPrompt, true),
            CommandResult.Error result => new(true, result.Text, result.FollowupPrompt, false),
            _ => new(false, null, null, false),
        };
    }

    public Task<IReadOnlyList<RemoteCommandDescriptor>> ListSessionCommandsAsync(string sessionId, CancellationToken cancellationToken)
    {
        var commands = runtime.App.Ctx.Get<CommandsService>(CommandsService.ServiceName, false)?.List(runtime.Require(sessionId)) ?? [];
        return Task.FromResult<IReadOnlyList<RemoteCommandDescriptor>>([.. commands.Select(command => new RemoteCommandDescriptor(command.Name, command.Description))]);
    }

    public Task RenameSessionAsync(string sessionId, string title, CancellationToken cancellationToken)
    {
        var id = SessionId.Create(sessionId);
        var persistence = runtime.Persistence ?? throw new InvalidOperationException("session persistence is unavailable");
        if (runtime.Store.Get(id) is { } session)
            session.Rename(title);
        persistence.Rename(id, title);
        runtime.Sessions.SetTitle(sessionId, title);
        return Task.CompletedTask;
    }

    public async Task DeleteSessionAsync(string sessionId, CancellationToken cancellationToken)
    {
        if (runtime.Sessions.Get(sessionId) is not null)
            await agents.StopAgentAsync(sessionId, cancellationToken).ConfigureAwait(false);
        (runtime.Persistence ?? throw new InvalidOperationException("session persistence is unavailable")).Delete(SessionId.Create(sessionId));
        runtime.Sessions.Remove(sessionId);
    }

    public Task SetSessionApprovalPolicyAsync(string sessionId, string policy, CancellationToken cancellationToken)
    {
        if (!Enum.TryParse<ApprovalPolicy>(policy, out var parsed) || !Enum.IsDefined(parsed))
            throw new ArgumentException($"unknown approval policy: {policy}", nameof(policy));
        var approvals = runtime.App.Ctx.Get<ApprovalService>(ApprovalService.ServiceName)
            ?? throw new InvalidOperationException("approval service is unavailable");
        approvals.SetPolicy(runtime.Require(sessionId), parsed);
        return Task.CompletedTask;
    }

    public async Task<IReadOnlyList<RemoteSubagentInfo>> ListSubagentsAsync(string rootSessionId, CancellationToken cancellationToken)
    {
        var subagents = runtime.App.Ctx.Get<ISubagentService>(ISubagentService.ServiceName, false);
        if (subagents is null)
            return [];
        var entries = await subagents.ListDescendantsAsync(SessionId.Create(rootSessionId), signal: cancellationToken).ConfigureAwait(false);
        return [.. entries.OfType<SubagentListEntry.Child>().Select(child =>
        {
            var live = subagents.GetLive(child.Id);
            var header = live?.Session.Header ?? runtime.Persistence?.Stat(child.Id)?.Header;
            return new RemoteSubagentInfo(child.Id.Value, child.Parent?.Value, child.Depth ?? 1, child.Mode, child.Label,
                child.HasChildren, live is not null, header?.CreatedAt ?? 0);
        })];
    }
}
