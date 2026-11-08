using Dsh.Core;
using Dsh.Llm;
using Dsh.Runtime;

namespace Dsh.Tests;

internal static class InspectionTestPlugin
{
    public static PluginDefinition Definition(string package)
    {
        var plugin = new Dsh.Inspection.Plugin(package);
        return new PluginDefinition
        {
            Name = package,
            Inject = plugin.Inject,
            Apply = (ctx, config, _) => Task.FromResult<object?>(plugin.Apply(ctx, config)),
        };
    }
}

internal sealed class InspectionTestAgent : IAgent
{
    private InspectionTestAgent(Context ctx)
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

    public static InspectionTestAgent Open(Context ctx)
    {
        var agent = new InspectionTestAgent(ctx);
        agent.Session.Append(new TurnStartPayload(1));
        return agent;
    }

    public SessionId Id => Session.Id;
    public Session Session { get; }
    public ScopeKey ScopeKey { get; } = new();
    public Context Ctx { get; }
    public AgentStatus Status => AgentStatus.Idle;
    public AgentOptions Options { get; } = new();

    public void Cancel(AgentCancelCause cause, bool keepInbox = false) { }
    public Task WhenIdle() => Task.CompletedTask;
    public void Send(UserMessage message, string target, bool wakeup) { }
    public void Followup(UserMessage message) { }
    public void Steer(UserMessage message) { }
    public void Inject(UserMessage message) { }
}
