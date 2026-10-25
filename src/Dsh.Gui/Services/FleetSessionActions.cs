using Dsh.Fleet;
using Dsh.RemoteHost;

namespace Dsh.Gui.Services;

/**
 * Lifecycle operations on fleet sessions, shared by the dashboard, the session view and the command palette.
 * Every call goes through the machine's current connection and waits for it to be connected; failures surface as exceptions for the caller to report.
 */
public sealed class FleetSessionActions(FleetWorkspace fleet)
{
    public async Task<HostLease> LeaseAsync(string machine, CancellationToken cancellationToken)
        => await Connection(machine).WaitConnectedAsync(cancellationToken);

    private MachineConnection Connection(string machine)
        => fleet.Find(machine) ?? throw new InvalidOperationException($"no machine named \"{machine}\"");

    /** Harness sessions release their agent, terminals and external agents end their process, ACP agents end their adapter. */
    public async Task StopAsync(string machine, string sessionId, CancellationToken cancellationToken)
    {
        var lease = await LeaseAsync(machine, cancellationToken);
        await lease.Client.Agent.StopAgentAsync(sessionId, cancellationToken);
    }

    /** Resumes an exited session and returns its actual ID, including a new terminal-mode agent ID. */
    public async Task<string> ResumeAsync(string machine, RemoteSessionSummary session, CancellationToken cancellationToken)
    {
        var lease = await LeaseAsync(machine, cancellationToken);
        if (session.Kind != HostSessionKinds.Harness)
            return (await lease.Client.Agent.ResumeAgentAsync(session.SessionId, cancellationToken)).SessionId;
        return (await lease.Client.ResumeSessionAsync(session.SessionId, cancellationToken)).Id;
    }

    /** Starts a session of any kind; the host chooses its own model for harness sessions. */
    public async Task<RemoteSessionSummary> StartAsync(string machine, RemoteAgentStartRequest request, CancellationToken cancellationToken)
        => await (await LeaseAsync(machine, cancellationToken)).Client.Agent.StartAgentAsync(request, cancellationToken);
}

