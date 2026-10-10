using System.Diagnostics;
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
        var hostStdio = false;
        var hostServe = false;
        string? hostToken = null;
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
                            await Console.Error.WriteLineAsync("dsharp: tui attach requires a session id");
                            return 1;
                        }

                        // tmux 式语义: id 先当 daemon 的 PTY 会话接管; 不是的话按 harness 会话恢复进窗口。
                        var target = positional[2];
                        if (await BootCli.IsDaemonPtyAsync(target))
                            return await BootCli.RunTuiAttachAsync(target);
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
                return await RunEntrypointAsync(harnessHome, "gui", resumeSessionId);
            case "host":
                {
                    var subcommand = positional.Skip(1).FirstOrDefault();
                    if (subcommand is "serve" || hostServe)
                        return await RunHostServeAsync(harnessHome, hostStdio, hostToken);
                    await Console.Error.WriteLineAsync("dsharp: host requires a subcommand (serve)");
                    return 1;
                }
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

    private static async Task<int> RunHostServeAsync(HarnessHome home, bool useStdio, string? token)
    {

        if (useStdio)
        {
            // stdio-over-SSH: 桥接到常驻 daemon; 没有就 detached 起一个。断开只断这条桥, 会话留在远端(脱钩/重连)。
            if (!await Dsh.RemoteHost.RemoteHostListener.IsReachableAsync(home.Root, CancellationToken.None))
                StartHostDaemon(home, token);
            using var duplex = Dsh.Transport.TransportConnection.FromStandardIo();
            await Dsh.RemoteHost.RemoteHostListener.BridgeAsync(duplex, home.Root, CancellationToken.None);
            return 0;
        }

        using var app = await ConfigBoot.Compose(new HarnessOptions(home, Directory.GetCurrentDirectory(), IsTui: false));
        using var backend = new HarnessRemoteHostBackend(app);
        var server = new Dsh.RemoteHost.RemoteHostServer(new Dsh.RemoteHost.RemoteHostServerOptions(token), backend);
        Directory.CreateDirectory(Dsh.RemoteHost.RemoteHostEndpoint.RunDirectory(home.Root));
        await Console.Out.WriteLineAsync($"dsharp host serving on {Dsh.RemoteHost.RemoteHostEndpoint.SocketPath(home.Root)}");
        using var cancellation = new CancellationTokenSource();
        Console.CancelKeyPress += (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            cancellation.Cancel();
        };
        await Dsh.RemoteHost.RemoteHostListener.ServeLoopbackAsync(server, home.Root, cancellation.Token);
        return 0;
    }

    /** 以脱离会话的方式起 `dsharp host serve`(ssh 断开不把 daemon 带走)。 */
    private static void StartHostDaemon(HarnessHome home, string? token)
    {
        var executable = Environment.ProcessPath
            ?? throw new InvalidOperationException("failed to resolve the current executable to start the host daemon");
        var hostArguments = new List<string>();
        if (string.Equals(Path.GetFileNameWithoutExtension(executable), "dotnet", StringComparison.OrdinalIgnoreCase))
            hostArguments.Add(Environment.GetCommandLineArgs()[0]);
        hostArguments.Add("--home");
        hostArguments.Add(home.Root);
        if (token is { Length: > 0 })
        {
            hostArguments.Add("--token");
            hostArguments.Add(token);
        }

        hostArguments.Add("host");
        hostArguments.Add("serve");

        if (OperatingSystem.IsWindows())
        {
            Process.Start(new ProcessStartInfo(executable, hostArguments)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = Directory.GetCurrentDirectory(),
            });
            return;
        }

        var quoted = string.Join(' ', hostArguments.Select(argument => $"'{argument.Replace("'", "'\\''", StringComparison.Ordinal)}'"));
        Process.Start(new ProcessStartInfo("/bin/sh", ["-c", $"nohup '{executable}' {quoted} >/dev/null 2>&1 &"])
        {
            UseShellExecute = false,
            WorkingDirectory = Directory.GetCurrentDirectory(),
        });
    }

    private static void PrintUsage()
    {
        Console.WriteLine("""
            Usage: dsharp [options] [task...]
                   dsharp tui [list | attach <pty-id> | kill-pty <pty-id>]
                   dsharp tui --session <id> [--gpu]   (恢复 harness 会话; --gpu 开独立窗口)
                   dsharp gui [--session <id>]
                   dsharp headless "task"
                   dsharp host --serve [--stdio] [--token <t>]   (远端工作区宿主: 默认 loopback socket, --stdio 走 ssh)
                   dsharp register-terminal    (Linux: 注册为桌面环境的默认终端)

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

            tui kill-pty <pty-id>: 终止 daemon 里指定的那个 PTY 会话(见 dsharp tui list)。

            tui attach 的 <id> 两种都支持(tmux 习惯):
              · daemon 里的 PTY 会话(由 /detach 产生, 见 dsharp tui list) —— 直接接管它的字节流;
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

