using System.Diagnostics;
using Dsh.Boot;
using Dsh.Boot.Profiles;
using Dsh.Core;
using Dsh.Llm;
using Dsh.Pty;
using Dsh.Sdk;

namespace DeepSeek_Harness_Sharp;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
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
                default:
                    positional.Add(args[index]);
                    break;
            }
        }
        if (profile == "tui")
        {
            if (positional.FirstOrDefault() == "list")
                return await RunTuiList();
            if (positional.FirstOrDefault() == "attach")
            {
                if (positional.Count < 2)
                {
                    Console.Error.WriteLine("dsh: tui attach requires a session id");
                    return 1;
                }

                return await RunTuiAttach(positional[1]);
            }

            if (positional.FirstOrDefault() == "daemon")
                return await RunTuiDaemon();
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
            case "web":
                return await Dsh.Boot.ProfileSurfaceRegistry.RunAsync("web", new ProfileSurfaceRunOptions(
                    harnessHome, Directory.GetCurrentDirectory(), bootConfig, bootPatches));
            case "headless":
                return await RunHeadless(harnessHome, string.Join(' ', positional), bootConfig, bootPatches);
            case "sdk" or "sdk-minimal":
                return await RunSdk(harnessHome, bootConfig, bootPatches);
            case "acp":
                return await Dsh.Boot.ProfileSurfaceRegistry.RunAsync("acp", new ProfileSurfaceRunOptions(
                    harnessHome, Directory.GetCurrentDirectory(), bootConfig, bootPatches));
            case "lsp":
                return await Dsh.Boot.ProfileSurfaceRegistry.RunAsync("lsp", new ProfileSurfaceRunOptions(
                    harnessHome, Directory.GetCurrentDirectory(), bootConfig, bootPatches));
            case "tui":
                return await Dsh.Boot.ProfileSurfaceRegistry.RunAsync("tui", new ProfileSurfaceRunOptions(
                    harnessHome, Directory.GetCurrentDirectory(), bootConfig, bootPatches));
            default:
                Console.Error.WriteLine($"dsh: unknown profile \"{profile}\"");
                return 1;
        }
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

    private static async Task<int> RunTuiList()
    {
        try
        {
            var sessions = await PtyDaemonClient.ListAsync();
            if (sessions.Count == 0)
            {
                Console.WriteLine("no PTY sessions");
                return 0;
            }

            foreach (var session in sessions)
                Console.WriteLine($"{session.Id}\t{session.Command}\t{session.StartedAt:O}\t{session.Status}");

            return 0;
        }
        catch (PtyDaemonNotRunningException)
        {
            Console.Error.WriteLine("daemon not running");
            return 1;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine($"dsh: list failed: {error.Message}");
            return 1;
        }
    }

    private static async Task<int> RunTuiAttach(string id)
    {
        try
        {
            await PtyDaemonClient.AttachAsync(
                id,
                Console.OpenStandardInput(),
                Console.OpenStandardOutput());
            return 0;
        }
        catch (PtyDaemonNotRunningException)
        {
            Console.Error.WriteLine("daemon not running");
            return 1;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine($"dsh: attach failed: {error.Message}");
            return 1;
        }
    }

    private static async Task<int> RunTuiDaemon()
    {
        await using var daemon = new PtyDaemon();
        await daemon.StartAsync();
        var shutdown = new ManualResetEventSlim(false);
        ConsoleCancelEventHandler cancelHandler = (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            shutdown.Set();
        };
        Console.CancelKeyPress += cancelHandler;
        try
        {
            await Task.Run(shutdown.Wait);
        }
        finally
        {
            Console.CancelKeyPress -= cancelHandler;
            shutdown.Dispose();
        }

        return 0;
    }

    private static async Task<int> RunHeadless(HarnessHome home, string task, string? config, IReadOnlyList<Dictionary<string, object?>>? patches)    {
        if (string.IsNullOrWhiteSpace(task))
        {
            Console.Error.WriteLine("error: a task is required, for example: dsh --profile headless \"run the tests\"");
            return 1;
        }
        using var app = config is null
            ? HarnessComposer.Compose(new HarnessOptions(home, Directory.GetCurrentDirectory()))
            : await ConfigBoot.Compose(config, new HarnessOptions(home, Directory.GetCurrentDirectory()), patches: patches);
        using var autoApprove = Dsh.Interaction.ApprovalAnswerers.AutoApprove(app.Ctx);
        var agents = app.Ctx.Get<AgentRegistry>(AgentRegistry.ServiceName)!;
        var handle = await agents.Create(new CreateAgentOptions(
            SessionId.Create($"session-{Guid.NewGuid()}"),
            Directory.GetCurrentDirectory(),
            new AgentOptions(app.Provider, app.Model)));
        var agent = (AgentLoopAgent)handle.Agent;
        await agent.WhenIdle();
        var firstSeq = agent.Session.Seq;
        using var reasoning = StreamReasoning(app.Ctx, agent);
        agent.Followup(MessageFactory.CreateUserText(task));
        await agent.WhenIdle();
        var sessions = app.Ctx.Get<SessionStore>(SessionStore.ServiceName)!;
        await sessions.Flush(agent.Session);

        var (text, reason) = Summarize(agent.Session, firstSeq);
        Console.Out.WriteLine(text);
        if (reason is TurnEndReason.Error error)
        {
            Console.Error.WriteLine($"dsh: {error.Failure.Code}: {error.Failure.Message}");
            return 1;
        }
        return reason is TurnEndReason.Completed ? 0 : 1;
    }

    private static async Task<int> RunSdk(HarnessHome home, string? config, IReadOnlyList<Dictionary<string, object?>>? patches)
    {
        using var app = config is null
            ? HarnessComposer.Compose(new HarnessOptions(home, Directory.GetCurrentDirectory()))
            : await ConfigBoot.Compose(config, new HarnessOptions(home, Directory.GetCurrentDirectory()), patches: patches);
        await using var transport = new JsonRpcLineTransport(Console.In, Console.Out);
        var server = new HarnessSdkServer(app.Ctx, transport);
        transport.RequestHandler = (method, parameters) => server.HandleRequestAsync(method, parameters);
        transport.Start();
        await transport.WhenClosedAsync();
        return 0;
    }

    private static (string Text, TurnEndReason? Reason) Summarize(Session session, long firstSeq)
    {
        var started = false;
        var text = "";
        TurnEndReason? reason = null;
        for (var seq = firstSeq; seq < session.Seq; seq++)
        {
            var sessionEvent = session.EventAt(seq);
            if (sessionEvent is null)
                throw new InvalidOperationException($"headless summary cannot read seq {seq} below captured length {session.Seq}");
            switch (sessionEvent.Data)
            {
                case TurnStartPayload:
                    started = true;
                    break;
                case AssistantMessagePayload assistant when started:
                {
                    var joined = string.Concat(assistant.Message.Content.OfType<TextBlock>().Select(block => block.Text));
                    if (joined != "")
                        text = joined;
                    break;
                }
                case TurnEndPayload turnEnd:
                    reason = turnEnd.Reason;
                    break;
            }
        }
        return (text, reason);
    }

    private static IDisposable StreamReasoning(Cordis.Context ctx, AgentLoopAgent agent)
    {
        var started = false;
        var open = false;
        var endsWithNewline = true;

        void Close()
        {
            if (!open)
                return;
            if (!endsWithNewline)
                Console.Error.Write('\n');
            open = false;
            endsWithNewline = true;
        }

        var dispose = ctx.On(SessionStore.EventEvent, (_, args) =>
        {
            if (!ReferenceEquals(args[0], agent.Session))
                return new ValueTask<object?>();
            if (((SessionEvent)args[1]!).Data is TurnStartPayload)
            {
                Close();
                started = true;
                return new ValueTask<object?>();
            }
            if (!started || ((SessionEvent)args[1]!).Data is not AssistantChunkPayload chunkPayload)
                return new ValueTask<object?>();
            switch (chunkPayload.Chunk)
            {
                case StreamChunk.ReasoningDelta { Text.Length: > 0 } reasoning:
                    if (!open)
                    {
                        Console.Error.Write("dsh: reasoning:\n");
                        open = true;
                    }
                    Console.Error.Write(reasoning.Text);
                    endsWithNewline = reasoning.Text.EndsWith('\n');
                    break;
                case StreamChunk.BlockStart { BlockType: "reasoning" }:
                    break;
                case StreamChunk.BlockEnd { Block: ReasoningBlock }:
                    break;
                case StreamChunk.Usage:
                    break;
                default:
                    Close();
                    break;
            }
            return new ValueTask<object?>();
        });
        return new ReasoningSubscription(dispose, Close);
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

    private static int StartInTmux(string[] args)
    {
        var relaunchArgs = args.Where(argument => argument != "--tmux").ToArray();
        var dotnet = Environment.ProcessPath ?? "dotnet";
        var assembly = Environment.GetCommandLineArgs()[0];
        var command = $"{dotnet} \"{assembly}\" {string.Join(' ', relaunchArgs)}";
        Process.Start("tmux", ["new-session", "-d", "-s", "dsh", command]);
        Process.Start("tmux", ["attach", "-t", "dsh"])?.WaitForExit();
        return 0;
    }

    private sealed class ReasoningSubscription(Func<bool> dispose, Action close) : IDisposable
    {
        public void Dispose()
        {
            dispose();
            close();
        }
    }
}
