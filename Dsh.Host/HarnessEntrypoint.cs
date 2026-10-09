using Dsh.Boot;
using Dsh.Plugins;
using Dsh.Ptc;

namespace Dsh.Host;

/** 入口组合根: 解析 CLI 参数并组合插件, 然后分发到 tui/gui/headless 分支。两个宿主 exe 的 Program 只做转发。 */
public static class HarnessEntrypoint
{
    public static async Task<int> RunAsync(string[] args)
    {
        if (args.Contains(PtcScriptHost.HostArgument))
            return await PtcScriptHost.RunAsync(args);

        string? home = null;
        string? resumeSessionId = null;
        var dumpConfig = false;
        var gpu = false;
        var shell = false;
        string? gpuScreenshot = null;
        string? gpuCapturePlan = null;
        string? gpuCard = null;
        var positional = new List<string>();
        for (var index = 0; index < args.Length; index++)
        {
            switch (args[index])
            {
                case "--home" when index + 1 < args.Length:
                    home = args[++index];
                    break;
                case "--session" when index + 1 < args.Length:
                    resumeSessionId = args[++index];
                    break;
                case "--dump-config":
                    dumpConfig = true;
                    break;
                case "--gpu":
                    gpu = true;
                    break;
                case "--shell":
                    shell = true;
                    break;
                case "--gpu-screenshot" when index + 1 < args.Length:
                    gpuScreenshot = args[++index];
                    gpu = true;
                    break;
                case "--gpu-capture-plan" when index + 1 < args.Length:
                    gpuCapturePlan = args[++index];
                    gpu = true;
                    break;
                case "--gpu-card" when index + 1 < args.Length:
                    gpuCard = args[++index];
                    break;
                case "--help" or "-h":
                    PrintUsage();
                    return 0;
                default:
                    // 未知的 --选项 必须报错: 否则会被当成任务提示词喂给 headless(例如把 `dsh tui list` 误写成
                    // `dsh --tui list`), 表现为"什么都没打印然后卡住"。
                    if (args[index].StartsWith("--", StringComparison.Ordinal))
                    {
                        await Console.Error.WriteLineAsync($"dsh: unknown option \"{args[index]}\" (try --help)");
                        return 2;
                    }
                    positional.Add(args[index]);
                    break;
            }
        }

        var harnessHome = home is { Length: > 0 } explicitHome
            ? HarnessHome.Resolve(explicitHome)
            : HarnessStorage.ResolveDefaultHome();
        if (dumpConfig)
        {
            var settings = HarnessSettings.Load(harnessHome);
            var host = new PluginHost();
            host.RegisterCompiledIn();
            var discovery = host.Scan(Path.Combine(AppContext.BaseDirectory, "plugins"), nativeBridge: null);
            var lines = new List<string>
            {
                $"dsh-home: {harnessHome.Root}",
                $"provider: {settings.ResolveDefaultModel()?.Provider ?? "(not configured)"}",
                $"model: {settings.ResolveDefaultModel()?.Model ?? "(not configured)"}",
            };
            lines.AddRange(host.Catalog.Descriptors
                .OrderBy(descriptor => descriptor.Package, StringComparer.Ordinal)
                .Select(descriptor => $"plugin: {descriptor.Package} ({descriptor.Form}, "
                    + $"enabled={(settings.Plugins.TryGetValue(descriptor.Package, out var setting) ? setting.Enabled : true)})"));
            lines.AddRange(discovery.Skipped.Select(skip => $"skipped: {Path.GetFileName(skip.File)}: {skip.Reason}"));
            await Console.Out.WriteLineAsync(string.Join('\n', lines));
            return 0;
        }

        var command = positional.FirstOrDefault();
        switch (command)
        {
            case "tui":
                {
                    var subcommand = positional.Skip(1).FirstOrDefault();
                    if (subcommand == "list")
                        return await BootCli.RunTuiListAsync();
                    if (subcommand == "attach")
                    {
                        if (positional.Count < 3)
                        {
                            await Console.Error.WriteLineAsync("dsh: tui attach requires a session id");
                            return 1;
                        }

                        // tmux 式语义: id 先当 daemon 的 PTY 会话接管; 不是的话按 harness 会话恢复进窗口。
                        var target = positional[2];
                        if (await BootCli.IsDaemonPtyAsync(target))
                            return await BootCli.RunTuiAttachAsync(target);
                        await Console.Out.WriteLineAsync($"dsh: {target} 不是 daemon 里的 PTY 会话, 按 harness 会话恢复"
                            + (gpu ? "到独立 GPU 窗口" : "进本终端 TUI")
                            + "(要独立窗口加 --gpu)");
                        return await RunEntrypointAsync(harnessHome, "tui", target, gpu, shell, gpuScreenshot, gpuCard, gpuCapturePlan);
                    }
                    if (subcommand == "daemon")
                        return await BootCli.RunTuiDaemonAsync();
                    return await RunEntrypointAsync(harnessHome, "tui", resumeSessionId, gpu, shell, gpuScreenshot, gpuCard, gpuCapturePlan);
                }
            case "gui":
                // 组合插件之前先摘掉自己的控制台
                ConsoleWindow.DetachIfOwned();
                return await RunEntrypointAsync(harnessHome, "gui", resumeSessionId);
            case "headless":
                return await BootCli.RunHeadlessAsync(harnessHome, string.Join(' ', positional.Skip(1)));
            case "register-terminal":
                return await TerminalEntryRegistration.RegisterAsync(Console.Out);
            case null:
                return await RunEntrypointAsync(harnessHome, "tui", null, gpu, shell, gpuScreenshot, gpuCard, gpuCapturePlan);
            default:
                return await BootCli.RunHeadlessAsync(harnessHome, string.Join(' ', positional));
        }
    }

    /** 同步主入口: 需要时把 GUI 放到进程主线程(见 RunGuiOnMainThread), 其余入口沿用原异步路径。 */
    public static int Run(string[] args)
    {
        // macOS 的 Avalonia.Native 要求 UI 必须在进程主线程初始化(Dispatcher.ReplaceImplementation 会校验
        // impl.CurrentThreadIsLoopThread), 而 GUI 的正常启动横跨多个 await、主线程又被宿主阻塞, 无法满足;
        // 因此 gui 在 macOS 上改走顶层同步路径, 由主线程直接驱动 Avalonia。
        if (OperatingSystem.IsMacOS() && IsGuiInvocation(args))
            return RunGuiOnMainThread(args);
        return RunAsync(args).GetAwaiter().GetResult();
    }

    /** 取第一个位置参数作为命令; 跳过带值选项, 避免把 `--home gui` 之类误判成 gui 命令。 */
    private static bool IsGuiInvocation(string[] args)
    {
        string[] valueOptions = ["--home", "--session", "--gpu-screenshot", "--gpu-capture-plan", "--gpu-card"];
        for (var index = 0; index < args.Length; index++)
        {
            var arg = args[index];
            if (valueOptions.Contains(arg))
            {
                index++;
                continue;
            }
            if (arg.StartsWith("--", StringComparison.Ordinal))
                continue;
            return arg == "gui";
        }
        return false;
    }

    /** macOS 专用: 在主线程同步组合 harness 并运行 GUI, 满足 Avalonia.Native 的主线程约束。 */
    private static int RunGuiOnMainThread(string[] args)
    {
        string? home = null;
        string? resumeSessionId = null;
        for (var index = 0; index < args.Length; index++)
        {
            switch (args[index])
            {
                case "--home" when index + 1 < args.Length:
                    home = args[++index];
                    break;
                case "--session" when index + 1 < args.Length:
                    resumeSessionId = args[++index];
                    break;
            }
        }
        var harnessHome = home is { Length: > 0 } explicitHome
            ? HarnessHome.Resolve(explicitHome)
            : HarnessStorage.ResolveDefaultHome();
        var cwd = Directory.GetCurrentDirectory();
        ConsoleWindow.DetachIfOwned();
        using var app = ConfigBoot.Compose(new HarnessOptions(harnessHome, cwd, IsTui: false))
            .GetAwaiter().GetResult();
        return Dsh.Gui.GuiRunner.RunOnMainThread(app, cwd, resumeSessionId);
    }

    private static void PrintUsage()
    {
        Console.WriteLine("""
            Usage: dsh [options] [task...]
                   dsh tui [list | attach <pty-id>]
                   dsh tui --session <id> [--gpu]   (恢复 harness 会话; --gpu 开独立窗口)
                   dsh gui [--session <id>]
                   dsh headless "task"
                   dsh register-terminal    (Linux: 注册为桌面环境的默认终端)

            Options:
              --home <path>      harness home (default: $DSH_HOME or ~/.dsh; settings 的 storage.root 仍会覆盖)
              --session <id>     open an existing session (gui) or resume one (tui)
              --gpu              open the standalone terminal window with the GPU renderer
              --shell            start with a real shell pane (Dsh.Pty) in the focused slot
              --gpu-screenshot <path>  capture the GPU frame buffer to PNG/TIFF and exit
              --gpu-capture-plan <file>  drive scripted keys and capture multiple GPU frames (hidden window)
              --gpu-card <N|path>      pick the DRM card for bare-TTY GBM/KMS (e.g. 1 or /dev/dri/card1)
              --dump-config      print the resolved harness configuration and exit
              -h, --help         show this help

            tui attach 的 <id> 两种都支持(tmux 习惯):
              · daemon 里的 PTY 会话(由 /detach 产生, 见 dsh tui list) —— 直接接管它的字节流;
              · harness 会话 —— 恢复进窗口(本终端 TUI; 加 --gpu 开独立窗口)。
            注: 另一个 TUI 进程内的 shell 窗格无法跨进程 attach。
            """);
    }

    private static async Task<int> RunEntrypointAsync(
        HarnessHome home,
        string entrypoint,
        string? resumeSessionId = null,
        bool gpu = false,
        bool shell = false,
        string? gpuScreenshot = null,
        string? gpuCard = null,
        string? gpuCapturePlan = null)
    {
        var options = new HarnessOptions(home, Directory.GetCurrentDirectory(), IsTui: entrypoint == "tui");
        using var app = await ConfigBoot.Compose(options);
        return await app.RunEntrypointAsync(entrypoint, new PluginEntrypointOptions(
            app.Home,
            Directory.GetCurrentDirectory(),
            resumeSessionId,
            gpu,
            shell,
            gpuScreenshot,
            gpuCapturePlan,
            gpuCard));
    }
}

