using Cordis;
using Dsh.Core;
using Dsh.Goal;
using Dsh.Llm;

namespace Dsh.Tests;

public class GoalTests
{
    private sealed class Harness : IDisposable
    {
        public Context Ctx { get; } = new();
        public AgentRegistry Agents { get; }
        public SessionProjectionRegistry Projections { get; }

        public Harness()
        {
            Agents = new AgentRegistry(Ctx);
            Projections = new SessionProjectionRegistry(Ctx);
            _ = new GoalService(Ctx);
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
    public void GoalService_Creates_Reads_And_Completes_Goal()
    {
        using var harness = new Harness();
        var agent = new FakeAgent(harness.Ctx);
        harness.Agents.Register(agent);
        var goals = harness.Ctx.Get<GoalService>(GoalService.ServiceName)!;

        var created = goals.Create(agent, new CreateGoalRequest("Build the release"));
        Assert.Equal(GoalPhase.Active, created.Phase);
        Assert.Equal(1, created.Revision);
        Assert.Equal("Build the release", created.Objective);

        var read = goals.Get(agent);
        Assert.NotNull(read);
        Assert.Equal(created.Id, read!.Id);

        var completed = goals.Complete(agent, new GoalRef(created.Id, created.Revision));
        Assert.Equal(GoalPhase.Complete, completed.Phase);
        Assert.Equal(2, completed.Revision);
        Assert.Equal(GoalPhase.Complete, goals.Get(agent)!.Phase);
    }
}
