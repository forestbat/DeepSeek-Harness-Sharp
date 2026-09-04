using Dsh.Boot;
using Dsh.Core;
using Dsh.Llm;
using Terminal.Gui.App;

namespace Dsh.Tui;

public static class TuiRunner
{
    public static async Task<int> Run(HarnessHome home, string cwd, string? config = null, IReadOnlyList<Dictionary<string, object?>>? patches = null)
    {
        using var app = config is null
            ? HarnessComposer.Compose(new HarnessOptions(home, cwd))
            : await ConfigBoot.Compose(config, new HarnessOptions(home, cwd), patches: patches);
        var agents = app.Ctx.Get<AgentRegistry>(AgentRegistry.ServiceName)!;
        var handle = await agents.Create(new CreateAgentOptions(
            SessionId.Create($"session-{Guid.NewGuid()}"),
            cwd,
            new AgentOptions(app.Provider, app.Model)));
        var agent = (AgentLoopAgent)handle.Agent;
        await agent.WhenIdle();

        Application.Init();
        try
        {
            Application.Run(new ChatWindow(app.Ctx, agent, $"{app.Provider}/{app.Model}"), _ => false);
        }
        finally
        {
            Application.Shutdown();
        }

        var sessions = app.Ctx.Get<SessionStore>(SessionStore.ServiceName)!;
        await sessions.Flush(agent.Session);
        return 0;
    }
}
