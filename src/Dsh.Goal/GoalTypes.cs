using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Dsh.Core;
using Dsh.Llm;

namespace Dsh.Goal;

public static partial class GoalValidation
{
    [GeneratedRegex("""^[a-z][a-z0-9]*(?:-[a-z0-9]+)*$""")]
    public static partial Regex BlockCodePattern();
}

[JsonConverter(typeof(BrandJsonConverter<GoalId>))]
public readonly record struct GoalId(string Value) : IBrand<GoalId>
{
    public static GoalId Create(string value) => new(value);
    public override string ToString() => Value;
}

public sealed record GoalRef(GoalId Id, long Revision);

public enum GoalPhase
{
    Active,
    Paused,
    Blocked,
    Complete,
}

public enum GoalActivation
{
    Armed,
    Disarmed,
}

public enum GoalOperation
{
    Create,
    Edit,
    Pause,
    Resume,
    Complete,
    Block,
    Clear,
}

public static class GoalNames
{
    public static string Of(GoalPhase phase) => phase switch
    {
        GoalPhase.Active => "active",
        GoalPhase.Paused => "paused",
        GoalPhase.Blocked => "blocked",
        GoalPhase.Complete => "complete",
        _ => throw new ArgumentOutOfRangeException(nameof(phase), phase, null),
    };

    public static GoalPhase ParsePhase(string value) => value switch
    {
        "active" => GoalPhase.Active,
        "paused" => GoalPhase.Paused,
        "blocked" => GoalPhase.Blocked,
        "complete" => GoalPhase.Complete,
        _ => throw new ArgumentException($"unknown goal phase \"{value}\"", nameof(value)),
    };

    public static string Of(GoalOperation operation) => operation switch
    {
        GoalOperation.Create => "create",
        GoalOperation.Edit => "edit",
        GoalOperation.Pause => "pause",
        GoalOperation.Resume => "resume",
        GoalOperation.Complete => "complete",
        GoalOperation.Block => "block",
        GoalOperation.Clear => "clear",
        _ => throw new ArgumentOutOfRangeException(nameof(operation), operation, null),
    };

    public static string Of(GoalActivation activation) => activation switch
    {
        GoalActivation.Armed => "armed",
        GoalActivation.Disarmed => "disarmed",
        _ => throw new ArgumentOutOfRangeException(nameof(activation), activation, null),
    };
}

public sealed record GoalBlockReason(string Code, string Message);

public sealed record GoalSnapshot(
    GoalId Id,
    long Revision,
    string Objective,
    GoalPhase Phase,
    long MaxGoalRounds,
    GoalBlockReason? BlockedReason = null);

public sealed record GoalView(
    GoalId Id,
    long Revision,
    string Objective,
    GoalPhase Phase,
    long MaxGoalRounds,
    GoalBlockReason? BlockedReason,
    long RoundsStarted,
    long CreatedAt,
    long UpdatedAt,
    GoalActivation Activation);

public sealed record GoalProjection(GoalSnapshot Goal, long RoundsStarted, long CreatedAt, long UpdatedAt);

public sealed record GoalProjectionState
{
    public GoalProjection? Current { get; init; }
    public IReadOnlyList<string> SeenGoalIds { get; init; } = [];
    public string? Failure { get; init; }
}

public sealed record GoalChanged(GoalOperation Operation, GoalRef Ref, GoalView? Goal);

public sealed record CreateGoalRequest(string Objective, long? MaxGoalRounds = null);

public sealed record EditGoalRequest(string? Objective = null, long? MaxGoalRounds = null);

public static class GoalErrorCodes
{
    public const string AgentNotLive = "GOAL_AGENT_NOT_LIVE";
    public const string NotFound = "GOAL_NOT_FOUND";
    public const string AlreadyExists = "GOAL_ALREADY_EXISTS";
    public const string StaleRevision = "GOAL_STALE_REVISION";
    public const string InvalidObjective = "GOAL_INVALID_OBJECTIVE";
    public const string InvalidMaxRounds = "GOAL_INVALID_MAX_ROUNDS";
    public const string InvalidBlockReason = "GOAL_INVALID_BLOCK_REASON";
    public const string InvalidEdit = "GOAL_INVALID_EDIT";
    public const string InvalidTransition = "GOAL_INVALID_TRANSITION";
}

public sealed class GoalException(string message, string code) : HarnessException(message, code);

public abstract record GoalChange
{
    public const int ChangeVersion = 1;

    public sealed record Snapshot(GoalOperation Operation, GoalSnapshot Goal, long RoundsStarted, long CreatedAt, long UpdatedAt)
        : GoalChange;

    public sealed record Clear(GoalRef Cleared, long ClearedAt) : GoalChange;
}
