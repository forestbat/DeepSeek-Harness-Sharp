using System.Runtime.InteropServices;
using Dsh.Boot;
using Dsh.Core;
using Dsh.Llm;

namespace Dsh.Tui;

public static class TuiRunner
{
    /** 渲染合并窗口: leading(空闲后首事件立即渲) + trailing(窗口内到点渲最新态), 见阶段 1.4 拍板。 */
    private const int FrameIntervalMs = 10;

    public static async Task<int> Run(
        HarnessApp app,
        string cwd,
        bool gpu = false,
        bool shell = false,
        string? gpuScreenshot = null)
    {
        if (gpu)
            return RunGpuSync(app, cwd, shell, gpuScreenshot);

        // 接管(raw/备用屏幕/鼠标+清陈旧输入)必须先于耗时初始化: 堵住启动窗口期吃进残留鼠标跟踪字节的洞
        using var rawMode = TerminalRawMode.TryEnable(enableMouse: true);
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

    private static async Task<int> RunNonInteractiveAsync(HarnessApp app, AgentLoopAgent agent)
    {
        using var chat = new ChatWindow(app.Ctx, agent, app.Home, app.Ctx.Get<ISessionPersistence>(ISessionPersistence.ServiceName));
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
        using var chat = new ChatWindow(app.Ctx, agent, app.Home, app.Ctx.Get<ISessionPersistence>(ISessionPersistence.ServiceName));
        if (startShell)
            chat.AddShellPane();
        if (inputSource is not null)
            chat.WakeHook = inputSource.Wake;
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
                    forceFull = false;
                    seenVersion = chat.RenderVersion;
                    nextFrameAt = Environment.TickCount64 + FrameIntervalMs;
                }

                if (chat.ExitRequested)
                    break;

                if (inputSource is null)
                {
                    chat.HandleKey(Console.ReadKey(true));
                    continue;
                }

                var inputEvent = inputSource.Read(Timeout.Infinite);
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

    private static int RunGpuSync(HarnessApp app, string cwd, bool startShell, string? gpuScreenshot = null)
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
            if (OperatingSystem.IsLinux())
                GpuCatalog.ApplyPrimeSelection(GpuCatalog.LoadSelectedAdapter(app.Home));
            var atlas = CreateAtlasForTerminal();
            var prewarm = Task.Run(() => atlas.Prewarm());
            using var chat = new ChatWindow(app.Ctx, agent, app.Home);
            using var renderer = new GpuRenderer(chat, atlas, gpuScreenshot);
            if (startShell)
                chat.AddShellPane();
            renderer.Run();
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
            return 0;
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
        using var rawMode = TerminalRawMode.TryEnable(enableMouse: true);
        return RunInteractiveAsync(app, agent).GetAwaiter().GetResult();
    }

    /**
     * GPU 渲染的字号对齐宿主终端格尺寸: 以本机默认图集的格尺寸为基准, 宽/高两个方向各算一个比例, 取较紧者。
     * 终端不支持该查询或输出被重定向时用共享默认图集。用户想改字号就调终端字号, 重启 dsh 生效。
     */
    private static GlyphAtlas CreateAtlasForTerminal()
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