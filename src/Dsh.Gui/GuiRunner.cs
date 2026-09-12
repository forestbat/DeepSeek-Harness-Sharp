using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Themes.Fluent;
using Dsh.Boot;
using Dsh.Core;
using Dsh.Llm;

namespace Dsh.Gui;

public static class GuiRunner
{
    public static async Task<int> Run(
        HarnessApp app,
        string cwd)
    {
        var agents = app.Ctx.Get<AgentRegistry>(AgentRegistry.ServiceName)!;
        var handle = await agents.Create(new CreateAgentOptions(
            SessionId.Create($"session-{Guid.NewGuid()}"),
            cwd,
            new AgentOptions(app.Provider, app.Model, app.ReasoningEffort is null ? null : ReasoningEffortId.Create(app.ReasoningEffort))));
        var agent = (AgentLoopAgent)handle.Agent;
        await agent.WhenIdle();

        var exitCode = await RunAvaloniaAsync(app, agent);
        var sessions = app.Ctx.Get<SessionStore>(SessionStore.ServiceName)!;
        await sessions.Flush(agent.Session);
        return exitCode;
    }

    private static Task<int> RunAvaloniaAsync(HarnessApp app, AgentLoopAgent agent)
    {
        var done = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                GuiApplication.StartupWindowFactory = () =>
                {
                    var window = new MainWindow(app.Ctx, agent, app.Home);
                    window.Closed += (_, _) =>
                    {
                        if (!done.Task.IsCompleted)
                            done.TrySetResult(0);
                    };
                    return window;
                };
                var exitCode = AppBuilder.Configure<GuiApplication>()
                    .UsePlatformDetect()
                    .StartWithClassicDesktopLifetime([], ShutdownMode.OnMainWindowClose);
                if (!done.Task.IsCompleted)
                    done.TrySetResult(exitCode);
            }
            catch (Exception error)
            {
                done.TrySetException(error);
            }
        });
        if (OperatingSystem.IsWindows())
            thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return done.Task;
    }

    public sealed class GuiApplication : Application
    {
        public static Func<MainWindow>? StartupWindowFactory { get; set; }

        public GuiApplication()
        {
            Styles.Add(new FluentTheme());
        }

        public override void OnFrameworkInitializationCompleted()
        {
            if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop && StartupWindowFactory is not null)
                desktop.MainWindow = StartupWindowFactory();
            base.OnFrameworkInitializationCompleted();
        }
    }
}