using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Dsh.Boot;
using Dsh.Gui.Services;
using Dsh.Gui.Views;

namespace Dsh.Gui;

public static class GuiRunner
{
    /** startLocalHost launches the local Host on demand; the configured workspace provider owns its connections. */
    public static async Task<int> Run(
        HarnessApp app,
        string cwd,
        string? resumeSessionId = null,
        Func<string, CancellationToken, Task>? startLocalHost = null)
    {
        using var instance = SingleInstance.Acquire(app.Home.Root);
        if (instance is null)
            return 0;

        var settings = new GuiSettings(app.Home).Load();
        // 显式设置的工作区盖过进程启动目录: 双击桌面快捷方式启动时 cwd 会落在安装目录/System32
        var workspace = settings.DefaultWorkspace ?? cwd;
        return await RunAvaloniaAsync(app, workspace, resumeSessionId, settings, instance, startLocalHost);
    }

    /**
     * macOS 专用: Avalonia.Native(Cocoa)要求 UI 在进程主线程初始化(Dispatcher 会校验 impl.CurrentThreadIsLoopThread),
     * 子线程必然抛 "IDispatcherImpl belongs to a different thread"。这里全程同步地在调用方线程(必须是进程主线程)上跑,
     * 不再另起线程; 其余平台仍走 Run(另起线程 + Windows STA)。
     */
    public static int RunOnMainThread(
        HarnessApp app,
        string cwd,
        string? resumeSessionId = null,
        Func<string, CancellationToken, Task>? startLocalHost = null)
    {
        using var instance = SingleInstance.Acquire(app.Home.Root);
        if (instance is null)
            return 0;

        var settings = new GuiSettings(app.Home).Load();
        var workspace = settings.DefaultWorkspace ?? cwd;
        return RunAvalonia(app, workspace, resumeSessionId, settings, instance, startLocalHost);
    }

    /** A persisted pre-Fleet session is resumed by Host under the same id; GUI never opens a writer. */
    private static async Task<HostChatSession> OpenChatAsync(
        HarnessApp app, string cwd, string? resumeSessionId,
        Func<string, CancellationToken, Task>? startLocalHost)
    {
        var fleet = FleetWorkspace.ForContext(app.Ctx, app.Home, startLocalHost);
        try
        {
            return await HostChatSession.OpenAsync(fleet, cwd, resumeSessionId);
        }
        catch
        {
            await fleet.DisposeAsync();
            throw;
        }
    }

    private static Task<int> RunAvaloniaAsync(
        HarnessApp app,
        string cwd,
        string? resumeSessionId,
        GuiSettingsSnapshot settings,
        SingleInstance instance,
        Func<string, CancellationToken, Task>? startLocalHost)
    {
        var done = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                done.TrySetResult(RunAvalonia(app, cwd, resumeSessionId, settings, instance, startLocalHost));
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

    /** 在同一线程内跑 Avalonia 消息循环; 该线程必须是 UI 线程(macOS 上必须是进程主线程)。 */
    private static int RunAvalonia(
        HarnessApp app,
        string cwd,
        string? resumeSessionId,
        GuiSettingsSnapshot settings,
        SingleInstance instance,
        Func<string, CancellationToken, Task>? startLocalHost)
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
            // Platform/dispatcher must exist before FleetWorkspace starts posting UI events.
            // Connection setup runs without the UI synchronization context while this synchronous factory waits.
            var chat = Task.Run(() => OpenChatAsync(app, cwd, resumeSessionId, startLocalHost)).GetAwaiter().GetResult();
            window = new MainWindow(app, chat);
            return window;
        };
        return BuildApp(settings).StartWithClassicDesktopLifetime([], ShutdownMode.OnExplicitShutdown);
    }

    private static AppBuilder BuildApp(GuiSettingsSnapshot settings)
    {
        var builder = AppBuilder.Configure<App>()
            .UsePlatformDetect();
        GpuPreference.Apply(builder, settings);
        return builder;
    }
}
