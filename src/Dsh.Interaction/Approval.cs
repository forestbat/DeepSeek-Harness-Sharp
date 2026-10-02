using Dsh.Plugins;
using Dsh.Runtime;
using Dsh.Runtime.Events;
using Dsh.Core;
using Dsh.Llm;

namespace Dsh.Interaction;

public enum ApprovalPolicy
{
    Ask,
    Never,
    Auto,
}

public static class ApprovalEvents
{
    public const string Asked = "approval/asked";
    public const string Decided = "approval/decided";
    public const string Policy = "approval/policy";
    public const string Granted = "approval/granted";
}

public sealed record ApprovalAskedPayload(
    string Id,
    string ToolName,
    ToolCallId? CallId = null,
    string? Reason = null,
    string? Arguments = null)
    : SessionEventPayload
{
    public override string Type => ApprovalEvents.Asked;
}

public sealed record ApprovalDecidedPayload(string Id, ApprovalOutcome Outcome) : SessionEventPayload
{
    public override string Type => ApprovalEvents.Decided;
}

/** 用户在弹窗里选择「本会话内不再询问」后写入; 同一会话内该工具直接放行。 */
public sealed record ApprovalGrantedPayload(string ToolName) : SessionEventPayload
{
    public override string Type => ApprovalEvents.Granted;
}

public sealed record ApprovalPolicyPayload(ApprovalPolicy Policy, string? Source = null) : SessionEventPayload
{
    public override string Type => ApprovalEvents.Policy;
}

public sealed record ApprovalConfig(ApprovalPolicy Policy = ApprovalPolicy.Ask);

public sealed class ApprovalService : Service, IApprovalService
{
    public const string ServiceName = "approval";

    private const string NeverSentence = "Approval prompts are disabled in this session: actions that require approval are rejected automatically — do not request sandbox escalation (do not set `sandbox_permissions`).";
    private const string AskSentence = "Approval policy: ask. Operations that require approval may ask through the configured answerers; without an available answerer, the request fails closed.";
    private const string AutoSentence = "Approval policy: full access. Operations that require approval are approved automatically in this session (still subject to safety.blacklist); ask the user before anything destructive.";

    private readonly ApprovalConfig _config;

    public ApprovalService(Context ctx, ApprovalConfig? config = null) : base(ctx, ServiceName)
    {
        _config = config ?? new ApprovalConfig();
        var systemPrompt = ctx.Get<SystemPrompt>(SystemPrompt.ServiceName, false);
        systemPrompt?.Context(PromptContext.Literal(
            "approval:policy",
            _config.Policy switch
            {
                ApprovalPolicy.Never => NeverSentence,
                ApprovalPolicy.Auto => AutoSentence,
                _ => AskSentence,
            }));
    }

    public static ApprovalService Register(Context ctx, ApprovalConfig? config = null) => new(ctx, config);

    public static void SetApprovalPolicy(Session session, ApprovalPolicy policy)
        => session.Append(new ApprovalPolicyPayload(policy));

    public static ApprovalPolicy? OverrideOf(Session session)
    {
        for (var seq = session.Seq - 1; seq >= 0; seq--)
        {
            if (session.EventAt(seq)?.Data is ApprovalPolicyPayload payload)
                return payload.Policy;
        }
        return null;
    }

    /** 会话级授权: 只要该工具在本会话被「总是允许」过, 后续调用不再询问。 */
    public static bool HasGrant(Session session, string toolName)
    {
        for (var seq = session.Seq - 1; seq >= 0; seq--)
        {
            if (session.EventAt(seq)?.Data is ApprovalGrantedPayload payload
                && string.Equals(payload.ToolName, toolName, StringComparison.Ordinal))
            {
                return true;
            }
        }
        return false;
    }

    public ApprovalPolicy EffectivePolicy(Session session) => OverrideOf(session) ?? _config.Policy;

    public void SetPolicy(IAgent agent, ApprovalPolicy policy)
    {
        var previous = EffectivePolicy(agent.Session);
        if (previous == policy)
            return;
        SetApprovalPolicy(agent.Session, policy);
        agent.Inject(MessageFactory.CreateUserText(
            $"The approval policy changed from \"{WireName(previous)}\" to \"{WireName(policy)}\" (changed by the user).",
            new PluginMessageSource("user-approval")));
    }

    public async Task<ApprovalOutcome> Request(ApprovalRequest request, CancellationToken signal)
    {
        var session = request.Agent.Session;
        if (!HasOpenTurn(session))
        {
            throw new InvalidOperationException(
                "approval.request() outside an open turn: the approval/asked + approval/decided audit pair "
                + "must be turn-enclosed (a bare event between turns is crash-tail garbage on reload). "
                + "Ask from inside the turn that needs the decision.");
        }
        var id = Guid.NewGuid().ToString("N");
        session.Append(new ApprovalAskedPayload(id, request.ToolName, request.CallId, request.Reason, request.Arguments));
        var outcome = await Decide(request, session, signal);
        session.Append(new ApprovalDecidedPayload(id, outcome));
        if (outcome == ApprovalOutcome.AllowedForSession)
            session.Append(new ApprovalGrantedPayload(request.ToolName));
        return outcome;
    }

    private async Task<ApprovalOutcome> Decide(ApprovalRequest request, Session session, CancellationToken signal)
    {
        if (signal.IsCancellationRequested)
            return ApprovalOutcome.Cancelled;
        if (EffectivePolicy(session) == ApprovalPolicy.Never)
            return ApprovalOutcome.Rejected;
        if (EffectivePolicy(session) == ApprovalPolicy.Auto || HasGrant(session, request.ToolName))
            return ApprovalOutcome.AllowedOnce;
        var answer = DispatchAnswerers(request);
        if (!signal.CanBeCanceled)
            return await answer;
        var cancelled = new TaskCompletionSource<ApprovalOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var registration = signal.Register(
            static state => ((TaskCompletionSource<ApprovalOutcome>)state!).TrySetResult(ApprovalOutcome.Cancelled),
            cancelled);
        var completed = await Task.WhenAny(answer, cancelled.Task);
        return completed == answer ? await answer : ApprovalOutcome.Cancelled;
    }

    private async Task<ApprovalOutcome> DispatchAnswerers(ApprovalRequest request)
    {
        try
        {
            var carrier = DshScope.ScopeTarget(Ctx, request.Agent.ScopeKey);
            var result = await Ctx.Events.Waterfall(
                carrier, new ApprovalRequestNotification(request),
                () => new ValueTask<object?>(ApprovalOutcome.Unavailable));
            return result is ApprovalOutcome outcome ? outcome : ApprovalOutcome.Unavailable;
        }
        catch
        {
            return ApprovalOutcome.Unavailable;
        }
    }

    private static bool HasOpenTurn(Session session)
    {
        for (var seq = session.Seq - 1; seq >= 0; seq--)
        {
            switch (session.EventAt(seq)?.Type)
            {
                case SessionEventTypes.TurnStart:
                    return true;
                case SessionEventTypes.TurnEnd:
                    return false;
            }
        }
        return false;
    }

    private static string WireName(ApprovalPolicy policy) => policy switch
    {
        ApprovalPolicy.Never => "never",
        ApprovalPolicy.Auto => "full access",
        _ => "ask",
    };
}

public static class ApprovalAnswerers
{
    public static IDisposable AutoApprove(Context ctx)
        => Answerer(ctx, ApprovalOutcome.AllowedOnce);

    public static IDisposable DenyAll(Context ctx)
        => Answerer(ctx, ApprovalOutcome.Rejected);

    public static IDisposable AutoApproveScoped(Context scopeCtx)
    {
        var remove = scopeCtx.OnWaterfall<ApprovalRequestNotification>(
            (_, _) => ValueTask.FromResult<object?>(ApprovalOutcome.AllowedOnce),
            new EventOptions { Prepend = true });
        return new DisposeAction(() => remove());
    }

    private static DisposeAction Answerer(Context ctx, ApprovalOutcome outcome)
    {
        var remove = ctx.OnWaterfall<ApprovalRequestNotification>(
            (_, _) => ValueTask.FromResult<object?>(outcome),
            new EventOptions { Global = true });
        return new DisposeAction(() => remove());
    }
}

internal sealed class DisposeAction(Action dispose) : IDisposable
{
    private bool _disposed;

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        dispose();
    }
}

internal static class ApprovalCodecRegistration
{
    // 启动时由生成目录显式调用：审批事件可能在 ApprovalService 构造之前被日志读写。
    [DshPluginInitializer]
    internal static void Register()
    {
        SessionEventCodec.Register<ApprovalAskedPayload>(ApprovalEvents.Asked);
        SessionEventCodec.Register<ApprovalDecidedPayload>(ApprovalEvents.Decided);
        SessionEventCodec.Register<ApprovalPolicyPayload>(ApprovalEvents.Policy);
        SessionEventCodec.Register<ApprovalGrantedPayload>(ApprovalEvents.Granted);
    }
}
