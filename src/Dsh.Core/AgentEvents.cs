using Cordis;
using Dsh.Llm;

namespace Dsh.Core;

public sealed class PreStepPayload(
    IAgent agent,
    List<UserMessage> messages,
    int turn,
    int step,
    CancellationToken signal)
{
    public IAgent Agent { get; } = agent;
    public List<UserMessage> Messages { get; set; } = messages;
    public int Turn { get; } = turn;
    public int Step { get; } = step;
    public CancellationToken Signal { get; } = signal;
}

public abstract record PreStepDecision
{
    public sealed record Reject : PreStepDecision;

    public sealed record Enter(List<UserMessage> Messages, bool StartsRequestSeries = false) : PreStepDecision;
}

public sealed class AgentRequestPayload(IAgent agent, int turn, int step, CancellationToken signal)
{
    public IAgent Agent { get; } = agent;
    public int Turn { get; } = turn;
    public int Step { get; } = step;
    public CancellationToken Signal { get; } = signal;
}

public sealed class AgentRequestErrorPayload(
    IAgent agent,
    int turn,
    int step,
    string provider,
    LlmFailure failure,
    ResolvedRetryPolicy? retryPolicy,
    CancellationToken signal)
{
    public IAgent Agent { get; } = agent;
    public int Turn { get; } = turn;
    public int Step { get; } = step;
    public string Provider { get; } = provider;
    public LlmFailure Failure { get; } = failure;
    public ResolvedRetryPolicy? RetryPolicy { get; } = retryPolicy;
    public CancellationToken Signal { get; } = signal;
}

public abstract record RequestErrorAction
{
    public sealed record Retry : RequestErrorAction;
}

public sealed class AgentTurnStoppingPayload(IAgent agent, int turn, CancellationToken signal)
{
    public IAgent Agent { get; } = agent;
    public int Turn { get; } = turn;
    public CancellationToken Signal { get; } = signal;
}

public sealed class AgentEventDispatch(Context ctx, IAgent agent)
{
    private Context Carrier => DshScope.ScopeTarget(ctx, agent.ScopeKey);

    public void Emit(string name, object payload)
        => ctx.Events.Emit(Carrier, name, payload);

    public async ValueTask<object?> Serial(string name, object payload)
        => await ctx.Events.Serial(Carrier, name, payload);

    public async ValueTask<object?> Waterfall(string name, object payload, Func<ValueTask<object?>> inner)
        => await ctx.Events.Waterfall(Carrier, name, [payload], inner);
}

public static class AgentEventNames
{
    public const string Created = "agent/created";
    public const string Disposed = "agent/disposed";
    public const string Status = "agent/status";
    public const string InboxInserted = "agent/inbox/inserted";
    public const string InboxClaimed = "agent/inbox/claimed";
    public const string InboxDiscarded = "agent/inbox/discarded";
    public const string SessionStart = "agent/session-start";
    public const string PreStep = "agent/pre-step";
    public const string Request = "agent/request";
    public const string RequestError = "agent/request-error";
    public const string TurnStopping = "agent/turn-stopping";
    public const string Error = "agent/error";
}

public sealed record InboxSplicePayload(
    string Target,
    long Start,
    long? RemovedCount,
    IReadOnlyList<UserMessage> Inserted,
    string? Outcome = null) : SessionEventPayload
{
    public override string Type => SessionEventTypes.AgentInboxSpliced;
}
