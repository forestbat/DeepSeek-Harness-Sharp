using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Cordis;
using Dsh.Core;
using Dsh.Interaction;
using Dsh.Llm;

namespace Dsh.PlanMode;

public sealed record PlanModeConfig
{
    public string Section { get; init; } = "";
}

public sealed class PlanModeController : Service
{
    public const string ServiceName = "planMode";
    public const string ExitPlanMode = "exit_plan_mode";
    public const string PluginName = "dsh-plan-mode";

    private const string ReviewId = "plan-review";
    private const string ApproveLabel = "Approve";
    private const string KeepPlanningLabel = "Keep planning";

    private const string ExitDescription =
        "Use only in plan mode. Present your plan for the user's review and, on approval, leave plan mode. "
        + "Send the COMPLETE plan as markdown, starting with a # heading that names it. "
        + "The user may approve (carry out the plan from your next step) or keep "
        + "planning — their feedback comes back in the tool result; revise and present again.";

    private static readonly Regex PlanHeading = new(@"^#\s+\S", RegexOptions.Compiled);

    private sealed class PendingIntent(bool active, bool narrate)
    {
        public bool Active { get; } = active;
        public bool Narrate { get; } = narrate;
    }

    private sealed class RegistrationBundle(params IDisposable?[] disposables) : IDisposable
    {
        public void Dispose()
        {
            foreach (var disposable in disposables)
                disposable?.Dispose();
        }
    }

    private readonly string _section;
    private readonly ConditionalWeakTable<Session, PendingIntent> _pendingIntents = new();
    private volatile bool _disposed;

    static PlanModeController() => PlanModePayload.RegisterCodec();

    public PlanModeController(Context ctx, PlanModeConfig? config = null) : base(ctx, ServiceName)
    {
        _section = ResolveConfig(config ?? new PlanModeConfig()).Section;

        ctx.On(AgentEventNames.PreStep, (_, args) => PreStep(args), new EventOptions { Global = true });
        ctx.Effect(() => (Action)(() => _disposed = true), $"{PluginName}: close service lifetime");

        var systemPrompt = ctx.Get<SystemPrompt>(SystemPrompt.ServiceName)
            ?? throw new InvalidOperationException("plan-mode requires the systemPrompt service");
        systemPrompt.Section(new PromptSection("plan:policy", PromptOrders.PlanPolicy, context =>
        {
            if (context.Agent is null)
                return "";
            var session = context.Agent.Session;
            var pending = _pendingIntents.TryGetValue(session, out var intent) ? intent.Active : LoggedActive(session);
            return pending ? _section : "";
        }));

        var projections = ctx.Get<SessionProjectionRegistry>(SessionProjectionRegistry.ServiceName)
            ?? throw new InvalidOperationException("plan-mode requires the sessionProjections service");
        projections.Register(PlanProjectionDefinition.Instance);

        var commands = ctx.Get<CommandsService>(CommandsService.ServiceName, false);
        if (commands is not null)
            commands.Register(new CommandDefinition
            {
                Name = "plan",
                Description = "Enter or leave plan mode",
                Input = new CommandInputDescriptor("[off|message]", Images: true),
                Handler = invocation => Task.FromResult(HandleCommand(invocation)),
            });

        var tools = ctx.Get<ToolRuntime>(ToolRuntime.ServiceName)
            ?? throw new InvalidOperationException("plan-mode requires the tools service");
        tools.Register(new ToolDefinition
        {
            Name = ExitPlanMode,
            Description = ExitDescription,
            Parameters = JsonNode.Parse("""
                {
                  "type": "object",
                  "additionalProperties": false,
                  "properties": {
                    "plan": { "type": "string", "description": "The complete plan, as markdown, starting with a # heading that names it." }
                  },
                  "required": ["plan"]
                }
                """)!.AsObject(),
            Output = new ToolOutputDefinition(
                JsonNode.Parse("""
                    {
                      "type": "object",
                      "additionalProperties": false,
                      "required": ["approved"],
                      "properties": {
                        "approved": { "type": "boolean", "const": true }
                      }
                    }
                    """)!.AsObject(),
                (_, _) => [new TextBlock("Plan approved — plan mode exited; carry out the plan starting with your next step.")]),
            Execute = (args, exec) => ExecuteExitTool(args, exec),
        });
    }

    public static PlanModeController Register(Context ctx, PlanModeConfig? config = null) => new(ctx, config);

    public PlanProjectionView Get(IAgent agent)
    {
        var active = LoggedActive(agent.Session);
        var pending = _pendingIntents.TryGetValue(agent.Session, out var intent)
            ? new PlanProjectionView(active, intent.Active != active)
            : new PlanProjectionView(active, false);
        return pending;
    }

    public string Set(IAgent agent, bool active)
    {
        var session = agent.Session;
        var pending = _pendingIntents.TryGetValue(session, out var intent) ? intent : null;
        var target = pending?.Active ?? LoggedActive(session);
        if (active == target)
            return "noop";
        if (HasOpenTurn(session))
        {
            _pendingIntents.Remove(session);
            _pendingIntents.Add(session, new PendingIntent(active, true));
            return LoggedActive(session) == active ? "cancelled" : "queued";
        }
        if (active == LoggedActive(session))
        {
            _pendingIntents.Remove(session);
            return "cancelled";
        }
        session.Append(new PlanModePayload(active));
        _pendingIntents.Remove(session);
        var narration = Narration(session, active);
        if (narration is not null)
            agent.Inject(narration);
        return "committed";
    }

    private async ValueTask<object?> PreStep(object?[] args)
    {
        var payload = (PreStepPayload)args[0]!;
        var next = (Func<ValueTask<object?>>)args[1]!;
        var decision = await next();
        if (decision is PreStepDecision.Reject || payload.Signal.IsCancellationRequested)
            return decision;
        if (!_pendingIntents.TryGetValue(payload.Agent.Session, out var pending))
            return decision;
        var narration = Narration(payload.Agent.Session, pending.Active);
        try
        {
            OnBoundary(payload.Agent.Session);
        }
        catch (Exception error)
        {
            Ctx.LoggerFor(PluginName).Warn($"dsh-plan-mode: failed to append selected plan mode at step start: {error.Message}");
            return decision;
        }
        if (!pending.Narrate || narration is null)
            return decision;
        return decision is PreStepDecision.Enter enter
            ? new PreStepDecision.Enter([.. enter.Messages, narration], enter.StartsRequestSeries)
            : decision;
    }

    private void OnBoundary(Session session)
    {
        if (!_pendingIntents.TryGetValue(session, out var pending))
            return;
        var target = pending.Active;
        if (target == LoggedActive(session))
        {
            _pendingIntents.Remove(session);
            return;
        }
        session.Append(new PlanModePayload(target));
        _pendingIntents.Remove(session);
    }

    private UserMessage? Narration(Session session, bool target)
    {
        var told = LoggedActiveAtLastHeader(session);
        if (told is null || told == target)
            return null;
        var text = target
            ? "The user switched this session to plan mode."
            : "The user switched this session back to the default mode.";
        return MessageFactory.CreateUserMessage(
            [new TextBlock(text)],
            new PluginMessageSource("plan-mode", ContextForms.Notice, Summary: text));
    }

    private bool LoggedActive(Session session) => PlanState(session).Active;

    private bool HasOpenTurn(Session session)
    {
        var state = Projections().StateOf<TurnBoundaryProjection>(session, TurnBoundaryProjectionDefinition.Key);
        if (state is null)
            throw new InvalidOperationException("plan-mode requires the turnBoundary session projection");
        return state.OpenTurnStartSeq is not null;
    }

    private bool? LoggedActiveAtLastHeader(Session session) => PlanState(session).ActiveAtLastHeader;

    private PlanUnitState PlanState(Session session)
        => Projections().StateOf<PlanUnitState>(session, PlanProjectionDefinition.Key)
            ?? throw new InvalidOperationException("plan-mode requires the plan session projection");

    private SessionProjectionRegistry Projections()
        => Ctx.Get<SessionProjectionRegistry>(SessionProjectionRegistry.ServiceName)
            ?? throw new InvalidOperationException("plan-mode requires the sessionProjections service");

    private CommandResult HandleCommand(CommandInvocation invocation)
    {
        var message = invocation.RawInput.Trim();
        if (message == "off")
        {
            return Set(invocation.Agent, false) switch
            {
                "committed" => new CommandResult.Success("Plan mode off."),
                "queued" => new CommandResult.Success("Leaving plan mode (applies from the next step)."),
                "cancelled" => new CommandResult.Success("Plan mode entry cancelled."),
                "noop" => LoggedActive(invocation.Agent.Session)
                    ? new CommandResult.Success("Leaving plan mode (applies from the next step).")
                    : new CommandResult.Success("Plan mode is already inactive."),
                _ => throw new InvalidOperationException("unknown plan mode set outcome"),
            };
        }
        var outcome = Set(invocation.Agent, true);
        if (message.Length > 0)
            invocation.Agent.Steer(MessageFactory.CreateUserText(message));
        return new CommandResult.Success(outcome == "committed"
            ? "Plan mode on. Use /plan off to leave."
            : "Entering plan mode (applies from the next step). Use /plan off to leave.");
    }

    private async Task<object?> ExecuteExitTool(JsonElement args, ToolRunContext exec)
    {
        var agent = exec.Agent;
        if (agent is null)
            throw new InvalidOperationException($"{ExitPlanMode} requires a calling agent (no session to switch)");
        if (!LoggedActive(agent.Session))
            throw new InvalidOperationException($"{ExitPlanMode} is only available in plan mode");
        if (!args.TryGetProperty("plan", out var planElement) || planElement.ValueKind != JsonValueKind.String)
            throw new InvalidOperationException($"{ExitPlanMode} requires a non-empty markdown plan starting with a # heading");
        var plan = planElement.GetString() ?? "";
        if (!PlanHeading.IsMatch(plan.Trim()))
            throw new InvalidOperationException($"{ExitPlanMode} requires a non-empty markdown plan starting with a # heading");
        var interaction = Ctx.Get<UserQuestionService>(UserQuestionService.ServiceName, false);
        if (interaction is null)
            throw new InvalidOperationException("no user-questions channel is available to review the plan; ask the user to switch the session mode instead");
        AskUserQuestionAnswer answer;
        try
        {
            answer = await interaction.Ask(new AskUserQuestionRequest(
            [
                new AskUserQuestionItem(
                    ReviewId,
                    "Approve this plan and leave plan mode?",
                    Detail: plan,
                    Header: "Plan review",
                    Options:
                    [
                        new AskUserQuestionOption(ApproveLabel, "Leave plan mode; the plan is carried out from the next step."),
                        new AskUserQuestionOption(KeepPlanningLabel, "Stay in plan mode; feedback goes back to the model."),
                    ],
                    Intent: new AskUserQuestionIntent(AskUserQuestionIntentKind.PlanReview, ApproveLabel)),
            ], agent), exec.Signal);
        }
        catch (UserQuestionException error) when (error.Code == "ASK_CANCELLED")
        {
            throw new InvalidOperationException(
                "The user dismissed the plan review to speak instead; stay in plan mode, stop here, and wait for their message.");
        }
        if (_disposed)
            throw new InvalidOperationException("the plan-mode service was reloaded while the plan was under review; present the plan again");
        var item = answer.Answers.Count(entry => entry.Id == ReviewId) == 1 ? answer.Answers.First(entry => entry.Id == ReviewId) : null;
        if (item is null || item.Selected.Count != 1 || item.Selected[0] != ApproveLabel || item.Custom is not null)
        {
            var feedback = item?.Custom ?? "";
            throw new InvalidOperationException(feedback.Length == 0
                ? "The user chose to keep planning; revise the plan and present it again."
                : $"The user chose to keep planning; their feedback: {feedback}");
        }
        _pendingIntents.Remove(agent.Session);
        _pendingIntents.Add(agent.Session, new PendingIntent(false, false));
        return JsonDocument.Parse("""{"approved":true}""").RootElement;
    }

    private static PlanModeConfig ResolveConfig(PlanModeConfig config)
    {
        if (string.IsNullOrWhiteSpace(config.Section))
            throw new ArgumentException("PlanModeConfig needs a non-empty string `section`");
        return config;
    }
}
