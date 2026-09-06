using Dsh.Boot;
using Dsh.Core;
using Dsh.Llm;
using Dsh.Sdk;
using Dsh.Acp;
using Dsh.Web;
using Dsh.Lsp;

namespace DeepSeek_Harness_Sharp;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        string? profile = null;
        string? patch = null;
        string? home = null;
        string? config = null;
        var dumpConfig = false;
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
        var harnessHome = HarnessHome.Resolve(home);
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

        switch (profile)
        {
            case null:
            case "web":
                return await RunWeb(harnessHome, config, patches);
            case "headless":
                return await RunHeadless(harnessHome, string.Join(' ', positional), config, patches);
            case "sdk":
                return await RunSdk(harnessHome, config, patches);
            case "acp":
                return await RunAcp(harnessHome, config, patches);
            case "lsp":
                return await RunLsp(harnessHome, config, patches);
            case "tui":
                return await Dsh.Tui.TuiRunner.Run(harnessHome, Directory.GetCurrentDirectory(), config, patches);
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
              --profile <name>   headless | tui | web | sdk | acp | lsp (default: web)
              --config <path>    boot from a cordis.yml composition instead of the built-in defaults
              --home <path>      harness home (default: $DSH_HOME or ~/.dsh)
              --dump-config      print the composed configuration and exit
              -h, --help         show this help

            Commands:
              tui                start the terminal UI
              headless "task"    answer one task and exit
            """);
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

    private static async Task<int> RunWeb(HarnessHome home, string? config, IReadOnlyList<Dictionary<string, object?>>? patches)
    {
        await using var server = new WebProfileServer();
        Console.WriteLine($"dsh web: http://127.0.0.1:{server.Port}");
        await Task.Delay(Timeout.Infinite);
        return 0;
    }

    private static async Task<int> RunLsp(HarnessHome home, string? config, IReadOnlyList<Dictionary<string, object?>>? patches)
    {
        await using var transport = new JsonRpcLineTransport(Console.In, Console.Out);
        var server = new LspServer();
        transport.RequestHandler = server.HandleRequestAsync;
        transport.Start();
        await transport.WhenClosedAsync();
        return 0;
    }

    private static async Task<int> RunAcp(HarnessHome home, string? config, IReadOnlyList<Dictionary<string, object?>>? patches)
    {
        using var app = config is null
            ? HarnessComposer.Compose(new HarnessOptions(home, Directory.GetCurrentDirectory()))
            : await ConfigBoot.Compose(config, new HarnessOptions(home, Directory.GetCurrentDirectory()), patches: patches);
        await using var transport = new JsonRpcLineTransport(Console.In, Console.Out);
        var server = new AcpServer(app.Ctx, transport, app.Provider, app.Model);
        transport.RequestHandler = (method, parameters) => server.HandleRequestAsync(method, parameters);
        transport.NotificationHandler = (method, parameters) =>
        {
            if (method == AcpMethods.Cancel)
                server.Cancel(parameters);
        };
        transport.Start();
        await transport.WhenClosedAsync();
        await server.CloseAllAsync();
        return 0;
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

    private sealed class ReasoningSubscription(Func<bool> dispose, Action close) : IDisposable
    {
        public void Dispose()
        {
            dispose();
            close();
        }
    }
}
