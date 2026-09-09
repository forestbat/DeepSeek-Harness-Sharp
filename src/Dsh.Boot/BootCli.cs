using System.Reflection;
using System.Runtime.Loader;
using Cordis;

namespace Dsh.Boot;

public static class BootCli
{
    public static async Task<int> RunHeadlessAsync(
        HarnessHome home,
        string task,
        string? config,
        IReadOnlyList<Dictionary<string, object?>>? patches)
    {
        if (string.IsNullOrWhiteSpace(task))
        {
            Console.Error.WriteLine("error: a task is required, for example: dsh --profile headless \"run the tests\"");
            return 1;
        }
        using var app = await ComposeAppAsync(home, config, patches);
        using var autoApprove = InvokeStaticDisposable("Dsh.Interaction.ApprovalAnswerers", "Dsh.Interaction", "AutoApprove", app.Ctx);
        dynamic agents = app.Ctx.Get("agents")!;
        dynamic agentOptions = Activator.CreateInstance(RequireType("Dsh.Core.AgentOptions", "Dsh.Core"), app.Provider, app.Model, null, null)!;
        dynamic createOptions = Activator.CreateInstance(
            RequireType("Dsh.Core.CreateAgentOptions", "Dsh.Core"),
            null, Directory.GetCurrentDirectory(), agentOptions, null, null, null, null)!;
        dynamic handle = await agents.Create(createOptions);
        dynamic agent = handle.Agent;
        await agent.WhenIdle();
        long firstSeq = (long)agent.Session.Seq;
        using var reasoning = StreamReasoning(app.Ctx, agent);
        dynamic message = RequireType("Dsh.Llm.MessageFactory", "Dsh.Llm")
            .GetMethod("CreateUserText", [typeof(string)])!
            .Invoke(null, [task])!;
        agent.Followup(message);
        await agent.WhenIdle();
        dynamic sessions = app.Ctx.Get("sessions")!;
        await sessions.Flush(agent.Session);

        var summary = Summarize((object)agent.Session, firstSeq);
        var text = summary.Text;
        var reasonKind = summary.ReasonKind;
        var errorMessage = summary.ErrorMessage;
        Console.Out.WriteLine(text);
        if (reasonKind == "error")
        {
            Console.Error.WriteLine($"dsh: {errorMessage}");
            return 1;
        }
        return reasonKind == "completed" ? 0 : 1;
    }

    public static async Task<int> RunSdkAsync(
        HarnessHome home,
        string? config,
        IReadOnlyList<Dictionary<string, object?>>? patches)
    {
        using var app = await ComposeAppAsync(home, config, patches);
        var transportType = RequireType("Dsh.Sdk.JsonRpcLineTransport", "Dsh.Sdk");
        dynamic transport = Activator.CreateInstance(transportType, Console.In, Console.Out)!;
        var serverType = RequireType("Dsh.Sdk.HarnessSdkServer", "Dsh.Sdk");
        var server = Activator.CreateInstance(serverType, app.Ctx, (object)transport)!;
        var handlerType = transportType.Assembly.GetType("Dsh.Sdk.JsonRpcRequestHandler")!;
        transportType.GetProperty("RequestHandler")!.SetValue(transport,
            Delegate.CreateDelegate(handlerType, server, serverType.GetMethod("HandleRequestAsync")!));
        transportType.GetMethod("Start")!.Invoke(transport, null);
        await (Task)transportType.GetMethod("WhenClosedAsync")!.Invoke(transport, null)!;
        if (transport is IAsyncDisposable disposable)
            await disposable.DisposeAsync();
        return 0;
    }

    public static async Task<int> RunTuiListAsync()
    {
        try
        {
            var clientType = RequireType("Dsh.Pty.PtyDaemonClient", "Dsh.Pty");
            var task = (Task)clientType.GetMethod("ListAsync", Type.EmptyTypes)!.Invoke(null, null)!;
            await task;
            dynamic sessions = task.GetType().GetProperty("Result")!.GetValue(task)!;
            if (sessions.Count == 0)
            {
                Console.WriteLine("no PTY sessions");
                return 0;
            }
            foreach (dynamic session in sessions)
                Console.WriteLine($"{session.Id}\t{session.Command}\t{session.StartedAt:O}\t{session.Status}");
            return 0;
        }
        catch (TargetInvocationException error) when (IsPtyDaemonNotRunning(error.InnerException))
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

    public static async Task<int> RunTuiAttachAsync(string id)
    {
        try
        {
            var clientType = RequireType("Dsh.Pty.PtyDaemonClient", "Dsh.Pty");
            var method = clientType.GetMethod("AttachAsync", [typeof(string), typeof(Stream), typeof(Stream)])!;
            await (Task)method.Invoke(null, [id, Console.OpenStandardInput(), Console.OpenStandardOutput()])!;
            return 0;
        }
        catch (TargetInvocationException error) when (IsPtyDaemonNotRunning(error.InnerException))
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

    public static async Task<int> RunTuiDaemonAsync()
    {
        var daemonType = RequireType("Dsh.Pty.PtyDaemon", "Dsh.Pty");
        await using var daemon = (IAsyncDisposable)Activator.CreateInstance(daemonType)!;
        dynamic daemonDynamic = daemon;
        await daemonDynamic.StartAsync();
        var shutdown = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        ConsoleCancelEventHandler cancelHandler = (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            shutdown.TrySetResult();
        };
        Console.CancelKeyPress += cancelHandler;
        try
        {
            await shutdown.Task;
        }
        finally
        {
            Console.CancelKeyPress -= cancelHandler;
        }
        return 0;
    }

    private static async Task<HarnessApp> ComposeAppAsync(
        HarnessHome home,
        string? config,
        IReadOnlyList<Dictionary<string, object?>>? patches)
    {
        var options = new HarnessOptions(home, Directory.GetCurrentDirectory());
        return config is null
            ? await HarnessComposer.Compose(options)
            : await ConfigBoot.Compose(config, options, patches: patches);
    }

    private static IDisposable InvokeStaticDisposable(string typeName, string assemblyName, string methodName, Context ctx)
    {
        var method = RequireType(typeName, assemblyName).GetMethod(methodName, [typeof(Context)])!;
        return (IDisposable)method.Invoke(null, [ctx])!;
    }

    private static IDisposable StreamReasoning(Context ctx, dynamic agent)
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

        EventListener handler = (_, args) =>
        {
            dynamic session = args[0]!;
            if (!ReferenceEquals(session, agent.Session))
                return new ValueTask<object?>();
            dynamic sessionEvent = args[1]!;
            var data = sessionEvent.Data;
            var typeName = data.GetType().Name;
            if (typeName == "TurnStartPayload")
            {
                Close();
                started = true;
                return new ValueTask<object?>();
            }
            if (!started || typeName != "AssistantChunkPayload")
                return new ValueTask<object?>();
            dynamic chunkPayload = data;
            var chunk = chunkPayload.Chunk;
            var chunkTypeName = chunk.GetType().Name;
            if (chunkTypeName == "StreamChunk+ReasoningDelta" && chunk.Text.Length > 0)
            {
                if (!open)
                {
                    Console.Error.Write("dsh: reasoning:\n");
                    open = true;
                }
                Console.Error.Write((string)chunk.Text);
                endsWithNewline = ((string)chunk.Text).EndsWith('\n');
            }
            else if (chunkTypeName is not ("StreamChunk+BlockStart" or "StreamChunk+BlockEnd" or "StreamChunk+Usage"))
            {
                Close();
            }
            return new ValueTask<object?>();
        };
        var remove = ctx.On("session/event", handler, new EventOptions { Global = true });
        return new CallbackDisposable(() =>
        {
            remove();
            Close();
        });
    }

    private static (string Text, string ReasonKind, string? ErrorMessage) Summarize(dynamic session, long firstSeq)
    {
        var started = false;
        var text = "";
        string reasonKind = "";
        string? errorMessage = null;
        for (var seq = firstSeq; seq < (long)session.Seq; seq++)
        {
            dynamic sessionEvent = session.EventAt(seq);
            if (sessionEvent is null)
                throw new InvalidOperationException($"headless summary cannot read seq {seq} below captured length {session.Seq}");
            dynamic data = sessionEvent.Data;
            var typeName = data.GetType().Name;
            switch (typeName)
            {
                case "TurnStartPayload":
                    started = true;
                    break;
                case "AssistantMessagePayload" when started:
                {
                    dynamic message = data.Message;
                    var joined = string.Concat(((IEnumerable<object>)message.Content)
                        .Where(block => block.GetType().Name == "TextBlock")
                        .Select(block => (string)((dynamic)block).Text));
                    if (joined != "")
                        text = joined;
                    break;
                }
                case "TurnEndPayload":
                    reasonKind = (string)data.Reason.Kind;
                    if (reasonKind == "error")
                        errorMessage = (string)data.Reason.Failure.Message;
                    break;
            }
        }
        return (text, reasonKind, errorMessage);
    }

    private static bool IsPtyDaemonNotRunning(Exception? error)
        => error?.GetType().Name == "PtyDaemonNotRunningException";

    private static Type RequireType(string typeName, string assemblyName)
    {
        var type = Type.GetType($"{typeName}, {assemblyName}", throwOnError: false);
        if (type is null)
        {
            var path = Path.Combine(AppContext.BaseDirectory, $"{assemblyName}.dll");
            if (File.Exists(path))
            {
                try
                {
                    var assembly = AssemblyLoadContext.Default.LoadFromAssemblyPath(path);
                    type = assembly.GetType(typeName);
                }
                catch (Exception)
                {
                    type = null;
                }
            }
        }
        type ??= AppDomain.CurrentDomain.GetAssemblies().Select(assembly => assembly.GetType(typeName)).FirstOrDefault(candidate => candidate is not null);
        if (type is null)
            throw new CordisException("SURFACE_NOT_FOUND", $"surface assembly \"{assemblyName}\" is not available: {typeName}");
        return type;
    }

    private sealed class CallbackDisposable(Action dispose) : IDisposable
    {
        public void Dispose() => dispose();
    }
}