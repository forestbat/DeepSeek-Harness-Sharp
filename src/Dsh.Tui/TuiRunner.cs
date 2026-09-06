using Dsh.Boot;
using Dsh.Core;
using Dsh.Llm;
using Terminal.Gui.App;

namespace Dsh.Tui;

public static class TuiRunner
{
    public static async Task<int> Run(HarnessHome home, string cwd, string? config = null, IReadOnlyList<Dictionary<string, object?>>? patches = null)
    {
        var settings = HarnessSettings.Load(home);
        var options = new HarnessOptions(home, cwd,
            Provider: settings.Provider,
            Model: settings.Model,
            BaseUrl: settings.BaseUrl,
            ApiKey: settings.ApiKey,
            ReasoningEffort: settings.ReasoningEffort);
        using var app = config is null
            ? HarnessComposer.Compose(options)
            : await ConfigBoot.Compose(config, options, patches: patches);
        var agents = app.Ctx.Get<AgentRegistry>(AgentRegistry.ServiceName)!;
        var handle = await agents.Create(new CreateAgentOptions(
            SessionId.Create($"session-{Guid.NewGuid()}"),
            cwd,
            new AgentOptions(app.Provider, app.Model, app.ReasoningEffort is null ? null : ReasoningEffortId.Create(app.ReasoningEffort))));
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
