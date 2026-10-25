using Dsh.Boot;
using Dsh.Plugins;
using Dsh.ScriptHost;
using Dsh.Tui;

namespace Dsh.Host;

/** 入口组合根: 解析 CLI 参数并组合插件, 然后分发到 tui/gui/headless 分支。两个宿主 exe 的 Program 只做转发。 */
public static class HarnessEntrypoint
{
    public static async Task<int> RunAsync(string[] args)
    {
        if (args.Contains(PtcScriptHost.HostArgument))
            return await PtcScriptHost.RunAsync(args);
        // The web process alone owns ASP.NET. Forward before parsing so this assembly knows no web options or WebBridge types.
        if (args.FirstOrDefault() == "web")
            return await WebHostLauncher.RunAsync(args[1..]);
        if (args.Length >= 2 && args[0] == "host" && args[1] == "web")
            return await WebHostLauncher.RunAsync(args[2..]);
        // machine/session/doctor have their own options (--host, --machine, ...): they are parsed as a whole, not by the loop below.
        if (FleetCli.Handles(args) && !args.Contains("--help") && !args.Contains("-h"))
            return await FleetCli.RunAsync(HarnessStorage.ResolveDefaultHome(), args, (home, sessionId) => RunEntrypointAsync(home, "tui", sessionId));

        string? resumeSessionId = null;
        var dumpConfig = false;
        var gpu = false;
        var shell = false;
        string? gpuScreenshot = null;
        string? gpuCapturePlan = null;
        string? gpuCard = null;
        var hostStdio = false;
        var hostServe = false;
        string? hostToken = null;
        var positional = new List<string>();
        for (var index = 0; index < args.Length; index++)
        {
            switch (args[index])
            {
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
                case "--stdio":
                    hostStdio = true;
                    break;
                case "--serve":
                    hostServe = true;
                    break;
                case "--token" when index + 1 < args.Length:
                    hostToken = args[++index];
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
                // 子命令自己的选项(config schema / storage 等): 原样透传给子命令解析, 不当未知选项报错。
                case "--write-template" or "--verify" or "--json" or "--markdown" or "--md":
                    positional.Add(args[index]);
                    break;
                case "--path" when index + 1 < args.Length:
                    positional.Add(args[index]);
                    positional.Add(args[++index]);
                    break;
                case "--help" or "-h":
                    PrintUsage();
                    return 0;
                default:
                    // 未知的 --选项 必须报错: 否则会被当成任务提示词喂给 headless(例如把 `dsharp tui list` 误写成
                    // `dsharp --tui list`), 表现为"什么都没打印然后卡住"。
                    if (args[index].StartsWith("--", StringComparison.Ordinal))
                    {
                        await Console.Error.WriteLineAsync($"dsharp: unknown option \"{args[index]}\" (try --help)");
                        return 2;
                    }
                    positional.Add(args[index]);
                    break;
            }
        }

        // §18/D14: 统一持久化根只由 settings.storage.root > DSH_HOME 环境变量 > ~/.dsh 决定, 不再有 --home。
        var harnessHome = HarnessStorage.ResolveDefaultHome();
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
        // 裸 `dsharp attach <pty-id>` 等价 `dsharp tui attach <pty-id>`(tmux 习惯)。
        // 仅当第二个参数确实是 daemon 里的 PTY 会话时生效; 否则仍按任务提示词走 headless, 不吞掉 "attach ..." 开头的指令。
        if (command == "attach" && positional.Count >= 2 && await BootCli.IsDaemonPtyAsync(positional[1]))
            return await AttachDaemonPtyAsync(positional[1], gpu, harnessHome);
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
                            await Console.Error.WriteLineAsync("dsharp: tui attach requires a session id");
                            return 1;
                        }

                        // tmux 式语义: id 先当 daemon 的 PTY 会话接管; 不是的话按 harness 会话恢复进窗口。
                        var target = positional[2];
                        if (await BootCli.IsDaemonPtyAsync(target))
                            return await AttachDaemonPtyAsync(target, gpu, harnessHome);
                        await Console.Out.WriteLineAsync($"dsharp: {target} 不是 daemon 里的 PTY 会话, 按 harness 会话恢复"
                            + (gpu ? "到独立 GPU 窗口" : "进本终端 TUI")
                            + "(要独立窗口加 --gpu)");
                        return await RunEntrypointAsync(harnessHome, "tui", target, gpu, shell, gpuScreenshot, gpuCard, gpuCapturePlan);
                    }
                    if (subcommand == "kill-pty")
                    {
                        if (positional.Count < 3)
                        {
                            await Console.Error.WriteLineAsync("dsharp: tui kill-pty requires a pty id (see `dsharp tui list`)");
                            return 1;
                        }

                        return await BootCli.RunTuiKillPtyAsync(positional[2]);
                    }
                    if (subcommand == "daemon")
                        return await BootCli.RunTuiDaemonAsync();
                    return await RunEntrypointAsync(harnessHome, "tui", resumeSessionId, gpu, shell, gpuScreenshot, gpuCard, gpuCapturePlan);
                }
            case "gui":
                // 组合插件之前先摘掉自己的控制台
                ConsoleWindow.DetachIfOwned();
                // macOS 的 Avalonia.Native(Cocoa) 要求 UI 在进程主线程初始化; GUI 的正常路径横跨多个 await,
                // 主线程会被宿主阻塞, 因此在首个 await 之前同步走主线程路径(此处仍在进程主线程上)。
                if (OperatingSystem.IsMacOS())
                    return RunGuiOnMainThread(harnessHome, resumeSessionId);
                return await RunEntrypointAsync(harnessHome, "gui", resumeSessionId);
            case "host":
                {
                    var subcommand = positional.Skip(1).FirstOrDefault();
                    if (subcommand is "serve" || hostServe)
                        return await RunHostServeAsync(harnessHome, hostStdio, hostToken);
                    await Console.Error.WriteLineAsync("dsharp: host requires a subcommand (serve | web)");
                    return 1;
                }
            case "headless":
                return await BootCli.RunHeadlessAsync(harnessHome, string.Join(' ', positional.Skip(1)));
            case "storage":
                return await RunStorageCommandAsync(harnessHome, positional.Skip(1).ToList());
            case "config":
                return await RunConfigCommandAsync(positional.Skip(1).ToList());
            case "register-terminal":
                return await TerminalEntryRegistration.RegisterAsync(Console.Out);
            case null:
                return await RunEntrypointAsync(harnessHome, "tui", resumeSessionId, gpu, shell, gpuScreenshot, gpuCard, gpuCapturePlan);
            default:
                return await BootCli.RunHeadlessAsync(harnessHome, string.Join(' ', positional));
        }
    }

    private static async Task<int> RunHostServeAsync(HarnessHome home, bool useStdio, string? token)
    {

        if (useStdio)
        {
            // stdio-over-SSH: 桥接到常驻 daemon; 没有就 detached 起一个。断开只断这条桥, 会话留在远端(脱钩/重连)。
            await HostAutostart.EnsureRunningAsync(home, token);
            using var duplex = Dsh.Transport.TransportConnection.FromStandardIo();
            await HostBridge.RunAsync(duplex, home.Root, CancellationToken.None);
            return 0;
        }

        using var instance = HostAutostart.TryAcquireInstance(home);
        if (instance is null)
        {
            using var connection = await Dsh.RemoteHost.RemoteHostConnector.ConnectAsync(home.Root, CancellationToken.None);
            return 0;
        }

        using var app = await ConfigBoot.Compose(new HarnessOptions(home, Directory.GetCurrentDirectory(), IsTui: false));
        await using var backend = await HarnessRemoteHostBackend.CreateAsync(app);
        var factory = app.Ctx.Get<Dsh.RemoteHost.RemoteHostServerFactory>(Dsh.RemoteHost.RemoteHostServerFactory.ServiceName, false)
            ?? throw new InvalidOperationException("remote-host-server 插件未加载，无法启动 host");
        var server = factory.Create(new Dsh.RemoteHost.RemoteHostServerOptions(token), backend);
        await Console.Out.WriteLineAsync($"dsharp host serving on {Dsh.RemoteHost.RemoteHostEndpoint.SocketPath(home.Root)}");
        using var cancellation = new CancellationTokenSource();
        Console.CancelKeyPress += (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            cancellation.Cancel();
        };
        // SIGTERM 同样走正常收尾(登记表落盘、删 socket), 而不是被运行时直接终止。
        using var termination = System.Runtime.InteropServices.PosixSignalRegistration.Create(System.Runtime.InteropServices.PosixSignal.SIGTERM, context =>
        {
            context.Cancel = true;
            cancellation.Cancel();
        });
        await Dsh.RemoteHost.RemoteHostListener.ServeLoopbackAsync(server, home.Root, cancellation.Token);
        return 0;
    }



    /**
     * `dsharp storage paths`: 打印 §18 解析后的根与各分区, 便于核对 settings.storage 是否生效。
     * `dsharp storage migrate <old> <new>`: 手动搬运(与启动时的自动迁移共用同一实现)。
     */
    /** `dsharp config schema`: 打印/校验/重写配置面(顶层 settings + 各插件参数, 全部来自编译期生成的 schema)。 */
    private static async Task<int> RunConfigCommandAsync(List<string> arguments)
    {
        var subcommand = arguments.FirstOrDefault();
        if (subcommand is null or "schema")
        {
            var path = ValueOf(arguments, "--path") ?? SettingsTemplate.DefaultPath;
            if (arguments.Contains("--write-template"))
            {
                var changed = SettingsTemplate.Write(path);
                await Console.Out.WriteLineAsync(changed ? $"settings.yaml 模板已更新: {path}" : $"settings.yaml 模板已是最新: {path}");
                return 0;
            }
            if (arguments.Contains("--verify"))
            {
                var problem = SettingsTemplate.Verify(path);
                if (problem is null)
                {
                    await Console.Out.WriteLineAsync($"settings.yaml 模板与配置面一致: {path}");
                    return 0;
                }
                await Console.Error.WriteLineAsync(problem);
                return 1;
            }
            var format = arguments.Contains("--json") ? "json" : arguments.Contains("--markdown") || arguments.Contains("--md") ? "markdown" : "yaml";
            await Console.Out.WriteAsync(SettingsSchemaCatalog.Render(format));
            return 0;
        }

        await Console.Error.WriteLineAsync("dsharp: config requires a subcommand (schema [--json|--markdown|--verify|--write-template])");
        return 1;
    }

    private static string? ValueOf(List<string> arguments, string option)
    {
        var index = arguments.IndexOf(option);
        return index >= 0 && index + 1 < arguments.Count ? arguments[index + 1] : null;
    }

    private static async Task<int> RunStorageCommandAsync(HarnessHome home, List<string> arguments)
    {
        var subcommand = arguments.FirstOrDefault();
        if (subcommand is null or "paths")
        {
            var settings = HarnessSettings.Load(home);
            var paths = HarnessStorage.Paths(home, settings);
            await Console.Out.WriteLineAsync($"root: {paths.Root}");
            foreach (var name in Dsh.Runtime.DshPaths.Names)
                await Console.Out.WriteLineAsync($"{name}: {paths.Path(name)}");
            return 0;
        }

        if (subcommand != "migrate")
        {
            await Console.Error.WriteLineAsync("dsharp: storage requires a subcommand (paths | migrate <old> <new>)");
            return 1;
        }

        if (arguments.Count < 3)
        {
            await Console.Error.WriteLineAsync("dsharp: storage migrate requires <old> and <new> roots");
            return 1;
        }

        var source = HarnessHome.Resolve(arguments[1]);
        var target = HarnessHome.Resolve(arguments[2]);
        if (!Directory.Exists(source.Root))
        {
            await Console.Error.WriteLineAsync($"dsharp: storage migrate old root {source.Root} does not exist");
            return 1;
        }

        // 搬运清单按"旧根自己的 settings"解析(改动后旧根里实际存在的分区名), 目标根的分区配置与此无关。
        var sourceSettings = HarnessSettings.Load(source);
        var results = HarnessStorage.Migrate(source, target, sourceSettings.Storage);
        foreach (var (directory, outcome) in results)
            await Console.Out.WriteLineAsync($"  {directory}: {outcome}");
        await Console.Out.WriteLineAsync(results.Count == 0
            ? "storage migrate: nothing to do (same root or nothing to move)"
            : $"storage migrate: {results.Count} partition(s) handled");
        return 0;
    }

    /**
     * 接管 daemon 里的 PTY 会话: `--gpu` 时优先用独立窗口接回(与 `--gpu` 启动同一条渲染路径),
     * 窗口宿主不可用(无显示服务器/无 DRM/终端不支持图像协议)再回退到本终端接管。
     */
    private static async Task<int> AttachDaemonPtyAsync(string ptyId, bool gpu, HarnessHome home)
    {
        if (gpu && TuiProxy.TryRunWindow(home, ptyId))
            return 0;
        return await BootCli.RunTuiAttachAsync(ptyId);
    }

    private static void PrintUsage()
    {
        Console.WriteLine("""
            Usage: dsharp [options] [task...]
                   dsharp tui [list | attach <pty-id> [--gpu] | kill-pty <pty-id>]
                   dsharp attach <pty-id> [--gpu]   (等价 dsharp tui attach <pty-id>)
                   dsharp tui --session <id> [--gpu]   (恢复 harness 会话; --gpu 开独立窗口)
                   dsharp gui [--session <id>]
                   dsharp web [--port N] [--print-token]   (启动独立安装的 dsharp-web，不在本进程加载 Web 框架)
                   dsharp headless "task"
                   dsharp host serve [--stdio] [--token <t>]   (后台工作区宿主: --stdio 通过 ssh 桥接, 不安装系统服务)
                   dsharp host web [--port N] [--print-token]   (同上；只监听 127.0.0.1，跨设备用 ssh -L 转发)
                   dsharp register-terminal    (Linux: 注册为桌面环境的默认终端)
                   dsharp storage paths        (打印 storage.root 与各分区解析结果)
                   dsharp storage migrate <old> <new>   (手动搬运旧根数据)
                   dsharp config schema [--json | --markdown]   (列出所有可配置项: 顶层 settings + 各插件参数)
                   dsharp config schema --verify [--path <模板>]        (校验 settings.yaml 模板是否与配置面同步)
                   dsharp config schema --write-template [--path <模板>] (用配置面重写模板生成块)
            """);
        Console.WriteLine(FleetCli.Usage);
        Console.WriteLine("""

            Options:
              --session <id>     open an existing session (gui) or resume one (tui)
              --gpu              open the standalone terminal window with the GPU renderer
              --shell            start with a real shell pane (Dsh.Pty) in the focused slot
              --gpu-screenshot <path>  capture the GPU frame buffer to PNG/TIFF and exit
              --gpu-capture-plan <file>  drive scripted keys and capture multiple GPU frames (hidden window)
              --gpu-card <N|path>      pick the DRM card for bare-TTY GBM/KMS (e.g. 1 or /dev/dri/card1)
              --dump-config      print the resolved harness configuration and exit
              -h, --help         show this help

            tui kill-pty <pty-id>: 终止 daemon 里指定的那个 PTY 会话(见 dsharp tui list)。

            tui attach 的 <id> 两种都支持(tmux 习惯):
              · daemon 里的 PTY 会话(由 /detach 产生, 见 dsharp tui list) —— 直接接管它的字节流; 加 --gpu 改用独立窗口接回该会话;
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
            gpuCard,
            StartLocalHostAsync));
    }

    /** macOS 专用: 由进程主线程同步组合 harness 并运行 GUI(Avalonia.Native 要求主线程初始化 UI)。 */
    private static int RunGuiOnMainThread(HarnessHome home, string? resumeSessionId)
    {
        var cwd = Directory.GetCurrentDirectory();
        using var app = ConfigBoot.Compose(new HarnessOptions(home, cwd, IsTui: false)).GetAwaiter().GetResult();
        return Dsh.Gui.GuiRunner.RunOnMainThread(app, cwd, resumeSessionId, StartLocalHostAsync);
    }

    /** GUI 连接本机 host 时按需拉起: 以脱离会话的方式起 `host serve`, 与 CLI 的 `session *` 一致。 */
    private static Task StartLocalHostAsync(string harnessHome, CancellationToken cancellationToken)
        => HostAutostart.EnsureRunningAsync(HarnessHome.Resolve(harnessHome), token: null, cancellationToken);
}
