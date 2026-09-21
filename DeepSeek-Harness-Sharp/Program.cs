using Dsh.Boot;
using Dsh.Plugins;

namespace DeepSeek_Harness_Sharp;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        string? home = null;
        string? resumeSessionId = null;
        var dumpConfig = false;
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
                    break;
                case "--help" or "-h":
                    PrintUsage();
                    return 0;
                default:
                    positional.Add(args[index]);
                    break;
            }
        }

        var harnessHome = HarnessHome.Resolve(home);
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
                        return await BootCli.RunTuiAttachAsync(positional[2]);
                    }
                    if (subcommand == "daemon")
                        return await BootCli.RunTuiDaemonAsync();
                    return await RunEntrypointAsync(harnessHome, "tui", "@deepseek-ai/dsh-tui", resumeSessionId);
                }
            case "gui":
                // 组合插件之前先摘掉自己的控制台
                ConsoleWindow.DetachIfOwned();
                return await RunEntrypointAsync(harnessHome, "gui", "@deepseek-ai/dsh-gui", resumeSessionId);
            case "headless":
                return await BootCli.RunHeadlessAsync(harnessHome, string.Join(' ', positional.Skip(1)));
            case null:
                return await RunEntrypointAsync(harnessHome, "tui", "@deepseek-ai/dsh-tui");
            default:
                return await BootCli.RunHeadlessAsync(harnessHome, string.Join(' ', positional));
        }
    }

    private static void PrintUsage()
    {
        Console.WriteLine("""
            Usage: dsh [options] [task...]
                   dsh tui [list | attach <id>]
                   dsh gui [--session <id>]
                   dsh headless "task"

            Options:
              --home <path>      harness home (default: $DSH_HOME or ~/.dsh)
              --session <id>     open an existing session (gui) or resume one (tui)
              --gpu              run the TUI with the GPU renderer
              --dump-config      print the resolved harness configuration and exit
              -h, --help         show this help
            """);
    }

    private static async Task<int> RunEntrypointAsync(HarnessHome home, string entrypoint, string entrypointPlugin, string? resumeSessionId = null)
    {
        var options = new HarnessOptions(home, Directory.GetCurrentDirectory(), IsTui: entrypoint == "tui", EntrypointPlugin: entrypointPlugin);
        using var app = await ConfigBoot.Compose(options);
        return await app.RunEntrypointAsync(entrypoint, new PluginEntrypointOptions(home, Directory.GetCurrentDirectory(), resumeSessionId));
    }
}
