using System.Runtime.InteropServices;
using Dsh.Boot;
using Dsh.Core;
using Dsh.Llm;
using Dsh.Tui.Services;

namespace Dsh.Tui;

public static class TuiRunner
{
    /** 渲染合并窗口: leading(空闲后首事件立即渲) + trailing(窗口内到点渲最新态), 见阶段 1.4 拍板。 */
    private const int FrameIntervalMs = 10;

    /** detach 交接时携带"未发送的输入"的变量名: 旧 TUI 写, 新 TUI 读一次后立即清除。 */
    internal const string DetachDraftVariable = "DSH_DETACH_DRAFT";

    /** 空闲时复查会话尺寸的间隔: 终端(尤其 ConPTY)被 resize 不会打断阻塞读, 只能定期醒来对比。 */
    private const int ResizePollMilliseconds = 1000;

    public static async Task<int> Run(
        HarnessApp app,
        string cwd,
        bool gpu = false,
        bool shell = false,
        string? gpuScreenshot = null,
        string? gpuCard = null,
        string? gpuCapturePlan = null)
    {
        // 渲染前先声明控制台按 UTF-8 解释: daemon 脱离终端后创建的伪控制台默认取系统 OEM 代码页, 会把画面中文读成乱码。
        ConsoleCodePage.EnsureUtf8();
        HookCrashLog(app);
        if (gpu && gpuScreenshot is null && gpuCapturePlan is null && ShouldRunAsProxy())
            return await TuiProxy.RunAsync(app, shell, gpu: true);
        if (gpu)
            return RunGpuSync(app, cwd, shell, gpuScreenshot, gpuCard, gpuCapturePlan);

        // 终端交互形态默认走"会话常驻 daemon + 本进程当 proxy"; 常驻会话本体(DSH_PTY_CHILD)与非交互(重定向)例外。
        if (ShouldRunAsProxy())
            return await TuiProxy.RunAsync(app, shell);

        // 接管(raw/备用屏幕/鼠标+清陈旧输入)必须先于耗时初始化: 堵住启动窗口期吃进残留鼠标跟踪字节的洞。
        // 走到这里已不是 proxy: 本进程的终端要么是 daemon 的 ConPTY、要么被重定向, 都不是"用户终端"。
        // 因此只做原始模式与备用屏幕接管, 不发出鼠标上报序列(否则会经 daemon 泄到用户终端, 与 proxy 重复)——
        // 鼠标由持有用户终端的 proxy 负责开启并转发。Windows 仍保留控制台鼠标输入位, 供 InjectMouse 注入记录。
        using var rawMode = TerminalRawMode.TryEnable(enableMouse: true, emitMouseReports: false);
        var signalGuards = RegisterSignalRestore(rawMode);
        try
        {
            var agents = app.Ctx.Get<AgentRegistry>(AgentRegistry.ServiceName)!;
            var handle = await agents.Create(new CreateAgentOptions(
                SessionId.Create($"session-{Guid.NewGuid()}"),
                cwd,
                new AgentOptions(app.Provider, app.Model, app.ReasoningEffort is null ? null : ReasoningEffortId.Create(app.ReasoningEffort))));
            var agent = (AgentLoopAgent)handle.Agent;
            await agent.WhenIdle();

            if (Console.IsInputRedirected)
                return await RunNonInteractiveAsync(app, agent);

            return await RunInteractiveAsync(app, agent, shell);
        }
        finally
        {
            if (signalGuards is not null)
                foreach (var guard in signalGuards)
                    guard.Dispose();
        }
    }

    private static bool _crashLogHooked;

    /** 未处理异常把栈落盘(开发期落 artifacts020/, 安装态回落 home/logs), 事后可定位。 */
    private static void HookCrashLog(HarnessApp app)
    {
        if (_crashLogHooked)
            return;
        _crashLogHooked = true;
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            try
            {
                var directory = Path.Combine(Environment.CurrentDirectory, "artifacts020");
                if (!Directory.Exists(directory))
                    directory = app.Home.LogsPath;
                Directory.CreateDirectory(directory);
                var path = Path.Combine(directory, $"tui-crash-{DateTime.Now:yyyyMMdd-HHmmss}.log");
                File.WriteAllText(path, Convert.ToString(args.ExceptionObject) ?? "unhandled exception (no detail)");
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        };
    }

    /** Unix 信号兜底: SIGTERM/SIGHUP/SIGINT 时先恢复终端再退出, 不给宿主留 raw+备用屏幕残骸。 */
    private static List<PosixSignalRegistration>? RegisterSignalRestore(TerminalRawMode? rawMode)
    {
        if (rawMode is null || OperatingSystem.IsWindows())
            return null;
        var guards = new List<PosixSignalRegistration>(3);
        foreach (var signal in new[] { PosixSignal.SIGTERM, PosixSignal.SIGHUP, PosixSignal.SIGINT })
        {
            guards.Add(PosixSignalRegistration.Create(signal, context =>
            {
                context.Cancel = true;
                rawMode.Dispose();
                Environment.Exit(128 + (int)signal);
            }));
        }
        return guards;
    }

    /** 终端交互形态默认走 proxy(会话常驻 daemon); 常驻会话本体与非交互(输入/输出被重定向)留在本进程渲染。 */
    private static bool ShouldRunAsProxy()
        => !Console.IsInputRedirected
            && !Console.IsOutputRedirected
            && !IsResidentChild();

    /** 常驻会话(daemon 的 pty 里那个 TUI 本体): 它的终端不是用户终端, 鼠标上报等终端能力都不适用。 */
    private static bool IsResidentChild()
        => string.Equals(
            Environment.GetEnvironmentVariable(PtySessionProtocol.ChildVariable),
            "1",
            StringComparison.Ordinal);

    private static async Task<int> RunNonInteractiveAsync(HarnessApp app, AgentLoopAgent agent)
    {
        using var chat = new ChatWindow(app.Ctx, agent, app.Home, app.Ctx.Get<ISessionPersistence>(ISessionPersistence.ServiceName), new TuiSettings(app.Home));
        chat.DrainUi();
        var size = GetConsoleSize();
        var layout = LayoutEngine.Calculate(size.Width, size.Height);
        var grid = new CellGrid(size.Width, size.Height);
        chat.Draw(grid, layout);
        var renderer = new AnsiRenderer();
        await Console.Out.WriteAsync(renderer.RenderToBuffer(grid, chat.CursorScreenX, chat.CursorScreenY, forceFull: true));
        await Console.Out.FlushAsync();

        var sessions = app.Ctx.Get<SessionStore>(SessionStore.ServiceName)!;
        await sessions.Flush(agent.Session);
        return 0;
    }

    private static async Task<int> RunInteractiveAsync(HarnessApp app, AgentLoopAgent agent, bool startShell = false)
    {
        SetConsoleInteractive(true);
        using var inputSource = CreateInputSource();
        var renderer = new AnsiRenderer();
        var grid = new CellGrid(80, 25);
        var forceFull = true;
        using var chat = new ChatWindow(app.Ctx, agent, app.Home, app.Ctx.Get<ISessionPersistence>(ISessionPersistence.ServiceName), new TuiSettings(app.Home));
        chat.RegisterPaneTools();
        using var paneBridge = PaneBridge.Mount(chat);
        var draft = Environment.GetEnvironmentVariable(DetachDraftVariable);
        if (!string.IsNullOrEmpty(draft))
        {
            Environment.SetEnvironmentVariable(DetachDraftVariable, null);
            chat.SeedDraft(draft);
        }
        if (startShell)
            chat.AddShellPane();
        if (inputSource is not null)
            chat.WakeHook = inputSource.Wake;

        // 终端尺寸变化(尤其 attach/resize 之后)不会打断阻塞读: 定时盯住尺寸, 一变就唤醒主循环重排,
        // 否则画面按旧几何继续绘制(表现为 attach 后输入区下方留一大片空白)。
        using var sizeWatch = new Timer(
            _ =>
            {
                var size = GetConsoleSize();
                if (size.Width != grid.Width || size.Height != grid.Height)
                    chat.WakeHook?.Invoke();
            },
            null,
            ResizePollMilliseconds,
            ResizePollMilliseconds);

        var seenVersion = -1;
        var nextFrameAt = 0L;
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
                if (forceFull || chat.RenderVersion != seenVersion)
                {
                    // trailing 窗口内继续吃输入, 到点渲最新状态
                    if (inputSource is not null && Environment.TickCount64 < nextFrameAt)
                        DrainInputUntil(chat, inputSource, nextFrameAt, layout);
                    chat.Draw(grid, layout);
                    await Console.Out.WriteAsync(renderer.RenderToBuffer(grid, chat.CursorScreenX, chat.CursorScreenY, forceFull));
                    await Console.Out.FlushAsync();
                    if (chat.DetachRequested)
                    {
                        // 常驻会话: 只通知 proxy 离开, 自己接着跑(标记是 OSC, 不会污染画面)。
                        chat.ClearDetachRequest();
                        await Console.Out.WriteAsync(PtySessionProtocol.DetachMarker);
                        await Console.Out.FlushAsync();
                    }
                    forceFull = false;
                    seenVersion = chat.RenderVersion;
                    nextFrameAt = Environment.TickCount64 + FrameIntervalMs;
                    // DrainInputUntil 可能已消费唤醒字节, 但对应 UI 动作还没执行;
                    // 若直接落到下面的阻塞 Read, 这次唤醒就丢了, 输出泵的 Invalidate 会被 _wakePending 挡死(shell 输出饿死)。
                    // 先回环顶 DrainUi, 确认无待办再阻塞。
                    continue;
                }

                if (chat.ExitRequested)
                    break;

                if (inputSource is null)
                {
                    chat.HandleKey(Console.ReadKey(true));
                    continue;
                }

                // 用带超时的读而不是无限阻塞: 会话空闲时也要定期醒来看尺寸是否被 attach/resize 改过, 否则画面按旧几何绘制,
                // 表现为"attach 后输入区上方/下方留一大片空白"。超时返回 null 时会回到循环顶重新取尺寸并按需重绘。
                var inputEvent = inputSource.Read(ResizePollMilliseconds);
                if (inputEvent is null)
                {
                    if (inputSource.EndOfStream)
                        break;
                    continue;
                }
                DispatchInput(chat, inputEvent.Value, layout);
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

    private static void DispatchInput(ChatWindow chat, TerminalInputEvent inputEvent, UiLayout layout)
    {
        if (inputEvent.IsPaste)
        {
            chat.HandlePaste(inputEvent.Paste!);
            return;
        }
        if (inputEvent.IsMouse)
        {
            var mouse = inputEvent.Mouse!.Value;
            if (mouse.IsWheel)
                chat.HandleMouseWheel(mouse.WheelDelta, mouse.X, mouse.Y, layout);
            else if (mouse.IsMove)
                chat.HandleMouseDrag(mouse.X, mouse.Y, layout);
            else if (mouse.Pressed)
                chat.HandleMouseClick(mouse.X, mouse.Y, layout);
            else
                chat.HandleMouseRelease(mouse.X, mouse.Y, layout);
            return;
        }
        chat.HandleKey(inputEvent.Key);
    }

    private static void DrainInputUntil(ChatWindow chat, ITerminalInputSource inputSource, long deadline, UiLayout layout)
    {
        while (Environment.TickCount64 < deadline)
        {
            var inputEvent = inputSource.Read((int)(deadline - Environment.TickCount64));
            if (inputEvent is null)
                return;
            DispatchInput(chat, inputEvent.Value, layout);
        }
    }

    /**
     * 输入源按平台选择: Unix 原始字节 + SGR 鼠标, Windows 控制台输入记录(ReadConsoleInputW)。
     * 句柄不可用时返回 null, 调用方回退到 Console.ReadKey(无鼠标)。
     */
    private static ITerminalInputSource? CreateInputSource()
        => OperatingSystem.IsWindows()
            ? WindowsConsoleInputReader.TryCreate()
            : new TerminalInputReader();

    private static int RunGpuSync(
        HarnessApp app,
        string cwd,
        bool startShell,
        string? gpuScreenshot = null,
        string? gpuCard = null,
        string? gpuCapturePlan = null)
    {
        var agents = app.Ctx.Get<AgentRegistry>(AgentRegistry.ServiceName)!;
        var handle = agents.Create(new CreateAgentOptions(
            SessionId.Create($"session-{Guid.NewGuid()}"),
            cwd,
            new AgentOptions(app.Provider, app.Model, app.ReasoningEffort is null ? null : ReasoningEffortId.Create(app.ReasoningEffort)))).GetAwaiter().GetResult();
        var agent = (AgentLoopAgent)handle.Agent;
        agent.WhenIdle().GetAwaiter().GetResult();

        try
        {
            // Linux 进程内选卡靠 PRIME 变量, 必须在 GLFW/Mesa 初始化之前设置; Windows 的 WGL 无进程内选卡 API, 不做处理。
            string? preferredCard = null;
            if (OperatingSystem.IsLinux())
            {
                var selected = GpuCatalog.LoadSelectedAdapter(app.Home);
                GpuCatalog.ApplyPrimeSelection(selected);
                // GBM/KMS 不看 PRIME 变量, 得把同一个选卡偏好落到具体卡节点上(不可用时宿主会回退扫描)
                preferredCard = GpuCatalog.ResolveDrmCardPath(selected);
            }
            var atlas = CreateAtlasForTerminal();
            var prewarm = Task.Run(() => atlas.Prewarm());
            using var chat = new ChatWindow(app.Ctx, agent, app.Home, settings: new TuiSettings(app.Home));
            chat.RegisterPaneTools();
            using var paneBridge = PaneBridge.Mount(chat);
            using var renderer = new GpuRenderer(chat, atlas, gpuScreenshot, gpuCard, preferredCard, gpuCapturePlan, vsync: GpuCatalog.LoadVsync(app.Home));
            if (startShell)
                chat.AddShellPane();
            // 进程信号兜底: 裸 TTY(GBM)形态没有窗口关闭事件, 收到信号也要走完正常 Dispose 恢复控制台(KD_TEXT/VT_AUTO/dropMaster)
            var exitSignal = 0;
            var signalGuards = new List<PosixSignalRegistration>(4);
            if (!OperatingSystem.IsWindows())
            {
                // 信号兜底只持有弱引用: 注册可能比 using(renderer) 活得更久, 避免在已释放对象上调用。
                var rendererRef = new WeakReference<GpuRenderer>(renderer);
                // PosixSignal 枚举值是平台中立常量而非 Linux 信号编号, 退出码要用真实编号
                foreach (var (signal, signo) in new[] { (PosixSignal.SIGTERM, 15), (PosixSignal.SIGHUP, 1), (PosixSignal.SIGINT, 2), (PosixSignal.SIGQUIT, 3) })
                {
                    signalGuards.Add(PosixSignalRegistration.Create(signal, context =>
                    {
                        context.Cancel = true;
                        exitSignal = signo;
                        if (rendererRef.TryGetTarget(out var running))
                            running.RequestClose();
                    }));
                }
            }
            try
            {
                renderer.Run();
            }
            finally
            {
                foreach (var guard in signalGuards)
                    guard.Dispose();
            }
            try
            {
                prewarm.GetAwaiter().GetResult();
            }
            catch (Exception error)
            {
                Console.Error.WriteLine($"字形预热失败：{error.Message}");
            }
            var sessions = app.Ctx.Get<SessionStore>(SessionStore.ServiceName)!;
            sessions.Flush(agent.Session).GetAwaiter().GetResult();
            return exitSignal != 0 ? 128 + exitSignal : 0;
        }
        catch (Exception error)
        {
            return ReturnGpuUnavailable(app, agent, error.Message);
        }
    }

    private static int ReturnGpuUnavailable(HarnessApp app, AgentLoopAgent agent, string reason)
    {
        Console.Error.WriteLine($"GPU unavailable: {reason}");
        if (Console.IsInputRedirected)
            return 1;
        using var rawMode = TerminalRawMode.TryEnable(enableMouse: true, emitMouseReports: false);
        return RunInteractiveAsync(app, agent).GetAwaiter().GetResult();
    }

    /**
     * GPU 渲染的字号对齐宿主终端格尺寸: 以本机默认图集的格尺寸为基准, 宽/高两个方向各算一个比例, 取较紧者。
     * 终端不支持该查询或输出被重定向时用共享默认图集。用户想改字号就调终端字号, 重启 dsh 生效。
     */
    internal static GlyphAtlas CreateAtlasForTerminal()
    {
        if (TerminalFontProbe.QueryCellPixelSize() is not { Height: > 0 } cell)
            return GlyphAtlas.Shared;
        var shared = GlyphAtlas.Shared;
        var pt = GlyphAtlas.DefaultFontSizePt * Math.Min(
            cell.Height / (double)shared.GlyphHeight,
            cell.Width / (double)shared.GlyphWidth);
        return Math.Abs(pt - GlyphAtlas.DefaultFontSizePt) < 0.5 ? GlyphAtlas.Shared : new GlyphAtlas(fontSizePt: pt);
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
        // 常驻会话的尺寸以 daemon 为准(它写尺寸文件, 与 tmux 的 server 同角色):
        // ConPTY 子进程在 ResizePseudoConsole 之后读不到新的窗口尺寸, 只信 Console.WindowWidth 会导致画面按旧几何绘制、下方留白。
        if (SessionSizeFromDaemon() is { } size)
            return size;

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

    private static (int Width, int Height)? SessionSizeFromDaemon()
    {
        var id = Environment.GetEnvironmentVariable(PtySessionProtocol.SessionVariable);
        if (string.IsNullOrEmpty(id))
            return null;
        try
        {
            var parts = File.ReadAllText(PtyDaemonPaths.SessionSizeFile(id))
                .Split(' ', StringSplitOptions.RemoveEmptyEntries);
            return parts.Length == 2
                && int.TryParse(parts[0], out var columns)
                && int.TryParse(parts[1], out var rows)
                && columns > 0
                && rows > 0
                ? (columns, rows)
                : null;
        }
        catch (IOException)
        {
            return null;
        }
    }
}