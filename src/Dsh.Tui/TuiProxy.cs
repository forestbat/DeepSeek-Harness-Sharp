using Dsh.Boot;
using Dsh.Pty;

namespace Dsh.Tui;

/**
 * 终端形态的 proxy: 会话常驻 daemon(一个 pty 上的 TUI 进程), 本进程只搬运字节、同步尺寸。
 * 这样 detach/attach 只是 proxy 的退出/进入, 会话进程内的草稿、鼠标模式、菜单与备用屏状态都不会丢;
 * 常驻会话自己用 DSH_PTY_CHILD 环境变量标记, 避免它再次包一层 proxy。
 */
internal static class TuiProxy
{
    public static async Task<int> RunAsync(HarnessApp app, bool startShell, bool gpu = false)
    {
        await PtyDaemonClient.EnsureRunningAsync();
        var (columns, rows) = ConsoleSize();
        var executable = Environment.ProcessPath
            ?? throw new InvalidOperationException("failed to resolve the current executable to start the resident TUI session");
        var arguments = new List<string>();
        if (string.Equals(Path.GetFileNameWithoutExtension(executable), "dotnet", StringComparison.OrdinalIgnoreCase))
            arguments.Add(Environment.GetCommandLineArgs()[0]);
        arguments.AddRange(ChildArguments(app, startShell));

        var session = await PtyDaemonClient.StartAsync(new PtyDaemonStartParams
        {
            FileName = executable,
            Arguments = arguments,
            WorkingDirectory = Environment.CurrentDirectory,
            Rows = rows,
            Columns = columns,
            Environment = new Dictionary<string, string?> { [PtySessionProtocol.ChildVariable] = "1" },
        });

        if (gpu)
        {
            // 独立窗口形态: 窗口只渲染会话画面, 关窗口即 detach(会话继续跑)。
            var atlas = TuiRunner.CreateAtlasForTerminal();
            using var windowHost = GpuHostFactory.CreateWindowHost(atlas);
            new GpuSessionProxy(session.Id, atlas, windowHost).Run();
            return 0;
        }

        // 终端形态: 接管本终端, 输出即会话画面, 输入原样进会话; 常驻会话发 detach 标记或退出时本进程就结束。
        await PtyDaemonClient.AttachAsync(
            session.Id,
            Console.OpenStandardInput(),
            Console.OpenStandardOutput(),
            CancellationToken.None);
        return 0;
    }

    /** 只转发会影响"会话本体"的参数: --session 决定续哪个会话, --home 决定配置根; 形态参数一律不带过去。 */
    private static List<string> ChildArguments(HarnessApp app, bool startShell)
    {
        var arguments = new List<string> { "tui" };
        var raw = Environment.GetCommandLineArgs();
        var start = Array.FindIndex(raw, argument => string.Equals(argument, "tui", StringComparison.Ordinal));
        var hasHome = false;
        for (var position = start + 1; start >= 0 && position < raw.Length; position++)
        {
            if (string.Equals(raw[position], "--session", StringComparison.Ordinal) && position + 1 < raw.Length)
            {
                arguments.Add("--session");
                arguments.Add(raw[++position]);
                continue;
            }

            if (string.Equals(raw[position], "--home", StringComparison.Ordinal) && position + 1 < raw.Length)
            {
                arguments.Add("--home");
                arguments.Add(raw[++position]);
                hasHome = true;
            }
        }

        if (!hasHome)
        {
            arguments.Add("--home");
            arguments.Add(app.Home.Root);
        }

        if (startShell)
            arguments.Add("--shell");
        return arguments;
    }

    private static (int Columns, int Rows) ConsoleSize()
    {
        try
        {
            return (Math.Max(20, Console.WindowWidth), Math.Max(4, Console.WindowHeight));
        }
        catch (IOException)
        {
            return (80, 24);
        }
        catch (ArgumentOutOfRangeException)
        {
            return (80, 24);
        }
    }
}
