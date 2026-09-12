using Dsh.Boot;
using Dsh.Core;
using Dsh.Llm;

namespace Dsh.Tui;

public static class TuiRunner
{
    public static async Task<int> Run(
        HarnessApp app,
        string cwd)
    {
        if (IsGpuRequested())
            return RunGpuSync(app, cwd);

        var agents = app.Ctx.Get<AgentRegistry>(AgentRegistry.ServiceName)!;
        var handle = await agents.Create(new CreateAgentOptions(
            SessionId.Create($"session-{Guid.NewGuid()}"),
            cwd,
            new AgentOptions(app.Provider, app.Model, app.ReasoningEffort is null ? null : ReasoningEffortId.Create(app.ReasoningEffort))));
        var agent = (AgentLoopAgent)handle.Agent;
        await agent.WhenIdle();

        if (Console.IsInputRedirected)
            return await RunNonInteractiveAsync(app, agent);

        return await RunInteractiveAsync(app, agent);
    }

    private static bool IsGpuRequested()
    {
        if (string.Equals(Environment.GetEnvironmentVariable("DSH_TUI_GPU"), "1", StringComparison.Ordinal))
            return true;

        var args = Environment.GetCommandLineArgs();
        for (var i = 1; i < args.Length; i++)
        {
            if (string.Equals(args[i], "--gpu", StringComparison.Ordinal))
                return true;
        }

        return false;
    }

    private static async Task<int> RunNonInteractiveAsync(HarnessApp app, AgentLoopAgent agent)
    {
        using var chat = new ChatWindow(app.Ctx, agent, app.Home, app.Ctx.Get<ISessionPersistence>(Persistence.Plugin.ServiceName));
        chat.DrainUi();
        var size = GetConsoleSize();
        var layout = LayoutEngine.Calculate(size.Width, size.Height);
        var grid = new CellGrid(size.Width, size.Height);
        chat.Draw(grid, layout);
        var renderer = new AnsiRenderer();
        Console.Out.Write(renderer.Render(grid, chat.CursorScreenX, chat.CursorScreenY, forceFull: true));
        Console.Out.Flush();

        var sessions = app.Ctx.Get<SessionStore>(SessionStore.ServiceName)!;
        await sessions.Flush(agent.Session);
        return 0;
    }

    private static async Task<int> RunInteractiveAsync(HarnessApp app, AgentLoopAgent agent)
    {
        SetConsoleInteractive(true);
        using var rawMode = TerminalRawMode.TryEnable();
        var renderer = new AnsiRenderer();
        var grid = new CellGrid(80, 25);
        var forceFull = true;
        using var chat = new ChatWindow(app.Ctx, agent, app.Home, app.Ctx.Get<ISessionPersistence>(Persistence.Plugin.ServiceName));
        try
        {
            while (!chat.ExitRequested)
            {
                chat.DrainUi();
                var size = GetConsoleSize();
                if (grid.Width != size.Width || grid.Height != size.Height)
                {
                    grid = new CellGrid(size.Width, size.Height);
                    forceFull = true;
                }

                var layout = LayoutEngine.Calculate(size.Width, size.Height);
                chat.Draw(grid, layout);
                Console.Out.Write(renderer.Render(grid, chat.CursorScreenX, chat.CursorScreenY, forceFull));
                Console.Out.Flush();
                forceFull = false;

                if (chat.ExitRequested)
                    break;

                var key = Console.ReadKey(true);
                chat.HandleKey(key);
            }
        }
        finally
        {
            SetConsoleInteractive(false);
        }

        var sessions = app.Ctx.Get<SessionStore>(SessionStore.ServiceName)!;
        await sessions.Flush(agent.Session);
        return 0;
    }

    private static int RunGpuSync(HarnessApp app, string cwd)
    {
        try
        {
            Console.Clear();
        }
        catch (IOException)
        {
        }

        var agents = app.Ctx.Get<AgentRegistry>(AgentRegistry.ServiceName)!;
        var handle = agents.Create(new CreateAgentOptions(
            SessionId.Create($"session-{Guid.NewGuid()}"),
            cwd,
            new AgentOptions(app.Provider, app.Model, app.ReasoningEffort is null ? null : ReasoningEffortId.Create(app.ReasoningEffort)))).GetAwaiter().GetResult();
        var agent = (AgentLoopAgent)handle.Agent;
        agent.WhenIdle().GetAwaiter().GetResult();

        try
        {
            using var chat = new ChatWindow(app.Ctx, agent, app.Home, app.Ctx.Get<ISessionPersistence>(Persistence.Plugin.ServiceName));
            using var renderer = new GpuRenderer(chat);
            renderer.Run();
            var sessions = app.Ctx.Get<SessionStore>(SessionStore.ServiceName)!;
            sessions.Flush(agent.Session).GetAwaiter().GetResult();
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine($"GPU unavailable: {error.Message}");
            if (Console.IsInputRedirected)
                return 1;
            return RunInteractiveAsync(app, agent).GetAwaiter().GetResult();
        }
    }

    private static void SetConsoleInteractive(bool interactive)
    {
        try
        {
            Console.CursorVisible = !interactive;
            Console.TreatControlCAsInput = interactive;
        }
        catch (IOException)
        {
        }
    }

    private static (int Width, int Height) GetConsoleSize()
    {
        try
        {
            return (Math.Max(1, Console.WindowWidth), Math.Max(1, Console.WindowHeight));
        }
        catch (IOException)
        {
            return (80, 25);
        }
        catch (ArgumentOutOfRangeException)
        {
            return (80, 25);
        }
    }
}