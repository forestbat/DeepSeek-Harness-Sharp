using System.Diagnostics;
using Dsh.Boot;
using Dsh.Boot.Profiles;

namespace DeepSeek_Harness_Sharp;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        Dsh.Launcher.PluginRoot.EnsureRooted();

        if (args.Length > 0 && args[0] == "plugin")
            return await RunPlugin(HarnessHome.Resolve(FindHomeArg(args)), args[1..]);

        string? profile = null;
        string? patch = null;
        string? home = null;
        string? config = null;
        var dumpConfig = false;
        var dumpDefaultConfig = false;
        var useTmux = false;
        var positional = new List<string>();
        for (var index = 0; index < args.Length; index++)
        {
            switch (args[index])
            {
                case "--profile" when index + 1 < args.Length:
                    profile = args[++index];
                    break;
                case "--patch" when index + 1 < args.Length:
                    patch = args[++index];
                    break;
                case "--home" when index + 1 < args.Length:
                    home = args[++index];
                    break;
                case "--config" when index + 1 < args.Length:
                    config = args[++index];
                    break;
                case "--dump-config":
                    dumpConfig = true;
                    break;
                case "--dump-default-config":
                    dumpDefaultConfig = true;
                    break;
                case "--tmux":
                    useTmux = true;
                    break;
                case "--help" or "-h":
                    PrintUsage();
                    return 0;
                case "web":
                    profile = "web";
                    break;
                case "tui":
                    profile = "tui";
                    break;
                case "acp":
                    profile = "acp";
                    break;
                case "lsp":
                    profile = "lsp";
                    break;
                default:
                    positional.Add(args[index]);
                    break;
            }
        }
        if (profile == "tui")
        {
            if (positional.FirstOrDefault() == "list")
                return await BootCli.RunTuiListAsync();
            if (positional.FirstOrDefault() == "attach")
            {
                if (positional.Count < 2)
                {
                    Console.Error.WriteLine("dsh: tui attach requires a session id");
                    return 1;
                }

                return await BootCli.RunTuiAttachAsync(positional[1]);
            }

            if (positional.FirstOrDefault() == "daemon")
                return await BootCli.RunTuiDaemonAsync();
        }

        if (useTmux && Environment.GetEnvironmentVariable("TMUX") is null)
            return StartInTmux(args);

        var harnessHome = HarnessHome.Resolve(home);
        if (dumpDefaultConfig)
        {
            foreach (var name in ProfileTemplates.Names)
                Console.WriteLine(name);
            return 0;
        }
        IReadOnlyList<Dictionary<string, object?>>? patches;
        try
        {
            patches = patch is null ? null : ConfigBoot.LoadPatches(patch);
        }
        catch (Cordis.CordisException error)
        {
            Console.Error.WriteLine($"dsh: {error.Message}");
            return 1;
        }
        if (dumpConfig)
        {
            Console.WriteLine($"dsh-home: {harnessHome.Root}");
            Console.WriteLine($"provider: {HarnessComposer.DefaultProvider}");
            Console.WriteLine($"model: {HarnessComposer.DefaultModel}");
            return 0;
        }

        string? bootConfig = config;
        IReadOnlyList<Dictionary<string, object?>>? bootPatches = patches;
        if (config is null && profile is not null)
        {
            (bootConfig, bootPatches) = ConfigBoot.PrepareProfile(harnessHome, profile, patches);
        }

        switch (profile)
        {
            case null:
            case "tui":
            case "web":
            case "acp":
            case "lsp":
            {
                if (profile == "tui" && IsGpuRequested())
                {
                    using var gpuApp = ComposeEntrypointApp(harnessHome, bootConfig, bootPatches).GetAwaiter().GetResult();
                    return PluginEntrypointRegistry.RunAsync("tui", gpuApp, new PluginEntrypointOptions(
                        harnessHome, Directory.GetCurrentDirectory(), bootConfig, bootPatches)).GetAwaiter().GetResult();
                }

                using var app = await ComposeEntrypointApp(harnessHome, bootConfig, bootPatches);
                return await PluginEntrypointRegistry.RunAsync(profile ?? "web", app, new PluginEntrypointOptions(
                    harnessHome, Directory.GetCurrentDirectory(), bootConfig, bootPatches));
            }
            case "headless":
                return await BootCli.RunHeadlessAsync(harnessHome, string.Join(' ', positional), bootConfig, bootPatches);
            case "sdk" or "sdk-minimal":
                return await BootCli.RunSdkAsync(harnessHome, bootConfig, bootPatches);
            default:
                Console.Error.WriteLine($"dsh: unknown profile \"{profile}\"");
                return 1;
        }
    }

    private static bool IsGpuRequested()
    {
        if (string.Equals(Environment.GetEnvironmentVariable("DSH_TUI_GPU"), "1", StringComparison.Ordinal))
            return true;
        var args = Environment.GetCommandLineArgs();
        return args.Any(argument => string.Equals(argument, "--gpu", StringComparison.Ordinal));
    }

    private static void PrintUsage()
    {
        Console.WriteLine("""
            Usage: dsh [options] [task...]

            Options:
              --profile <name>   headless | tui | web | sdk | acp | lsp | sdk-minimal (default: web)
              --config <path>    boot from a cordis.yml composition instead of the built-in defaults
              --home <path>      harness home (default: $DSH_HOME or ~/.dsh)
              --dump-config      print the composed configuration and exit
              --dump-default-config
                                 print the built-in profile template names and exit
              -h, --help         show this help

            Commands:
              tui                start the terminal UI
              headless "task"    answer one task and exit
            """);
    }

    private static string? FindHomeArg(string[] args)
    {
        for (var index = 0; index < args.Length - 1; index++)
        {
            if (args[index] == "--home")
                return args[index + 1];
        }
        return null;
    }

    private static async Task<int> RunPlugin(HarnessHome home, string[] args)
    {
        string? profile = null;
        var pnpmArgs = new List<string>();
        for (var index = 0; index < args.Length; index++)
        {
            if (args[index] == "--profile" && index + 1 < args.Length)
            {
                profile = args[++index];
            }
            else
            {
                pnpmArgs.Add(args[index]);
            }
        }
        profile ??= "tui";
        if ((pnpmArgs.FirstOrDefault() is "add" or "remove")
            && !pnpmArgs.Contains("-w")
            && !pnpmArgs.Contains("--workspace-root"))
        {
            pnpmArgs.Insert(1, "-w");
        }
        ProfileStore.InitProfile(home, profile);
        var workingDirectory = ProfileStore.ResolveProfileDir(home, profile);
        var startInfo = new ProcessStartInfo("pnpm")
        {
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
        };
        foreach (var argument in pnpmArgs)
            startInfo.ArgumentList.Add(argument);
        using var process = Process.Start(startInfo);
        if (process is null)
            return 1;
        await process.WaitForExitAsync();
        return process.ExitCode;
    }

    private static async Task<HarnessApp> ComposeEntrypointApp(
        HarnessHome home,
        string? config,
        IReadOnlyList<Dictionary<string, object?>>? patches)
    {
        var options = new HarnessOptions(home, Directory.GetCurrentDirectory());
        return config is null
            ? await HarnessComposer.Compose(options)
            : await ConfigBoot.Compose(config, options, patches: patches);
    }

    private static int StartInTmux(string[] args)
    {
        var relaunchArgs = args.Where(argument => argument != "--tmux").ToArray();
        var dotnet = Environment.ProcessPath ?? "dotnet";
        var assembly = Environment.GetCommandLineArgs()[0];
        var command = $"{dotnet} \"{assembly}\" {string.Join(' ', relaunchArgs)}";
        Process.Start("tmux", ["new-session", "-d", "-s", "dsh", command]);
        Process.Start("tmux", ["attach", "-t", "dsh"]).WaitForExit();
        return 0;
    }
}
