using Dsh.Boot;
using Dsh.Tui.Services;

namespace Dsh.Tui;

/**
 * 终端形态的 proxy: 会话常驻 daemon(一个 pty 上的 TUI 进程), 本进程只搬运字节、同步尺寸。
 * 这样 detach/attach 只是 proxy 的退出/进入, 会话进程内的草稿、鼠标模式、菜单与备用屏状态都不会丢;
 * 常驻会话自己用 DSH_PTY_CHILD 环境变量标记, 避免它再次包一层 proxy。
 */
public static class TuiProxy
{
    public static async Task<int> RunAsync(HarnessApp app, bool startShell, bool gpu = false, string? gpuCard = null, string? resumeSessionId = null)
    {
        await PtyDaemonClient.EnsureRunningAsync();
        var (columns, rows) = ConsoleTunnel.ReadSize() ?? (80, 24);
        var executable = Environment.ProcessPath
            ?? throw new InvalidOperationException("failed to resolve the current executable to start the resident TUI session");
        var arguments = new List<string>();
        if (string.Equals(Path.GetFileNameWithoutExtension(executable), "dotnet", StringComparison.OrdinalIgnoreCase))
            arguments.Add(Environment.GetCommandLineArgs()[0]);
        arguments.AddRange(ChildArguments(startShell, resumeSessionId));

        var session = await PtyDaemonClient.StartAsync(new PtyDaemonStartParams
        {
            FileName = executable,
            Arguments = arguments,
            WorkingDirectory = Environment.CurrentDirectory,
            Rows = rows,
            Columns = columns,
            Home = app.Home.Root,
            Environment = new Dictionary<string, string?> { [PtySessionProtocol.ChildVariable] = "1" },
            // 常驻 TUI 要鼠标(拖动分隔线/滚轮): 终端形态的代理据此打开宿主终端上报。
            WantsMouse = true,
        });

        // 独立窗口形态: 窗口只渲染会话画面, 关窗口即 detach(会话继续跑)。
        // 宿主创建失败(无显示服务器/无 DRM/终端不支持图像协议)时打印原因并回退到下面的终端形态, 不抛未捕获异常。
        var windowShown = gpu && TryRunWindow(app.Home, session.Id, gpuCard);
        if (!windowShown)
        {
            // 终端形态: 接管本终端, 输出即会话画面, 输入原样进会话; 常驻会话发 detach 标记或退出时本进程就结束。
            try
            {
                await PtyDaemonClient.AttachConsoleAsync(session.Id, Console.OpenStandardOutput(), CancellationToken.None);
            }
            catch (InvalidOperationException)
            {
                var exitCode = await ProxyExitCodeAsync(session.Id, reportFailure: true, requireExited: true);
                if (exitCode is null)
                    throw;
                return exitCode.Value;
            }
        }
        return await ProxyExitCodeAsync(session.Id, reportFailure: windowShown) ?? 0;
    }

    private static async Task<int?> ProxyExitCodeAsync(string sessionId, bool reportFailure, bool requireExited = false)
    {
        foreach (var candidate in await PtyDaemonClient.ListAsync())
        {
            if (candidate.Id != sessionId || requireExited && candidate.Status != nameof(PtySessionStatus.Exited))
                continue;
            var exitCode = candidate.ExitCode ?? 0;
            if (reportFailure && exitCode != 0)
                await Console.Error.WriteLineAsync(await PtyDaemonClient.SnapshotTextAsync(sessionId));
            return exitCode;
        }
        return null;
    }

    /**
     * 把某个常驻会话(daemon PTY id)渲染进独立 GPU 窗口: 关窗口即 detach, 会话继续在 daemon 里跑。
     * `--gpu` 启动与 `dsharp tui attach --gpu` 共用本入口(launch 传新会话 id, attach 传已有 pty id)。
     * 宿主不可用(无显示服务器/无 DRM/终端不支持图像协议)时打印原因并返回 false, 由调用方回退到终端接管。
     * gpuCard: `--gpu-card` 显式指定的 DRM 卡; 未指定时按设置里的选卡(与进程内 GPU 形态同一套 PRIME/卡节点规则)。
     */
    public static bool TryRunWindow(HarnessHome home, string sessionId, string? gpuCard = null)
    {
        var atlas = TuiRunner.CreateAtlasForTerminal();
        var windowHost = CreateWindowHostOrNull(home, atlas, gpuCard);
        if (windowHost is null)
            return false;
        using (windowHost)
            new GpuSessionProxy(sessionId, atlas, windowHost).Run();
        return true;
    }

    /** 后端按 tui.gpu.backend 选择(与 RunGpuSync 同一条路径): vk 时窗口形态也走 Vulkan。 */
    private static IGlSurfaceHostRunner? CreateWindowHostOrNull(HarnessHome home, GlyphAtlas atlas, string? gpuCard)
    {
        var backend = TuiGpuBackendParser.Parse(new TuiSettings(home).GpuBackend);
        // 显式 CPU 后端: 不开 GPU 窗口, 回退终端接管(与 launch 路径 `backend == Cpu ⇒ gpu = false` 一致)。
        if (backend == TuiGpuBackend.Cpu)
            return null;
        var preferredCard = GpuCatalog.ApplyProcessSelection(home);
        try
        {
            return GpuHostFactory.CreateWindowHostForBackend(
                backend,
                atlas,
                gpuCard,
                preferredCard,
                hidden: false,
                vsync: GpuCatalog.LoadVsync(home),
                headlessCapture: false);
        }
        catch (Exception error)
        {
            Console.Error.WriteLine($"GPU unavailable: {error.Message}");
            return null;
        }
    }


    /** 只转发会影响"会话本体"的参数: --session 决定续哪个会话; home 作为结构化字段交给 daemon, 不塞进命令行。 */
    private static List<string> ChildArguments(bool startShell, string? resumeSessionId)
    {
        var arguments = new List<string> { "tui" };
        if (resumeSessionId is not null)
        {
            arguments.Add("--session");
            arguments.Add(resumeSessionId);
        }

        if (startShell)
            arguments.Add("--shell");
        return arguments;
    }
}
