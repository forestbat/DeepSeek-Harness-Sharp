using Dsh.Boot;
using Dsh.Llm;
using Dsh.RemoteHost;

namespace Dsh.Gui.Services;

/** One session addressed across machines; ProjectPath and Title are display values, not identity. */
public sealed record SessionRef(
    string Machine,
    string SessionId,
    string Title,
    string? ProjectPath,
    string Kind,
    bool IsCurrent,
    RemoteSessionSummary? Summary = null);

/** Session mutations that always use the session's own machine, never the connection of the chat in view. */
public sealed class SessionActions(FleetWorkspace fleet, HarnessHome home, Func<(string Machine, string SessionId)>? current = null)
{
    public async Task RenameAsync(SessionRef session, string title, CancellationToken cancellationToken)
    {
        var lease = await LeaseAsync(session.Machine, cancellationToken);
        await lease.Client.SessionEditing.RenameSessionAsync(session.SessionId, title, cancellationToken);
    }

    public async Task DeleteAsync(SessionRef session, CancellationToken cancellationToken)
    {
        if (IsCurrent(session))
            throw new InvalidOperationException("不能删除当前正在使用的会话");
        var lease = await LeaseAsync(session.Machine, cancellationToken);
        await lease.Client.SessionEditing.DeleteSessionAsync(session.SessionId, cancellationToken);
    }

    public async Task<string> ExportLogAsync(SessionRef session, CancellationToken cancellationToken)
    {
        var lease = await LeaseAsync(session.Machine, cancellationToken);
        var snapshot = await lease.Client.GetSessionSnapshotAsync(session.SessionId, cancellationToken);
        var directory = Path.Combine(home.Root, "exports");
        Directory.CreateDirectory(directory);
        var machine = string.Concat(session.Machine.Select(character => Path.GetInvalidFileNameChars().Contains(character) ? '_' : character));
        var path = Path.Combine(directory, $"{machine}-{session.SessionId}-{DateTimeOffset.Now:yyyyMMdd-HHmmss}.jsonl");
        await using var stream = File.Create(path);
        await using var writer = new StreamWriter(stream);
        foreach (var sessionEvent in snapshot.Events)
            await writer.WriteLineAsync(DshJson.Serialize(sessionEvent));
        await writer.FlushAsync(cancellationToken);
        return path;
    }

    public async Task StopAsync(SessionRef session, CancellationToken cancellationToken)
    {
        var lease = await LeaseAsync(session.Machine, cancellationToken);
        await lease.Client.Agent.StopAgentAsync(session.SessionId, cancellationToken);
    }

    public async Task<string> ResumeAsync(SessionRef session, CancellationToken cancellationToken)
    {
        var lease = await LeaseAsync(session.Machine, cancellationToken);
        var summary = session.Summary ?? throw new InvalidOperationException($"找不到会话 {session.SessionId}");
        if (summary.Kind != HostSessionKinds.Harness)
            return (await lease.Client.Agent.ResumeAgentAsync(session.SessionId, cancellationToken)).SessionId;
        return (await lease.Client.ResumeSessionAsync(session.SessionId, cancellationToken)).Id;
    }

    public bool IsCurrent(SessionRef session)
        => current?.Invoke() is { } active
            && active.Machine == session.Machine
            && active.SessionId == session.SessionId;

    private Task<HostLease> LeaseAsync(string machine, CancellationToken cancellationToken)
        => (fleet.Find(machine) ?? throw new InvalidOperationException($"no machine named \"{machine}\""))
            .WaitConnectedAsync(cancellationToken);
}
