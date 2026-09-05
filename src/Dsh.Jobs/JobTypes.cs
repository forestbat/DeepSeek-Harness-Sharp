using Dsh.Core;
using Dsh.Llm;

namespace Dsh.Jobs;

public enum JobStatus
{
    Running,
    Stopping,
    Completed,
    Killed,
    Failed,
}

public static class JobStatusWire
{
    public static string Of(JobStatus status) => status switch
    {
        JobStatus.Running => "running",
        JobStatus.Stopping => "stopping",
        JobStatus.Completed => "completed",
        JobStatus.Killed => "killed",
        JobStatus.Failed => "failed",
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, null),
    };

    public static bool IsTerminal(JobStatus status)
        => status is JobStatus.Completed or JobStatus.Killed or JobStatus.Failed;
}

public sealed record JobOutcome(JobStatus Status, string? Detail = null, string? Output = null);

public sealed record JobStart
{
    public required string Kind { get; init; }
    public required string Label { get; init; }
    public int? OutputLimitBytes { get; init; }
    public IAgent? Owner { get; init; }
    public required Func<JobHooks> Run { get; init; }
}

public sealed record JobHooks(
    Action<string?> Cancel,
    Task<JobOutcome> Done,
    Func<string>? ReadOutput = null);

public sealed record JobSnapshot
{
    public required string Id { get; init; }
    public required string Kind { get; init; }
    public required string Label { get; init; }
    public int? OutputLimitBytes { get; init; }
    public SessionId? OwnerSession { get; init; }
    public required JobStatus Status { get; init; }
    public string? Detail { get; init; }
    public required long StartedAt { get; init; }
    public long? FinishedAt { get; init; }
    public required bool Reported { get; init; }
}

public sealed record JobRead(string Text, JobSnapshot Snapshot);

public delegate void JobDoneListener(JobSnapshot snapshot, IAgent? owner);

public delegate void JobsChangedListener(IAgent? owner);

public enum JobKillOutcome
{
    Requested,
    AlreadyFinished,
}
