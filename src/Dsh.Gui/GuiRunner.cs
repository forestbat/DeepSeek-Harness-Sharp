using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Dsh.Boot;
using Dsh.Core;
using Dsh.Gui.Services;
using Dsh.Gui.Views;
using Dsh.Llm;

namespace Dsh.Gui;

public static class GuiRunner
{
    public static async Task<int> Run(
        HarnessApp app,
        string cwd,
        string? resumeSessionId = null)
    {
        using var instance = SingleInstance.Acquire(app.Home.Root);
        if (instance is null)
            return 0;

        var settings = new GuiSettings(app.Home).Load();
        // 显式设置的工作区盖过进程启动目录: 双击桌面快捷方式启动时 cwd 会落在安装目录/System32
        var workspace = settings.DefaultWorkspace ?? cwd;
        var agents = app.Ctx.Get<AgentRegistry>(AgentRegistry.ServiceName)!;
        var agent = await OpenAgentAsync(app, agents, workspace, resumeSessionId);

        var (exitCode, lastSession) = await RunAvaloniaAsync(app, agent, settings, instance);
        var sessions = app.Ctx.Get<SessionStore>(SessionStore.ServiceName)!;
        await sessions.Flush(lastSession);
        return exitCode;
    }

    /**
     * macOS 专用: Avalonia.Native(Cocoa)要求 UI 在进程主线程初始化(Dispatcher 会校验 impl.CurrentThreadIsLoopThread),
     * 所以这里全程同步地在调用方线程(必须是主线程)上执行, 不再另起线程 —— RunAvaloniaAsync 里 new Thread 的写法
     * 在 macOS 上必然抛 "IDispatcherImpl belongs to a different thread"。
     * 其余平台仍走 Run(另起线程 + Windows STA)。
     */
    public static int RunOnMainThread(
        HarnessApp app,
        string cwd,
        string? resumeSessionId = null)
    {
        using var instance = SingleInstance.Acquire(app.Home.Root);
        if (instance is null)
            return 0;

        var settings = new GuiSettings(app.Home).Load();
        // 显式设置的工作区盖过进程启动目录: 双击桌面快捷方式启动时 cwd 会落在安装目录/System32
        var workspace = settings.DefaultWorkspace ?? cwd;
        var agents = app.Ctx.Get<AgentRegistry>(AgentRegistry.ServiceName)!;
        var agent = OpenAgentAsync(app, agents, workspace, resumeSessionId).GetAwaiter().GetResult();

        MainWindow? window = null;
        instance.ActivationRequested += () =>
        {
            if (window is not null)
                Dispatcher.UIThread.Post(() => window.ShowFromTray());
        };
        App.StartupAppearance = application => ThemeService.Apply(application, settings);
        App.StartupWindowFactory = () =>
        {
            window = new MainWindow(app, agent);
            return window;
        };
        var exitCode = BuildApp(settings).StartWithClassicDesktopLifetime([], ShutdownMode.OnExplicitShutdown);
        var sessions = app.Ctx.Get<SessionStore>(SessionStore.ServiceName)!;
        sessions.Flush(window?.ViewModel?.CurrentAgent.Session ?? agent.Session).GetAwaiter().GetResult();
        return exitCode;
    }

    /** --session 指定时直接打开历史会话; 恢复失败则退回新建, 不让 GUI 起不来。 */
    private static async Task<AgentLoopAgent> OpenAgentAsync(
        HarnessApp app,
        AgentRegistry agents,
        string cwd,
        string? resumeSessionId)
    {
        var options = new AgentOptions(app.Provider, app.Model, app.ReasoningEffort is null ? null : ReasoningEffortId.Create(app.ReasoningEffort));
        if (resumeSessionId is not null)
        {
            try
            {
                var resumed = (AgentLoopAgent)(await agents.Resume(new ResumeAgentOptions(SessionId.Create(resumeSessionId), options))).Agent;
                await resumed.WhenIdle();
                return resumed;
            }
            catch (Exception error)
            {
                await Console.Error.WriteLineAsync($"dsh: session \"{resumeSessionId}\" cannot be resumed: {error.Message}");
            }
        }
        var created = (AgentLoopAgent)(await agents.Create(new CreateAgentOptions(
            SessionId.Create($"session-{Guid.NewGuid()}"),
            cwd,
            options))).Agent;
        await created.WhenIdle();
        return created;
    }

    private static Task<(int ExitCode, Session Session)> RunAvaloniaAsync(
        HarnessApp app,
        AgentLoopAgent agent,
        GuiSettingsSnapshot settings,
        SingleInstance instance)
    {
        var done = new TaskCompletionSource<(int, Session)>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                MainWindow? window = null;
                instance.ActivationRequested += () =>
                {
                    if (window is not null)
                        Dispatcher.UIThread.Post(() => window.ShowFromTray());
                };
                App.StartupAppearance = application => ThemeService.Apply(application, settings);
                App.StartupWindowFactory = () =>
                {
                    window = new MainWindow(app, agent);
                    return window;
                };
                var exitCode = BuildApp(settings).StartWithClassicDesktopLifetime([], ShutdownMode.OnExplicitShutdown);
                done.TrySetResult((exitCode, window?.ViewModel?.CurrentAgent.Session ?? agent.Session));
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

    private static AppBuilder BuildApp(GuiSettingsSnapshot settings)
    {
        var builder = AppBuilder.Configure<App>()
            .UsePlatformDetect();
        GpuPreference.Apply(builder, settings);
        return builder;
    }
}
