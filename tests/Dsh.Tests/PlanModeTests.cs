using Cordis;
using Dsh.Core;
using Dsh.Interaction;
using Dsh.Llm;
using Dsh.PlanMode;

namespace Dsh.Tests;

public class PlanModeTests
{
    private sealed class Harness : IDisposable
    {
        public Context Ctx { get; } = new();
        public ToolRuntime Tools { get; }
        public SessionProjectionRegistry Projections { get; }

        public Harness()
        {
            _ = new SystemPrompt(Ctx, new SystemPromptConfig());
            Tools = new ToolRuntime(Ctx);
            Projections = new SessionProjectionRegistry(Ctx);
            Projections.Register(TurnBoundaryProjectionDefinition.Instance);
            _ = new PlanModeController(Ctx, new PlanModeConfig { Section = "Plan carefully." });
        }

        public void Dispose()
        {
        }
    }

    private sealed class FakeAgent : IAgent
    {
        public FakeAgent(Context ctx)
        {
            Ctx = ctx;
            var id = SessionId.Create($"session-{Guid.NewGuid():N}");
            Session = Session.Create(id, null, new SessionHeader
            {
                Version = SessionHeader.SessionFormatVersion,
                Id = id,
                CreatedAt = 0,
                Cwd = Path.GetTempPath(),
                IsSeeded = false,
            });
        }

        public SessionId Id => Session.Id;
        public Session Session { get; }
        public ScopeKey ScopeKey { get; } = new();
        public Context Ctx { get; }
        public AgentStatus Status { get; set; } = AgentStatus.Running;
        public AgentOptions Options { get; } = new();

        public void Cancel(AgentCancelCause cause, bool keepInbox = false) { }
        public Task WhenIdle() => Task.CompletedTask;
        public void Send(UserMessage message, string target, bool wakeup) { }
        public void Followup(UserMessage message) { }
        public void Steer(UserMessage message) { }
        public void Inject(UserMessage message) { }
    }

    [Fact]
    public void Projection_Folds_Command_Lifecycle_And_Mode()
    {
        var state = new PlanUnitState();
        state = PlanProjectionDefinition.Instance.Apply(state, new SessionEvent
        {
            Type = CommandEvents.Run,
            Seq = 1,
            Time = 1,
            Data = new CommandRunPayload("cmd-1", "plan", "on", "user"),
        });
        Assert.True(state.Running is { Wanted: true });

        state = PlanProjectionDefinition.Instance.Apply(state, new SessionEvent
        {
            Type = CommandEvents.Done,
            Seq = 2,
            Time = 2,
            Data = new CommandDonePayload("cmd-1", "success", "Plan mode on."),
        });
        Assert.True(state.Wanted);
        Assert.Null(state.Running);
        Assert.True(PlanProjectionDefinition.View(state).Pending);

        state = PlanProjectionDefinition.Instance.Apply(state, new SessionEvent
        {
            Type = "plan/mode",
            Seq = 3,
            Time = 3,
            Data = new PlanModePayload(true),
        });
        Assert.True(state.Active);
        Assert.False(PlanProjectionDefinition.View(state).Pending);
    }

    [Fact]
    public void Set_With_No_Open_Turn_Commits_Immediately()
    {
        using var harness = new Harness();
        var agent = new FakeAgent(harness.Ctx);

        var outcome = harness.Ctx.Get<PlanModeController>(PlanModeController.ServiceName)!.Set(agent, true);

        Assert.Equal("committed", outcome);
        Assert.True(harness.Ctx.Get<PlanModeController>(PlanModeController.ServiceName)!.Get(agent).Active);
        Assert.Contains(agent.Session.SnapshotEvents(), e => e.Data is PlanModePayload { Active: true });
    }

    [Fact]
    public void Set_With_Open_Turn_Queues_Pending_Selection()
    {
        using var harness = new Harness();
        var agent = new FakeAgent(harness.Ctx);
        agent.Session.Append(new TurnStartPayload(1));

        var controller = harness.Ctx.Get<PlanModeController>(PlanModeController.ServiceName)!;
        var outcome = controller.Set(agent, true);

        Assert.Equal("queued", outcome);
        Assert.False(controller.Get(agent).Active);
        Assert.DoesNotContain(agent.Session.SnapshotEvents(), e => e.Data is PlanModePayload);
    }

    [Fact]
    public async Task ExitTool_Rejects_When_Plan_Mode_Is_Inactive()
    {
        using var harness = new Harness();
        var agent = new FakeAgent(harness.Ctx);

        var result = await harness.Tools.Execute(new ToolExecutionInput
        {
            CallId = ToolCallId.Create("call-exit"),
            Name = PlanModeController.ExitPlanMode,
            Arguments = System.Text.Json.JsonDocument.Parse("""{"plan":"# Do it\nSteps."}""").RootElement,
            Agent = agent,
            Signal = default,
        });

        Assert.True(result.IsError);
        Assert.Contains(PlanModeController.ExitPlanMode, ((ToolExecutionResult.Failure)result).Error.Message);
    }
}
