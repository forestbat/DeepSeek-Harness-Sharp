using System.Collections;
using System.Globalization;
using System.Reflection;
using System.Runtime.Loader;
using Dsh.Runtime;

namespace Dsh.Boot;

public static class BootCli
{
    public static async Task<int> RunHeadlessAsync(HarnessHome home, string task)
    {
        if (string.IsNullOrWhiteSpace(task))
        {
            await Console.Error.WriteLineAsync("error: a task is required, for example: dsh headless \"run the tests\"");
            return 1;
        }
        using var app = await HarnessComposer.Compose(new HarnessOptions(home, Directory.GetCurrentDirectory()));
        using var autoApprove = InvokeStaticDisposable("Dsh.Interaction.ApprovalAnswerers", "Dsh.Interaction", "AutoApprove", app.Ctx);
        var agents = app.Ctx.Get("agents")!;
        var agentOptions = Activator.CreateInstance(RequireType("Dsh.Core.AgentOptions", "Dsh.Core"), app.Provider, app.Model, null, null)!;
        var createOptions = Activator.CreateInstance(
            RequireType("Dsh.Core.CreateAgentOptions", "Dsh.Core"),
            [null, Directory.GetCurrentDirectory(), agentOptions, null, null, null, null])!;
        var handle = await AwaitAsync(InvokeMethod(agents, "Create", createOptions));
        var agent = RequireProp(handle, "Agent");
        await AwaitAsync(InvokeMethod(agent, "WhenIdle"));
        var session = RequireProp(agent, "Session");
        var firstSeq = SeqOf(session);
        using var reasoning = StreamReasoning(app.Ctx, session);
        var message = InvokeStatic(RequireType("Dsh.Llm.MessageFactory", "Dsh.Llm"), "CreateUserText", task)!;
        InvokeMethod(agent, "Followup", message);
        await AwaitAsync(InvokeMethod(agent, "WhenIdle"));
        var sessions = app.Ctx.Get("sessions")!;
        await AwaitAsync(InvokeMethod(sessions, "Flush", session));

        var (text, reasonKind, errorMessage) = Summarize(session, firstSeq);
        Console.Out.WriteLine(text);
        if (reasonKind == "error")
        {
            Console.Error.WriteLine($"dsh: {errorMessage}");
            return 1;
        }
        return reasonKind == "completed" ? 0 : 1;
    }

    public static async Task<int> RunTuiListAsync()
    {
        try
        {
            var clientType = RequireType("Dsh.Pty.PtyDaemonClient", "Dsh.Pty");
            await (Task)InvokeStatic(clientType, "EnsureRunningAsync", CancellationToken.None)!;
            var task = (Task)InvokeStatic(clientType, "ListAsync", CancellationToken.None)!;
            await task;
            var sessions = task.GetType().GetProperty("Result")!.GetValue(task)!;
            var printed = 0;
            foreach (var session in (IEnumerable)sessions)
            {
                var startedAt = (DateTimeOffset)RequireProp(session, "StartedAt");
                Console.WriteLine($"{RequireProp(session, "Id")}\t{RequireProp(session, "Command")}\t{startedAt:O}\t{RequireProp(session, "Status")}");
                printed++;
            }
            if (printed == 0)
                Console.WriteLine("no PTY sessions");
            return 0;
        }
        catch (Exception error) when (IsPtyDaemonNotRunning(error))
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
            await (Task)InvokeStatic(clientType, "EnsureRunningAsync", CancellationToken.None)!;
            await (Task)InvokeStatic(clientType, "AttachAsync", id, Console.OpenStandardInput(), Console.OpenStandardOutput(), CancellationToken.None)!;
            return 0;
        }
        catch (Exception error) when (IsPtyDaemonNotRunning(error))
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
        await using var daemon = (IAsyncDisposable)Activator.CreateInstance(daemonType, [null, null, null])!;
        await (Task)InvokeMethod(daemon, "StartAsync")!;
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

    private static IDisposable InvokeStaticDisposable(string typeName, string assemblyName, string methodName, Context ctx)
    {
        var type = RequireType(typeName, assemblyName);
        return (IDisposable)InvokeStatic(type, methodName, ctx)!;
    }

    private static IDisposable StreamReasoning(Context ctx, object session)
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

        Action<object> handler = notification =>
        {
            if (!ReferenceEquals(Prop(notification, "Session"), session))
                return;
            var sessionEvent = Prop(notification, "Event");
            var data = sessionEvent is null ? null : Prop(sessionEvent, "Data");
            if (data is null)
                return;
            var typeName = data.GetType().Name;
            if (typeName == "TurnStartPayload")
            {
                Close();
                started = true;
                return;
            }
            if (!started || typeName != "AssistantChunkPayload")
                return;
            var chunk = Prop(data, "Chunk");
            if (chunk is null)
                return;
            var chunkTypeName = chunk.GetType().Name;
            var text = Prop(chunk, "Text") as string;
            if (chunkTypeName == "StreamChunk+ReasoningDelta" && text is { Length: > 0 })
            {
                if (!open)
                {
                    Console.Error.Write("dsh: reasoning:\n");
                    open = true;
                }
                Console.Error.Write(text);
                endsWithNewline = text.EndsWith('\n');
            }
            else if (chunkTypeName is not ("StreamChunk+BlockStart" or "StreamChunk+BlockEnd" or "StreamChunk+Usage"))
            {
                Close();
            }
        };
        var remove = SubscribeNotification(ctx, RequireType("Dsh.Core.SessionEventNotification", "Dsh.Core"), handler);
        return new CallbackDisposable(() =>
        {
            remove();
            Close();
        });
    }

    private static (string Text, string ReasonKind, string? ErrorMessage) Summarize(object session, long firstSeq)
    {
        var started = false;
        var text = "";
        string reasonKind = "";
        string? errorMessage = null;
        for (var seq = firstSeq; seq < SeqOf(session); seq++)
        {
            var sessionEvent = InvokeMethod(session, "EventAt", seq);
            if (sessionEvent is null)
                throw new InvalidOperationException($"headless summary cannot read seq {seq} below captured length {SeqOf(session)}");
            var data = Prop(sessionEvent, "Data");
            if (data is null)
                continue;
            var typeName = data.GetType().Name;
            switch (typeName)
            {
                case "TurnStartPayload":
                    started = true;
                    break;
                case "AssistantMessagePayload" when started:
                    {
                        var message = Prop(data, "Message");
                        if (message is null)
                            break;
                        var joined = string.Concat(((IEnumerable)(Prop(message, "Content") ?? Array.Empty<object>()))
                            .Cast<object>()
                            .Where(block => block.GetType().Name == "TextBlock")
                            .Select(block => Prop(block, "Text") as string ?? ""));
                        if (joined != "")
                            text = joined;
                        break;
                    }
                case "TurnEndPayload":
                    var reason = Prop(data, "Reason");
                    if (reason is null)
                        break;
                    reasonKind = Prop(reason, "Kind") as string ?? "";
                    if (reasonKind == "error" && Prop(reason, "Failure") is { } failure)
                        errorMessage = Prop(failure, "Message") as string;
                    break;
            }
        }
        return (text, reasonKind, errorMessage);
    }

    private static long SeqOf(object session) => Convert.ToInt64(Prop(session, "Seq") ?? 0L, CultureInfo.InvariantCulture);

    private static object? Prop(object target, string name)
    {
        var type = target.GetType();
        if (type.GetProperty(name, BindingFlags.Public | BindingFlags.Instance) is { } property)
            return property.GetValue(target);
        if (type.GetField(name, BindingFlags.Public | BindingFlags.Instance) is { } field)
            return field.GetValue(target);
        return null;
    }

    private static object RequireProp(object? target, string name)
        => target is null
            ? throw new RuntimeException("SURFACE_NOT_FOUND", $"cannot read property \"{name}\" from a null value")
            : Prop(target, name) ?? throw new RuntimeException("SURFACE_NOT_FOUND", $"{target.GetType().FullName} has no property \"{name}\"");

    private static object? InvokeMethod(object target, string name, params object?[] args)
    {
        var (method, converted) = RequireMethod(target.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance), name, args, target.GetType().FullName ?? target.GetType().Name);
        return method.Invoke(target, converted);
    }

    private static object? InvokeStatic(Type type, string name, params object?[] args)
    {
        var (method, converted) = RequireMethod(type.GetMethods(BindingFlags.Public | BindingFlags.Static), name, args, type.FullName ?? type.Name);
        return method.Invoke(null, converted);
    }

    private static (MethodInfo Method, object?[] Args) RequireMethod(IEnumerable<MethodInfo> candidates, string name, object?[] args, string owner)
    {
        var seen = new List<string>();
        foreach (var method in candidates)
        {
            if (method.Name != name)
                continue;
            var parameters = method.GetParameters();
            if (parameters.Length < args.Length)
                continue;
            if (parameters.Skip(args.Length).Any(parameter => !parameter.IsOptional))
                continue;
            var converted = new object?[parameters.Length];
            for (var index = args.Length; index < parameters.Length; index++)
                converted[index] = Type.Missing;
            var match = true;
            for (var index = 0; index < args.Length; index++)
            {
                var argument = args[index];
                var parameterType = parameters[index].ParameterType;
                if (argument is null)
                {
                    if (parameterType.IsValueType && Nullable.GetUnderlyingType(parameterType) is null)
                    {
                        match = false;
                        break;
                    }
                    continue;
                }
                if (parameterType.IsInstanceOfType(argument))
                {
                    converted[index] = argument;
                    continue;
                }
                if (Nullable.GetUnderlyingType(parameterType) is { } underlying && underlying.IsInstanceOfType(argument))
                {
                    converted[index] = argument;
                    continue;
                }
                if (parameterType.IsPrimitive && argument is IConvertible)
                {
                    converted[index] = Convert.ChangeType(argument, parameterType, CultureInfo.InvariantCulture);
                    continue;
                }
                match = false;
                break;
            }
            if (match)
                return (method, converted);
            seen.Add($"{method.Name}({string.Join(", ", parameters.Select(parameter => parameter.ParameterType.Name))})");
        }
        throw new RuntimeException("SURFACE_NOT_FOUND",
            $"no method {name}({string.Join(", ", args.Select(argument => argument?.GetType().Name ?? "null"))}) on {owner}"
            + (seen.Count > 0 ? $"; candidates: {string.Join("; ", seen)}" : ""));
    }

    private static async Task<object?> AwaitAsync(object? awaitable)
    {
        switch (awaitable)
        {
            case null:
                return null;
            case Task task:
                await task;
                return task.GetType().IsGenericType
                    ? task.GetType().GetProperty("Result")!.GetValue(task)
                    : null;
            case ValueTask valueTask:
                await valueTask;
                return null;
            default:
                {
                    var asTask = awaitable.GetType().GetMethod("AsTask", Type.EmptyTypes);
                    if (asTask is null)
                        return awaitable;
                    var task = (Task)asTask.Invoke(awaitable, null)!;
                    await task;
                    return task.GetType().IsGenericType
                        ? task.GetType().GetProperty("Result")!.GetValue(task)
                        : null;
                }
        }
    }

    private static bool IsPtyDaemonNotRunning(Exception? error)
    {
        if (error is null)
            return false;
        if (error.GetType().Name == "PtyDaemonNotRunningException")
            return true;
        if (error is AggregateException aggregate)
            return aggregate.InnerExceptions.Any(IsPtyDaemonNotRunning);
        return error.InnerException is not null && IsPtyDaemonNotRunning(error.InnerException);
    }

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
            throw new RuntimeException("SURFACE_NOT_FOUND", $"surface assembly \"{assemblyName}\" is not available: {typeName}");
        return type;
    }

    private static Func<bool> SubscribeNotification(Context ctx, Type notificationType, Action<object> handler)
        => ctx.On(notificationType, notification =>
        {
            handler(notification);
            return ValueTask.CompletedTask;
        }, new EventOptions { Global = true });

    private sealed class CallbackDisposable(Action dispose) : IDisposable
    {
        public void Dispose() => dispose();
    }
}
