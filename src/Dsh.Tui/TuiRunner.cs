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

    private static async Task<int> RunInteractiveAsync(HarnessApp app, AgentLoopAgent agent)
    {
        SetConsoleInteractive(true);
        // Unix 上用原始字节读取以便解析 SGR 鼠标; Windows 控制台 ReadKey 无法消费 SGR, 鼠标仅 GPU 后端可用。
        var rawMouse = !OperatingSystem.IsWindows();
        using var rawMode = TerminalRawMode.TryEnable(enableMouse: rawMouse);
        using var inputReader = rawMouse ? new TerminalInputReader(Console.OpenStandardInput()) : null;
        var renderer = new AnsiRenderer();
        var grid = new CellGrid(80, 25);
        var forceFull = true;
        using var chat = new ChatWindow(app.Ctx, agent, app.Home, app.Ctx.Get<ISessionPersistence>(ISessionPersistence.ServiceName));
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
                await Console.Out.WriteAsync(renderer.RenderToBuffer(grid, chat.CursorScreenX, chat.CursorScreenY, forceFull));
                await Console.Out.FlushAsync();
                forceFull = false;

                if (chat.ExitRequested)
                    break;

                if (inputReader is null)
                {
                    chat.HandleKey(Console.ReadKey(true));
                    continue;
                }

                var inputEvent = inputReader.Read();
                if (inputEvent is null)
                    break;
                if (inputEvent.Value.IsMouse)
                {
                    var mouse = inputEvent.Value.Mouse!.Value;
                    if (mouse.IsWheel)
                        chat.HandleMouseWheel(mouse.WheelDelta, mouse.X, mouse.Y, layout);
                    else if (mouse.Pressed && mouse.Button == 0)
                        chat.HandleMouseClick(mouse.X, mouse.Y, layout);
                }
                else
                {
                    chat.HandleKey(inputEvent.Value.Key);
                }
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
            if (!GpuRenderer.TryDetectDisplay(out var unavailableReason))
                return ReturnGpuUnavailable(app, agent, unavailableReason);
            // Linux 进程内选卡靠 PRIME 变量, 必须在 GLFW/Mesa 初始化之前设置; Windows 的 WGL 无进程内选卡 API, 不做处理。
            if (OperatingSystem.IsLinux())
                GpuCatalog.ApplyPrimeSelection(GpuCatalog.LoadSelectedAdapter(app.Home));
            var atlas = CreateAtlasForTerminal();
            var prewarm = Task.Run(() => atlas.Prewarm());
            using var chat = new ChatWindow(app.Ctx, agent, app.Home);
            using var renderer = new GpuRenderer(chat, atlas);
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
        return RunInteractiveAsync(app, agent).GetAwaiter().GetResult();
    }

    /**
     * GPU 渲染的字号跟随终端: 启动时向终端查询字符格子的像素尺寸(CSI 16 t), 按格高比例换算字号(默认档 13pt = 16px 格高);
     * 终端不支持该查询或输出被重定向时用共享默认图集。用户想改字号就调终端字号, 重启 dsh 生效。
     */
    private static GlyphAtlas CreateAtlasForTerminal()
    {
        if (TerminalFontProbe.QueryCellPixelSize() is not { Height: > 0 } cell)
            return GlyphAtlas.Shared;
        var pt = GlyphAtlas.DefaultFontSizePt * cell.Height / GlyphAtlas.DefaultGlyphHeight;
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