using System.Runtime.CompilerServices;
using Cordis;
using Dsh.Core;
using Dsh.Llm;

namespace Dsh.Goal;

public static class GoalProjectionDefinition
{
    public const string Key = "goal";
    public const int StateVersion = 6;

    public static readonly SessionProjectionDefinition<GoalProjectionState> Instance = new(
        Key,
        StateVersion,
        (_, _) => new GoalProjectionState(),
        Apply);

    private static GoalProjectionState Apply(GoalProjectionState state, SessionEvent sessionEvent)
    {
        if (state.Failure is not null)
            return state;
        if (sessionEvent.Type != GoalChangePayload.EventType
            && sessionEvent.Data is not UserMessagePayload { Message.Source.Kind: "goal" })
            return state;
        var folded = new GoalFoldState
        {
            Goal = state.Current?.Goal,
            RoundsStarted = state.Current?.RoundsStarted ?? 0,
            CreatedAt = state.Current?.CreatedAt,
            UpdatedAt = state.Current?.UpdatedAt,
            SeenGoalIds = [.. state.SeenGoalIds],
        };
        try
        {
            GoalFold.ApplyEvent(folded, sessionEvent);
        }
        catch (Exception error)
        {
            return state with { Failure = $"goal replay failed at session event {sessionEvent.Seq}: {error.Message}" };
        }
        GoalProjection? current = null;
        if (folded.Goal is { } goal)
        {
            if (folded.CreatedAt is not { } createdAt || folded.UpdatedAt is not { } updatedAt)
                throw new InvalidOperationException("current goal fold lacks timestamps");
            current = new GoalProjection(goal, folded.RoundsStarted, createdAt, updatedAt);
        }
        return new GoalProjectionState { Current = current, SeenGoalIds = [.. folded.SeenGoalIds] };
    }
}

public sealed record GoalServiceConfig
{
    public long DefaultMaxGoalRounds { get; init; } = GoalService.DefaultMaxGoalRoundsValue;
}

public sealed class GoalService : Service
{
    public const string ServiceName = "goals";
    public const string ChangedEvent = "goal/changed";
    public const long DefaultMaxGoalRoundsValue = 256;

    private sealed class GoalRuntimeState
    {
        public GoalActivation Activation;
        public (long Offset, GoalActivation Activation)? PendingActivation;
    }

    private readonly long _defaultMaxGoalRounds;
    private readonly ConditionalWeakTable<Session, GoalRuntimeState> _runtimeStates = new();

    static GoalService() => GoalChangePayload.RegisterCodec();

    public GoalService(Context ctx, GoalServiceConfig? config = null) : base(ctx, ServiceName)
    {
        _defaultMaxGoalRounds = ResolveMaxGoalRounds(config?.DefaultMaxGoalRounds ?? DefaultMaxGoalRoundsValue);
        var projections = ctx.Get<SessionProjectionRegistry>(SessionProjectionRegistry.ServiceName)
            ?? throw new InvalidOperationException("goals requires the sessionProjections service");
        projections.Register(GoalProjectionDefinition.Instance);
        ctx.On(AgentEventNames.SessionStart, (_, args) =>
        {
            if (args[0]?.GetType().GetProperty("Agent")?.GetValue(args[0]) is IAgent agent)
                RuntimeState(agent.Session).Activation = GoalActivation.Disarmed;
            return new ValueTask<object?>();
        }, new EventOptions { Global = true });
        ctx.On(SessionStore.EventEvent, (_, args) =>
        {
            var sessionEvent = (SessionEvent)args[1]!;
            if (sessionEvent.Type != GoalChangePayload.EventType)
                return new ValueTask<object?>();
            var runtime = RuntimeState((Session)args[0]!);
            runtime.Activation = runtime.PendingActivation is { } pending && pending.Offset == sessionEvent.Seq
                ? pending.Activation
                : GoalActivation.Disarmed;
            return new ValueTask<object?>();
        }, new EventOptions { Global = true });
    }

    public GoalView? Get(IAgent agent)
    {
        AssertLive(agent);
        return View(StateOf(agent.Session), RuntimeState(agent.Session));
    }

    public GoalView? Disarm(IAgent agent)
    {
        AssertLive(agent);
        var runtime = RuntimeState(agent.Session);
        runtime.Activation = GoalActivation.Disarmed;
        return View(StateOf(agent.Session), runtime);
    }

    public GoalView Create(IAgent agent, CreateGoalRequest request)
    {
        var objective = ResolveObjective(request.Objective);
        var maxGoalRounds = ResolveMaxGoalRounds(request.MaxGoalRounds ?? _defaultMaxGoalRounds);
        var (state, runtime) = PrepareMutation(agent);
        var current = state?.Goal;
        if (current is not null && current.Phase != GoalPhase.Complete)
            throw new GoalException($"goal \"{current.Id}\" already exists with phase \"{GoalNames.Of(current.Phase)}\"", GoalErrorCodes.AlreadyExists);
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var goal = new GoalSnapshot(GoalId.Create($"goal-{Guid.NewGuid()}"), 1, objective, GoalPhase.Active, maxGoalRounds);
        return CommitSnapshot(agent, runtime, GoalOperation.Create, goal, 0, now, now, GoalActivation.Armed);
    }

    public GoalView Edit(IAgent agent, GoalRef reference, EditGoalRequest request)
    {
        var (state, runtime) = PrepareMutation(agent);
        var currentState = ExpectCurrent(state, reference);
        var current = currentState.Goal;
        if (request.Objective is null && request.MaxGoalRounds is null)
            throw new GoalException("goal edit requires objective and/or maxGoalRounds", GoalErrorCodes.InvalidEdit);
        var goal = new GoalSnapshot(
            current.Id,
            current.Revision + 1,
            request.Objective is null ? current.Objective : ResolveObjective(request.Objective),
            current.Phase,
            request.MaxGoalRounds is null ? current.MaxGoalRounds : ResolveMaxGoalRounds(request.MaxGoalRounds.Value),
            current.BlockedReason);
        return CommitCurrent(agent, currentState, runtime, GoalOperation.Edit, goal, runtime.Activation);
    }

    public GoalView Pause(IAgent agent, GoalRef reference)
        => Transition(agent, reference, GoalOperation.Pause, [GoalPhase.Active], GoalPhase.Paused, GoalActivation.Disarmed);

    public GoalView Resume(IAgent agent, GoalRef reference)
    {
        var (state, runtime) = PrepareMutation(agent);
        var currentState = ExpectCurrent(state, reference);
        var current = currentState.Goal;
        if (current.Phase is not (GoalPhase.Active or GoalPhase.Paused or GoalPhase.Blocked))
            throw TransitionError(current, GoalOperation.Resume, [GoalPhase.Active, GoalPhase.Paused, GoalPhase.Blocked]);
        if (current.Phase == GoalPhase.Active && runtime.Activation == GoalActivation.Armed)
            throw new GoalException($"goal \"{current.Id}\" is already active and armed", GoalErrorCodes.InvalidTransition);
        if (currentState.RoundsStarted >= current.MaxGoalRounds)
        {
            throw new GoalException(
                $"goal \"{current.Id}\" exhausted {current.MaxGoalRounds} goal rounds; increase maxGoalRounds before resuming",
                GoalErrorCodes.InvalidTransition);
        }
        return CommitCurrent(agent, currentState, runtime, GoalOperation.Resume, WithPhase(current, GoalPhase.Active), GoalActivation.Armed);
    }

    public GoalView Complete(IAgent agent, GoalRef reference)
        => Transition(agent, reference, GoalOperation.Complete, [GoalPhase.Active, GoalPhase.Paused, GoalPhase.Blocked], GoalPhase.Complete, GoalActivation.Disarmed);

    public GoalView Block(IAgent agent, GoalRef reference, GoalBlockReason reason)
    {
        var (state, runtime) = PrepareMutation(agent);
        var currentState = ExpectCurrent(state, reference);
        var current = currentState.Goal;
        if (current.Phase != GoalPhase.Active)
            throw TransitionError(current, GoalOperation.Block, [GoalPhase.Active]);
        var goal = WithPhase(current, GoalPhase.Blocked) with { BlockedReason = ResolveBlockReason(reason) };
        return CommitCurrent(agent, currentState, runtime, GoalOperation.Block, goal, GoalActivation.Disarmed);
    }

    public GoalRef Clear(IAgent agent, GoalRef reference)
    {
        var (state, runtime) = PrepareMutation(agent);
        var currentState = ExpectCurrent(state, reference);
        var current = currentState.Goal;
        var tombstone = new GoalRef(current.Id, current.Revision + 1);
        Commit(agent, runtime, new GoalChange.Clear(tombstone, NextMutationTime(currentState)), GoalActivation.Disarmed);
        return tombstone;
    }

    private (GoalProjection? State, GoalRuntimeState Runtime) PrepareMutation(IAgent agent)
    {
        AssertLive(agent);
        return (StateOf(agent.Session), RuntimeState(agent.Session));
    }

    private static GoalProjection ExpectCurrent(GoalProjection? state, GoalRef reference)
    {
        if (state is null)
            throw new GoalException("no current goal", GoalErrorCodes.NotFound);
        var current = state.Goal;
        if (reference.Id != current.Id || reference.Revision != current.Revision)
        {
            throw new GoalException(
                $"stale goal ref \"{reference.Id}\" revision {reference.Revision}; current is \"{current.Id}\" revision {current.Revision}",
                GoalErrorCodes.StaleRevision);
        }
        return state;
    }

    private void AssertLive(IAgent agent)
    {
        var agents = Ctx.Get<AgentRegistry>(AgentRegistry.ServiceName)
            ?? throw new InvalidOperationException("goals requires the agents service");
        if (!ReferenceEquals(agents.Get(agent.Id), agent))
            throw new GoalException($"agent \"{agent.Id}\" is not live in this registry", GoalErrorCodes.AgentNotLive);
    }

    private GoalProjection? StateOf(Session session)
    {
        var projections = Ctx.Get<SessionProjectionRegistry>(SessionProjectionRegistry.ServiceName)
            ?? throw new InvalidOperationException("goal projection is not registered");
        var state = projections.StateOf<GoalProjectionState>(session, GoalProjectionDefinition.Key)
            ?? throw new InvalidOperationException("goal projection is not registered");
        if (state.Failure is not null)
            throw new InvalidOperationException(state.Failure);
        return state.Current;
    }

    private GoalRuntimeState RuntimeState(Session session)
        => _runtimeStates.GetValue(session, static _ => new GoalRuntimeState());

    private static GoalSnapshot WithPhase(GoalSnapshot current, GoalPhase phase)
        => new(current.Id, current.Revision + 1, current.Objective, phase, current.MaxGoalRounds);

    private GoalView Transition(
        IAgent agent,
        GoalRef reference,
        GoalOperation operation,
        IReadOnlyList<GoalPhase> allowed,
        GoalPhase phase,
        GoalActivation activation)
    {
        var (state, runtime) = PrepareMutation(agent);
        var currentState = ExpectCurrent(state, reference);
        var current = currentState.Goal;
        if (!allowed.Contains(current.Phase))
            throw TransitionError(current, operation, allowed);
        return CommitCurrent(agent, currentState, runtime, operation, WithPhase(current, phase), activation);
    }

    private static GoalException TransitionError(GoalSnapshot current, GoalOperation operation, IReadOnlyList<GoalPhase> allowed)
        => new(
            $"cannot {GoalNames.Of(operation)} goal \"{current.Id}\" from phase \"{GoalNames.Of(current.Phase)}\"; expected {string.Join(" or ", allowed.Select(GoalNames.Of))}",
            GoalErrorCodes.InvalidTransition);

    private GoalView CommitCurrent(IAgent agent, GoalProjection state, GoalRuntimeState runtime, GoalOperation operation, GoalSnapshot goal, GoalActivation activation)
        => CommitSnapshot(agent, runtime, operation, goal, state.RoundsStarted, state.CreatedAt, NextMutationTime(state), activation);

    private static long NextMutationTime(GoalProjection state)
        => Math.Max(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), state.UpdatedAt);

    private GoalView CommitSnapshot(IAgent agent, GoalRuntimeState runtime, GoalOperation operation, GoalSnapshot goal, long roundsStarted, long createdAt, long updatedAt, GoalActivation activation)
    {
        Commit(agent, runtime, new GoalChange.Snapshot(operation, goal, roundsStarted, createdAt, updatedAt), activation);
        return new GoalView(goal.Id, goal.Revision, goal.Objective, goal.Phase, goal.MaxGoalRounds, goal.BlockedReason, roundsStarted, createdAt, updatedAt, runtime.Activation);
    }

    private void Commit(IAgent agent, GoalRuntimeState runtime, GoalChange change, GoalActivation activation)
    {
        var reference = GoalFold.ChangeRef(change);
        runtime.PendingActivation = (agent.Session.Seq, activation);
        try
        {
            var sessionEvent = agent.Session.Append(GoalChangePayload.FromChange(change));
            if (runtime.PendingActivation is { } pending && pending.Offset == sessionEvent.Seq)
                runtime.Activation = pending.Activation;
        }
        finally
        {
            runtime.PendingActivation = null;
        }
        var goal = View(StateOf(agent.Session), runtime);
        var notification = new GoalChanged(
            change is GoalChange.Clear ? GoalOperation.Clear : ((GoalChange.Snapshot)change).Operation,
            reference,
            goal);
        new AgentEventDispatch(Ctx, agent).Emit(ChangedEvent, new { Change = notification, Agent = agent });
    }

    private static GoalView? View(GoalProjection? state, GoalRuntimeState runtime)
        => state is null
            ? null
            : new GoalView(
                state.Goal.Id,
                state.Goal.Revision,
                state.Goal.Objective,
                state.Goal.Phase,
                state.Goal.MaxGoalRounds,
                state.Goal.BlockedReason,
                state.RoundsStarted,
                state.CreatedAt,
                state.UpdatedAt,
                runtime.Activation);

    private static string ResolveObjective(string? value)
    {
        if (value is null || value.Trim().Length == 0)
            throw new GoalException("goal objective must be a non-empty string", GoalErrorCodes.InvalidObjective);
        return value.Trim();
    }

    private static long ResolveMaxGoalRounds(long value)
    {
        if (value < 1)
            throw new GoalException("maxGoalRounds must be a positive safe integer", GoalErrorCodes.InvalidMaxRounds);
        return value;
    }

    private static GoalBlockReason ResolveBlockReason(GoalBlockReason reason)
    {
        if (!GoalValidation.BlockCodePattern().IsMatch(reason.Code) || reason.Message.Trim().Length == 0)
        {
            throw new GoalException(
                "goal block reason requires a lower-kebab-case code and a non-empty message",
                GoalErrorCodes.InvalidBlockReason);
        }
        return reason with { Message = reason.Message.Trim() };
    }
}
