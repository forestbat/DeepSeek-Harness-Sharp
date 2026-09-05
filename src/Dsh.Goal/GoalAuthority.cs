using Cordis;
using Dsh.Core;
using Dsh.Llm;

namespace Dsh.Goal;

public sealed record GoalToolExecution(IAgent Agent, IReadOnlyList<SessionEvent> Events, long OpenTurnStartSeq);

public abstract record GoalToolAuthority
{
    public sealed record DirectHuman : GoalToolAuthority;

    public sealed record GoalRound(GoalView Goal) : GoalToolAuthority;
}

public static class GoalAuthority
{
    public const string AuthorityRequiredCode = "GOAL_TOOL_AUTHORITY_REQUIRED";
    public const string DriverRequiredCode = "GOAL_TOOL_DRIVER_REQUIRED";
    public const string AgentRequiredCode = "GOAL_TOOL_AGENT_REQUIRED";

    public static GoalToolExecution Execution(Context ctx, ToolRunContext exec)
    {
        var agent = exec.Agent;
        if (agent is null)
            throw new HarnessException("goal tools require a calling agent", AgentRequiredCode);
        var agents = ctx.Get<AgentRegistry>(AgentRegistry.ServiceName)
            ?? throw new InvalidOperationException("goal tools require the agents service");
        if (!ReferenceEquals(agents.Get(agent.Id), agent) || agent.Status != AgentStatus.Running
            || !ReferenceEquals(agents.CurrentInitiator(), agent))
        {
            throw new HarnessException(
                "goal tools require the exact live calling agent inside its active driver",
                DriverRequiredCode);
        }
        var projections = ctx.Get<SessionProjectionRegistry>(SessionProjectionRegistry.ServiceName)
            ?? throw new InvalidOperationException("goal tools require the sessionProjections service");
        var boundary = projections.StateOf<TurnBoundaryProjection>(agent.Session, TurnBoundaryProjectionDefinition.Key);
        if (boundary?.OpenTurnStartSeq is not { } openTurnStartSeq)
            throw new HarnessException("goal tools require an open model turn", DriverRequiredCode);
        return new GoalToolExecution(agent, agent.Session.SnapshotEvents(), openTurnStartSeq);
    }

    public static void RequireDirectHuman(Context ctx, GoalToolExecution execution)
    {
        if (!HasDirectHumanInput(ctx, execution))
        {
            throw new HarnessException(
                "this goal operation requires a direct human turn on a top-level agent",
                AuthorityRequiredCode);
        }
    }

    public static GoalToolAuthority Completion(Context ctx, GoalToolExecution execution)
    {
        if (HasDirectHumanInput(ctx, execution))
            return new GoalToolAuthority.DirectHuman();
        var goals = ctx.Get<GoalService>(GoalService.ServiceName)
            ?? throw new InvalidOperationException("goal tools require the goals service");
        var goal = goals.Get(execution.Agent);
        if (goal is not null && IsMatchingGoalRound(execution, goal))
            return new GoalToolAuthority.GoalRound(goal);
        throw new HarnessException(
            "complete and blocked require a direct human turn or the current goal round",
            AuthorityRequiredCode);
    }

    private static bool HasDirectHumanInput(Context ctx, GoalToolExecution execution)
    {
        var agents = ctx.Get<AgentRegistry>(AgentRegistry.ServiceName)
            ?? throw new InvalidOperationException("goal tools require the agents service");
        if (!agents.Roots().Contains(execution.Agent))
            return false;
        return SomeOpenTurnEvent(execution, sessionEvent
            => sessionEvent.Data is UserMessagePayload { Message.Source: UserMessageSource });
    }

    private static bool IsMatchingGoalRound(GoalToolExecution execution, GoalView goal)
        => SomeOpenTurnEvent(execution, sessionEvent
            => sessionEvent.Data is UserMessagePayload { Message.Source: GoalMessageSource source }
            && source.GoalId == goal.Id.Value
            && source.Revision == goal.Revision
            && source.Round == goal.RoundsStarted);

    private static bool SomeOpenTurnEvent(GoalToolExecution execution, Func<SessionEvent, bool> predicate)
    {
        for (var seq = execution.OpenTurnStartSeq + 1; seq < execution.Events.Count; seq++)
        {
            if (predicate(execution.Events[(int)seq]))
                return true;
        }
        return false;
    }
}
