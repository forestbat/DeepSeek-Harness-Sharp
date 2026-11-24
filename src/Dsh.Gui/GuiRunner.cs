using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using Dsh.Boot;
using Dsh.Gui.Services;
using Dsh.Gui.Views;

namespace Dsh.Gui;

public static class GuiRunner
{
    /** 启动时等本机 Host 完成握手的上限: 超过它必须给出可见的失败窗口和重试入口。 */
    private static readonly TimeSpan HandshakeTimeout = TimeSpan.FromSeconds(90);

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
        Func<string, CancellationToken, Task>? startLocalHost, CancellationToken cancellationToken)
    {
        var fleet = FleetWorkspace.ForContext(app.Ctx, app.Home, startLocalHost);
        try
        {
            return await HostChatSession.OpenAsync(
                fleet, cwd, resumeSessionId, cancellationToken, ProjectionCache.ForHome(app.Home.Root));
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
        // 前台线程会阻止进程退出: 消息循环一旦退不干净(见 MainWindow.ShutdownApplication 的兜底),
        // 进程就永远留在任务管理器里, 只能手动结束。
        thread.IsBackground = true;
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
        StartupFailureWindow? failureWindow = null;
        // GUI 没有控制台: 未处理异常不记日志, 用户看到的就只是"窗口自己没了"(实测如此)。
        // 文件 provider 逐行 Flush, 进程随后退出也留得下这条栈。
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            var logger = app.Ctx.LoggerFor("gui");
            if (args.ExceptionObject is Exception error)
                logger.Error(error);
            else
                logger.Error("未处理异常, 进程即将退出: %s", Convert.ToString(args.ExceptionObject));
        };
        // 本进程就是 GUI 本体: 允许退出兜底强杀自己(见 MainWindow.ShutdownApplication)。
        MainWindow.OwnsProcess = true;
        instance.ActivationRequested += () => Dispatcher.UIThread.Post(() =>
        {
            // 第二次启动或托盘图标: 主窗口建好了就把它带回前台; 还在连接、或停在失败窗口时, 把手上那个窗口带到前面。
            if (window is not null)
                window.ShowFromTray();
            else
                failureWindow?.Activate();
        });
        App.StartupAppearance = application => ThemeService.Apply(application, settings);
        // 启动路径只登记后台流程, 不同步等握手: 连不上时必须落到失败窗口, 而不是"进程活着但没有窗口"。
        App.StartupHook = () => _ = RunStartupAsync();
        return BuildApp(settings).StartWithClassicDesktopLifetime([], ShutdownMode.OnExplicitShutdown);

        // 连上就换主窗口; 连不上(超时或抛异常)就摆出失败窗口让用户重试或退出。
        // 所有窗口动作都显式投递到 UI 线程: 握手在线程池上等, 续体落在哪个线程不该影响窗口。
        async Task RunStartupAsync()
        {
            try
            {
                while (true)
                {
                    if (await TryOpenMainWindowAsync() is not { } startupFailure)
                        return;
                    var retry = await Dispatcher.UIThread.InvokeAsync(async () =>
                    {
                        failureWindow = new StartupFailureWindow(startupFailure);
                        try
                        {
                            return await failureWindow.ShowAndWaitAsync();
                        }
                        finally
                        {
                            failureWindow = null;
                        }
                    });
                    if (retry)
                        continue;
                    Dispatcher.UIThread.Post(Shutdown);
                    return;
                }
            }
            catch (Exception error)
            {
                // 连失败窗口都摆不出来时至少让进程退出: 留一个没有窗口的进程等于什么都没告诉用户。
                app.Ctx.LoggerFor("gui").Error($"GUI 启动失败: {error.Message}", error);
                Dispatcher.UIThread.Post(Shutdown);
            }
        }

        // null 表示主窗口已经显示; 否则返回失败窗口要摆的标题与原因。
        async Task<StartupFailure?> TryOpenMainWindowAsync()
        {
            using var handshake = new CancellationTokenSource(HandshakeTimeout);
            try
            {
                var chat = await Task.Run(() => OpenChatAsync(app, cwd, resumeSessionId, startLocalHost, handshake.Token));
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    window = new MainWindow(app, chat);
                    if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
                        desktop.MainWindow = window;
                    window.Show();
                });
                return null;
            }
            catch (OperationCanceledException) when (handshake.IsCancellationRequested)
            {
                return new StartupFailure(
                    "连不上本机 Host，主窗口没有打开。",
                    $"本机 Host 在 {HandshakeTimeout.TotalSeconds:0} 秒内没有完成握手。",
                    Connection: true);
            }
            catch (HostConnectionUnavailableException error)
            {
                return new StartupFailure(
                    "连不上本机 Host，主窗口没有打开。",
                    Runtime.OutboundProxy.Redact(error.Message),
                    Connection: true);
            }
            catch (Exception error)
            {
                // Host 已经连上了: 这一步的失败(打开/恢复会话读不出来)与连接无关, 别让用户去杀宿主进程。
                return new StartupFailure("主窗口没有打开。", Runtime.OutboundProxy.Redact(error.Message), Connection: false);
            }
        }

        static void Shutdown()
        {
            if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
                desktop.Shutdown();
        }
    }

    private static AppBuilder BuildApp(GuiSettingsSnapshot settings)
    {
        var builder = AppBuilder.Configure<App>()
            .UsePlatformDetect();
        GpuPreference.Apply(builder, settings);
        return builder;
    }
}