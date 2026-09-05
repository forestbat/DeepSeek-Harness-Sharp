using System.Text.Json;
using Dsh.Core;
using Dsh.Llm;

namespace Dsh.Goal;

public sealed class GoalFoldState
{
    public GoalSnapshot? Goal;
    public long RoundsStarted;
    public long? CreatedAt;
    public long? UpdatedAt;
    public GoalRef? LastRef;
    public List<string> SeenGoalIds = [];
}

public static class GoalFold
{
    private static readonly IReadOnlySet<GoalOperation> SnapshotOperations = new HashSet<GoalOperation>
    {
        GoalOperation.Create, GoalOperation.Edit, GoalOperation.Pause,
        GoalOperation.Resume, GoalOperation.Complete, GoalOperation.Block,
    };

    private static readonly string ClearChangeKeys = string.Join(',', new[] { "cleared", "clearedAt", "kind", "operation", "version" }.Order(StringComparer.Ordinal));
    private static readonly string SnapshotChangeKeys = string.Join(',', new[] { "createdAt", "goal", "kind", "operation", "roundsStarted", "updatedAt", "version" }.Order(StringComparer.Ordinal));

    public static GoalRef ChangeRef(GoalChange change) => change switch
    {
        GoalChange.Clear clear => clear.Cleared,
        GoalChange.Snapshot snapshot => new GoalRef(snapshot.Goal.Id, snapshot.Goal.Revision),
        _ => throw new ArgumentOutOfRangeException(nameof(change), change, null),
    };

    public static GoalChange? DecodeChange(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object
            || !value.TryGetProperty("kind", out var kind)
            || kind.ValueKind != JsonValueKind.String
            || kind.GetString() != GoalChangePayload.EventType)
            return null;
        if (!value.TryGetProperty("version", out var version) || version.ValueKind != JsonValueKind.Number
            || !version.TryGetInt64(out var versionNumber) || versionNumber != GoalChange.ChangeVersion)
            throw new InvalidOperationException($"unsupported goal change version {JsonText(version)}");
        if (value.TryGetProperty("operation", out var operationElement) && operationElement.ValueKind == JsonValueKind.String
            && operationElement.GetString() == "clear")
        {
            if (ExactKeys(value) != ClearChangeKeys)
                throw new InvalidOperationException($"goal clear change must have exactly {ClearChangeKeys} fields");
            return new GoalChange.Clear(
                DecodeRef(value.GetProperty("cleared")),
                NonNegativeInteger(value.GetProperty("clearedAt"), "clearedAt"));
        }
        var operation = operationElement.ValueKind == JsonValueKind.String ? operationElement.GetString() : null;var snapshotOperation = operation switch
        {
            "create" => GoalOperation.Create,
            "edit" => GoalOperation.Edit,
            "pause" => GoalOperation.Pause,
            "resume" => GoalOperation.Resume,
            "complete" => GoalOperation.Complete,
            "block" => GoalOperation.Block,
            _ => (GoalOperation?)null,
        };
        if (snapshotOperation is not { } resolvedOperation || !SnapshotOperations.Contains(resolvedOperation))
            throw new InvalidOperationException("goal change operation is invalid");
        if (ExactKeys(value) != SnapshotChangeKeys)
            throw new InvalidOperationException($"goal snapshot change must have exactly {SnapshotChangeKeys} fields");
        var createdAt = NonNegativeInteger(value.GetProperty("createdAt"), "createdAt");
        var updatedAt = NonNegativeInteger(value.GetProperty("updatedAt"), "updatedAt");
        if (updatedAt < createdAt)
            throw new InvalidOperationException("goal change updatedAt cannot precede createdAt");
        return new GoalChange.Snapshot(
            resolvedOperation,
            DecodeSnapshot(value.GetProperty("goal")),
            NonNegativeInteger(value.GetProperty("roundsStarted"), "roundsStarted"),
            createdAt,
            updatedAt);
    }

    public static void ApplyChange(GoalFoldState state, GoalChange change)
    {
        var reference = ChangeRef(change);
        if (change is GoalChange.Clear clear)
        {
            var clearing = state.Goal ?? throw new InvalidOperationException("goal clear requires a current goal");
            RequireNextRevision(clearing, clear.Cleared, GoalOperation.Clear);
            if (state.UpdatedAt is not { } clearedBaseline)
                throw new InvalidOperationException("current goal fold lacks updatedAt");
            if (clear.ClearedAt < clearedBaseline)
                throw new InvalidOperationException("goal clear timestamp cannot precede the current goal update");
            state.Goal = null;
            state.RoundsStarted = 0;
            state.CreatedAt = null;
            state.UpdatedAt = null;
            state.LastRef = reference;
            return;
        }
        var snapshot = (GoalChange.Snapshot)change;
        if (snapshot.Operation == GoalOperation.Create)
        {
            if (snapshot.Goal.Revision != 1 || snapshot.Goal.Phase != GoalPhase.Active || snapshot.RoundsStarted != 0
                || (state.Goal is not null && state.Goal.Phase != GoalPhase.Complete)
                || state.SeenGoalIds.Contains(snapshot.Goal.Id.Value))
                throw new InvalidOperationException("goal create requires a fresh active revision-one goal with zero rounds");
            state.SeenGoalIds.Add(snapshot.Goal.Id.Value);
        }
        else
        {
            var current = state.Goal
                ?? throw new InvalidOperationException($"goal {GoalNames.Of(snapshot.Operation)} requires a current goal");
            ValidateSnapshotTransition(state, snapshot, current);
        }
        state.Goal = snapshot.Goal;
        state.RoundsStarted = snapshot.RoundsStarted;
        state.CreatedAt = snapshot.CreatedAt;
        state.UpdatedAt = snapshot.UpdatedAt;
        state.LastRef = reference;
    }

    public static void ApplyEvent(GoalFoldState state, SessionEvent sessionEvent)
    {
        if (sessionEvent.Type == GoalChangePayload.EventType)
        {
            var change = sessionEvent.Data switch
            {
                GoalChangePayload { Decoded: { } decoded } => decoded,
                GoalChangePayload payload => DecodeChange(payload.Raw),
                UnknownSessionEventPayload unknown => DecodeChange(unknown.Raw),
                _ => null,
            } ?? throw new InvalidOperationException($"goal change at session event {sessionEvent.Seq} has an invalid kind");
            ApplyChange(state, change);
            return;
        }
        if (sessionEvent.Type == SessionEventTypes.UserMessage)
        {
            var source = GoalSourceOf(sessionEvent.Data);
            if (source is null)
                return;
            var current = state.Goal;
            if (current is null || current.Phase != GoalPhase.Active || source.GoalId != current.Id.Value
                || source.Revision != current.Revision || source.Round != state.RoundsStarted + 1
                || source.Round > current.MaxGoalRounds)
                throw new InvalidOperationException($"goal round at session event {sessionEvent.Seq} is not the next admitted round of the active goal");
            state.RoundsStarted = source.Round;
        }
    }

    private static GoalMessageSource? GoalSourceOf(SessionEventPayload payload)
    {
        if (payload is not UserMessagePayload userMessage)
            return null;
        var source = userMessage.Message.Source;
        if (source.Kind != "goal")
            return null;
        if (source is not GoalMessageSource goal || goal.GoalId.Length == 0 || goal.Revision < 1 || goal.Round < 1)
            throw new InvalidOperationException("goal message source is invalid");
        return goal;
    }

    private static void ValidateSnapshotTransition(GoalFoldState state, GoalChange.Snapshot change, GoalSnapshot current)
    {
        var next = change.Goal;
        RequireNextRevision(current, new GoalRef(next.Id, next.Revision), change.Operation);
        if (state.UpdatedAt is not { } updatedAt)
            throw new InvalidOperationException("current goal fold lacks updatedAt");
        if (change.CreatedAt != state.CreatedAt || change.UpdatedAt < updatedAt || change.RoundsStarted != state.RoundsStarted)
            throw new InvalidOperationException($"goal {GoalNames.Of(change.Operation)} does not preserve the current counters and timestamps");
        switch (change.Operation)
        {
            case GoalOperation.Edit:
                if (next.Phase != current.Phase || next.BlockedReason != current.BlockedReason)
                    throw new InvalidOperationException("goal edit cannot change phase or blocked reason");
                break;
            case GoalOperation.Pause:
                RequireSameDefinition(current, next, change.Operation);
                if (current.Phase != GoalPhase.Active || next.Phase != GoalPhase.Paused)
                    throw new InvalidOperationException("goal pause has an invalid phase transition");
                break;
            case GoalOperation.Resume:
                RequireSameDefinition(current, next, change.Operation);
                if (current.Phase is not (GoalPhase.Active or GoalPhase.Paused or GoalPhase.Blocked)
                    || next.Phase != GoalPhase.Active || state.RoundsStarted >= next.MaxGoalRounds)
                    throw new InvalidOperationException("goal resume has an invalid phase transition or exhausted round budget");
                break;
            case GoalOperation.Complete:
                RequireSameDefinition(current, next, change.Operation);
                if (current.Phase == GoalPhase.Complete || next.Phase != GoalPhase.Complete)
                    throw new InvalidOperationException("goal complete has an invalid phase transition");
                break;
            case GoalOperation.Block:
                RequireSameDefinition(current, next, change.Operation);
                if (current.Phase != GoalPhase.Active || next.Phase != GoalPhase.Blocked)
                    throw new InvalidOperationException("goal block has an invalid phase transition");
                break;
            default:
                throw new InvalidOperationException("goal create cannot be validated as a current-goal transition");
        }
    }

    private static void RequireSameDefinition(GoalSnapshot current, GoalSnapshot next, GoalOperation operation)
    {
        if (next.Objective != current.Objective || next.MaxGoalRounds != current.MaxGoalRounds)
            throw new InvalidOperationException($"goal {GoalNames.Of(operation)} cannot change objective or maxGoalRounds");
    }

    private static void RequireNextRevision(GoalSnapshot current, GoalRef next, GoalOperation operation)
    {
        if (next.Id != current.Id || next.Revision != current.Revision + 1)
            throw new InvalidOperationException($"goal {GoalNames.Of(operation)} must advance the current goal by one revision");
    }

    private static string ExactKeys(JsonElement value)
        => string.Join(',', value.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal));

    private static string JsonText(JsonElement? element)
        => element is null ? "undefined"
            : element.Value.ValueKind == JsonValueKind.String ? element.Value.GetString()!
            : element.Value.GetRawText();

    private static long PositiveInteger(JsonElement value, string field)
    {
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt64(out var number) || number < 1)
            throw new InvalidOperationException($"goal change {field} must be a positive safe integer");
        return number;
    }

    private static long NonNegativeInteger(JsonElement value, string field)
    {
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt64(out var number) || number < 0)
            throw new InvalidOperationException($"goal change {field} must be a non-negative safe integer");
        return number;
    }

    private static GoalRef DecodeRef(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object || ExactKeys(value) != "id,revision")
            throw new InvalidOperationException("goal clear tombstone must have exactly id and revision fields");
        if (!value.TryGetProperty("id", out var id) || id.ValueKind != JsonValueKind.String || id.GetString() is not { Length: > 0 } goalId)
            throw new InvalidOperationException("goal clear tombstone id must be a non-empty string");
        return new GoalRef(GoalId.Create(goalId), PositiveInteger(value.GetProperty("revision"), "cleared.revision"));
    }

    private static GoalSnapshot DecodeSnapshot(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object)
            throw new InvalidOperationException("goal change goal must be a record");
        if (!value.TryGetProperty("id", out var idElement) || idElement.ValueKind != JsonValueKind.String
            || idElement.GetString() is not { Length: > 0 } id)
            throw new InvalidOperationException("goal change goal.id must be a non-empty string");
        if (!value.TryGetProperty("objective", out var objectiveElement) || objectiveElement.ValueKind != JsonValueKind.String
            || objectiveElement.GetString() is not { } objective
            || objective.Trim().Length == 0 || objective != objective.Trim())
            throw new InvalidOperationException("goal change goal.objective must be non-empty and normalized");
        if (!value.TryGetProperty("phase", out var phaseElement) || phaseElement.ValueKind != JsonValueKind.String
            || phaseElement.GetString() is not { } phaseName
            || phaseName is not ("active" or "paused" or "blocked" or "complete"))
            throw new InvalidOperationException("goal change goal.phase is invalid");
        var phase = GoalNames.ParsePhase(phaseName);
        var expectedKeys = phase == GoalPhase.Blocked
            ? "blockedReason,id,maxGoalRounds,objective,phase,revision"
            : "id,maxGoalRounds,objective,phase,revision";
        if (ExactKeys(value) != expectedKeys)
            throw new InvalidOperationException($"goal change goal for phase {phaseName} must have exactly {expectedKeys} fields");
        return new GoalSnapshot(
            GoalId.Create(id),
            PositiveInteger(value.GetProperty("revision"), "goal.revision"),
            objective,
            phase,
            PositiveInteger(value.GetProperty("maxGoalRounds"), "goal.maxGoalRounds"),
            phase == GoalPhase.Blocked ? DecodeBlockReason(value.GetProperty("blockedReason")) : null);
    }

    private static GoalBlockReason DecodeBlockReason(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object || ExactKeys(value) != "code,message")
            throw new InvalidOperationException("goal change goal.blockedReason must have exactly code and message fields");
        if (!value.TryGetProperty("code", out var codeElement) || codeElement.ValueKind != JsonValueKind.String
            || codeElement.GetString() is not { } code || !GoalValidation.BlockCodePattern().IsMatch(code))
            throw new InvalidOperationException("goal change goal.blockedReason.code must be lower-kebab-case");
        if (!value.TryGetProperty("message", out var messageElement) || messageElement.ValueKind != JsonValueKind.String
            || messageElement.GetString() is not { } message
            || message.Trim().Length == 0 || message != message.Trim())
            throw new InvalidOperationException("goal change goal.blockedReason.message must be non-empty and normalized");
        return new GoalBlockReason(code, message);
    }
}
