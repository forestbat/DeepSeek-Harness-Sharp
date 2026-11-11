using System.Text.Json;
using Dsh.Boot;
using Dsh.Core;
using Dsh.Interaction;
using Dsh.Llm;
using Dsh.Runtime;
using Dsh.Agent;

namespace Dsh.Tests;

internal sealed class ReplacementFixture : IDisposable
{
    public ReplacementFixture(SafetySettings safety)
    {
        Root = TempTree.CreateDirectory("tool-registration-policy");
        Workspace = Directory.CreateDirectory(Path.Combine(Root, "workspace")).FullName;
        Target = Path.Combine(Workspace, "target.txt");
        File.WriteAllText(Target, "private-target-body");
        Context = new Context();
        _ = new SystemPrompt(Context, new SystemPromptConfig());
        _ = new LlmRuntime(Context);
        Tools = new ToolRuntime(Context);
        _ = ApprovalService.Register(Context);
        Settings = new HarnessSettings { Safety = safety };
        _ = new SafetyPolicySource(Context, () => Settings);
        var id = SessionId.Create(Guid.NewGuid().ToString("N"));
        Session = Session.Create(id, null, new SessionHeader
        {
            Version = SessionHeader.SessionFormatVersion, Id = id, Cwd = Workspace, CreatedAt = 0, IsSeeded = false,
        });
        Session.Append(new TurnStartPayload(1));
        Agent = new AgentLoopAgent(Context, id, new AgentOptions(), Session, _ => 0);
    }

    public string Root { get; }
    public string Workspace { get; }
    public string Target { get; }
    public Context Context { get; }
    public ToolRuntime Tools { get; }
    public HarnessSettings Settings { get; }
    public Session Session { get; }
    public AgentLoopAgent Agent { get; }

    public ToolExecutionInput Input(string name, object args) => new()
    {
        CallId = ToolCallId.Create(Guid.NewGuid().ToString("N")), Name = name,
        Arguments = JsonSerializer.SerializeToElement(args), Agent = Agent, Signal = TestContext.Current.CancellationToken,
    };

    public async Task<ScheduledToolPreparation.Dispatch> Prepare(string name, object args)
        => Assert.IsType<ScheduledToolPreparation.Dispatch>(await Tools.PrepareScheduledExecution(Input(name, args)));

    public async Task<ToolExecutionResult> Complete(ScheduledToolPreparation.Dispatch prepared)
        => (await Tools.DispatchScheduledExecution(prepared.Exec)) switch
        {
            ScheduledToolDispatch.PostResult post => await Tools.FinalizeScheduledExecution(prepared.Exec, post.Result),
            ScheduledToolDispatch.FinalResult final => await Tools.FinishScheduledExecution(prepared.Exec, final.Result),
            _ => throw new InvalidOperationException("Unexpected scheduled dispatch result."),
        };

    public void Dispose() { Agent.Dispose(); TempTree.Delete(Root); }
}
