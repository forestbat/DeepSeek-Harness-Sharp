namespace Dsh.Workspace;

public enum DoctorOutcome
{
    Ok,
    Warning,
    Failed,
    Skipped,
}

/** One diagnosis: what was checked, how it went, what was found, and a command that repairs it when it is not Ok. */
public sealed record DoctorCheck(string Name, DoctorOutcome Outcome, string Detail, string? Fix = null);

