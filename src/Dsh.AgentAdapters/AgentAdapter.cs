namespace Dsh.AgentAdapters;

/** An executable plus its arguments; the working directory is always set on the process, never passed as an argument. */
public sealed record AgentCommand(string Executable, IReadOnlyList<string> Arguments)
{
    public AgentCommand Append(IEnumerable<string> arguments) => this with { Arguments = [.. Arguments, .. arguments] };

    /** Value equality over the argument sequence (the default record equality would compare the list reference). */
    public bool Equals(AgentCommand? other)
        => other is not null && Executable == other.Executable && Arguments.SequenceEqual(other.Arguments);

    public override int GetHashCode() => Arguments.Aggregate(Executable.GetHashCode(), (hash, argument) => HashCode.Combine(hash, argument));
}

/**
 * Argument templates appended to the interactive command.
 * Prompt carries PromptToken, ResumeById carries SessionIdToken; ResumeLatest continues the most recent session of the working directory.
 */
public sealed record AgentLaunchSyntax(
    IReadOnlyList<string>? Prompt = null,
    IReadOnlyList<string>? ResumeById = null,
    IReadOnlyList<string>? ResumeLatest = null)
{
    public const string PromptToken = "{prompt}";
    public const string SessionIdToken = "{sessionId}";

    public static AgentLaunchSyntax None { get; } = new();
}

/**
 * How to run one kind of agent.
 * Interactive is the full-screen TUI run inside a PTY; Acp is the structured stdio channel when the agent has one.
 */
public sealed record AgentAdapter(
    string Kind,
    string DisplayName,
    AgentCommand? Interactive,
    AgentCommand? Acp,
    AgentLaunchSyntax Syntax)
{
    /** Interactive launch, optionally submitting an initial prompt; null when the agent has no interactive mode. */
    public AgentCommand? InteractiveCommand(string? initialPrompt = null)
        => initialPrompt is null || Syntax.Prompt is null
            ? Interactive
            : Interactive?.Append(Expand(Syntax.Prompt, AgentLaunchSyntax.PromptToken, initialPrompt));

    /** Interactive launch that resumes SessionId, or the most recent session when SessionId is null; null when unsupported. */
    public AgentCommand? ResumeCommand(string? sessionId = null)
    {
        var template = sessionId is null ? Syntax.ResumeLatest : Syntax.ResumeById;
        return template is null ? null : Interactive?.Append(Expand(template, AgentLaunchSyntax.SessionIdToken, sessionId ?? ""));
    }

    private static IEnumerable<string> Expand(IReadOnlyList<string> template, string token, string value)
        => template.Select(argument => argument == token ? value : argument);
}
