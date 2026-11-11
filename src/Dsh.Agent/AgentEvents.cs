using Dsh.Runtime;
using Dsh.Runtime.Events;
using Dsh.Llm;
using Dsh.Core;

namespace Dsh.Agent;

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

    public void Emit<TNotification>(TNotification notification) where TNotification : INotification
        => ctx.Events.Emit(Carrier, notification);

    public async ValueTask<object?> Serial<TNotification>(TNotification notification) where TNotification : INotification
        => await ctx.Events.Serial(Carrier, notification);

    public async ValueTask<object?> Waterfall<TNotification>(TNotification notification, Func<ValueTask<object?>> inner)
        where TNotification : INotification
        => await ctx.Events.Waterfall(Carrier, notification, inner);
}

