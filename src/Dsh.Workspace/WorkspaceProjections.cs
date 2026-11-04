using Dsh.RemoteHost;

namespace Dsh.Workspace;

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

