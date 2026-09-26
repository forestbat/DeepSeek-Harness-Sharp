using System.Reflection;
using System.Text;
using System.Text.Json.Nodes;

namespace Dsh.Ptc;

/** 同一可执行文件的隐藏 --ptc-host 模式: 在子进程内用 Roslyn 编译模型程序, 绑定调用经 NDJSON 帧桥回宿主。 */
public static class PtcScriptHost
{
    public const string HostArgument = "--ptc-host";
    public const int DefaultOutputLimitBytes = 64 * 1024 * 1024;

    public static async Task<int> RunAsync(string[] args)
    {
        var stdout = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false), leaveOpen: false)
        {
            AutoFlush = true,
        };
        var writer = new FrameWriter(stdout);
        await writer.WriteAsync(new JsonObject { ["t"] = "ready", ["v"] = 1 });
        var reader = new FrameReader(new StreamReader(Console.OpenStandardInput(), new UTF8Encoding(false)));
        var boot = await reader.ReadAsync();
        if (boot?["t"]?.GetValue<string>() != "boot")
        {
            writer.Write(FailureFrame(PtcFailureKinds.Protocol, "expected a boot frame"));
            return 0;
        }
        var program = boot["program"]?.GetValue<string>() ?? "";
        var bindings = (boot["bindings"] as JsonArray)?
            .Select(node => node?.GetValue<string>()).OfType<string>().ToList() ?? [];
        var outputLimit = boot["outputLimitBytes"]?.GetValue<int>() ?? DefaultOutputLimitBytes;
        var compilation = PtcProgramCompiler.Compile(program, bindings);
        if (compilation.Assembly is null)
        {
            writer.Write(FailureFrame(PtcFailureKinds.Exception, string.Join('\n', compilation.Errors)));
            return 0;
        }
        await ExecuteAsync(writer, reader, compilation.Assembly, bindings, outputLimit);
        return 0;
    }

    private static async Task ExecuteAsync(
        FrameWriter writer,
        FrameReader reader,
        byte[] assembly,
        IReadOnlyList<string> bindings,
        int outputLimit)
    {
        var channel = new GuestChannel(writer, reader, bindings);
        using var abort = new CancellationTokenSource();
        _ = channel.PumpAsync(abort.Token);
        var loadContext = new PtcProgramLoadContext();
        using var stream = new MemoryStream(assembly);
        var loaded = loadContext.LoadFromStream(stream);
        loaded.GetType("__PtcBridge")!.GetField("Call")!
            .SetValue(null, (Func<string, JsonObject, Task<string>>)channel.CallAsync);
        var console = new FrameTextWriter(writer, outputLimit);
        var original = Console.Out;
        Console.SetOut(console);
        JsonNode? value = null;
        string? kind = null;
        string? message = null;
        string? stack = null;
        try
        {
            var run = (Task<JsonNode?>)loaded.GetType("__Program")!.GetMethod("Run")!.Invoke(null, null)!;
            value = await run;
        }
        catch (Exception error)
        {
            var actual = error is TargetInvocationException { InnerException: { } inner } ? inner : error;
            (kind, message, stack) = Classify(actual);
        }
        finally
        {
            Console.SetOut(original);
            abort.Cancel();
        }
        if (kind is null && ExceedsOutputLimit(console.LogBytes, value, outputLimit))
        {
            kind = PtcFailureKinds.OutputLimit;
            message = $"program output exceeded {outputLimit} bytes";
        }
        writer.Write(kind is null
            ? new JsonObject { ["t"] = "done", ["ok"] = true, ["value"] = value?.DeepClone() }
            : FailureFrame(kind, message ?? "program failed", stack));
    }

    private static JsonObject FailureFrame(string kind, string message, string? stack = null)
        => new()
        {
            ["t"] = "done",
            ["ok"] = false,
            ["kind"] = kind,
            ["message"] = message,
            ["stack"] = stack,
        };

    private static bool ExceedsOutputLimit(long logBytes, JsonNode? value, int outputLimit)
        => logBytes + (value is null ? 0 : Encoding.UTF8.GetByteCount(value.ToJsonString())) > outputLimit;

    private static (string Kind, string Message, string? Stack) Classify(Exception error) => error switch
    {
        PtcOutputLimitException => (PtcFailureKinds.OutputLimit, error.Message, null),
        OperationCanceledException => (PtcFailureKinds.Abort, error.Message, null),
        _ => (PtcFailureKinds.Exception, error.Message, error.ToString()),
    };
}

internal sealed class PtcOutputLimitException(string message) : Exception(message);

/** 捕获程序 Console 输出, 按行发 log 帧并计账; 超限即抛出以终止程序。 */
internal sealed class FrameTextWriter : TextWriter
{
    private readonly FrameWriter _writer;
    private readonly int _outputLimitBytes;
    private readonly StringBuilder _line = new();
    private readonly Lock _gate = new();

    public FrameTextWriter(FrameWriter writer, int outputLimitBytes)
    {
        _writer = writer;
        _outputLimitBytes = outputLimitBytes;
        NewLine = "\n";
    }

    public long LogBytes { get; private set; }

    public override Encoding Encoding => Encoding.UTF8;

    public override void Write(char value) => Append(value.ToString());

    public override void Write(string? value)
    {
        if (value is not null)
            Append(value);
    }

    public override void Write(char[] buffer, int index, int count) => Append(new string(buffer, index, count));

    private void Append(string text)
    {
        lock (_gate)
        {
            foreach (var character in text)
            {
                if (character == '\n')
                    EmitLine();
                else if (character != '\r')
                    _line.Append(character);
            }
        }
    }

    private void EmitLine()
    {
        var line = _line.ToString();
        _line.Clear();
        LogBytes += Encoding.UTF8.GetByteCount(line) + 1;
        if (LogBytes > _outputLimitBytes)
            throw new PtcOutputLimitException($"program output exceeded {_outputLimitBytes} bytes");
        _writer.Write(new JsonObject { ["t"] = "log", ["text"] = line });
    }
}

/** 子进程侧绑定桥: 发 call 帧并挂起 Promise, 收到 reply 帧后用 JSON 信封唤醒程序。 */
internal sealed class GuestChannel(FrameWriter writer, FrameReader reader, IReadOnlyList<string> bindings)
{
    private readonly HashSet<string> _bindings = [.. bindings];
    private readonly Dictionary<int, TaskCompletionSource<string>> _pending = [];
    private int _nextId;

    public async Task<string> CallAsync(string toolName, JsonObject args)
    {
        if (!_bindings.Contains(toolName))
            return FailureEnvelope($"unknown tool \"{toolName}\" is not bound in this program");
        var id = Interlocked.Increment(ref _nextId);
        var completion = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_pending)
            _pending[id] = completion;
        await writer.WriteAsync(new JsonObject { ["t"] = "call", ["id"] = id, ["name"] = toolName, ["args"] = args.DeepClone() });
        return await completion.Task;
    }

    public async Task PumpAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            JsonObject? frame;
            try
            {
                frame = await reader.ReadAsync();
            }
            catch (Exception)
            {
                FailAll("invalid reply frame from the host");
                return;
            }
            if (frame is null)
                return;
            switch (frame["t"]?.GetValue<string>())
            {
                case "reply":
                    Complete(frame);
                    break;
                case "abort":
                    FailAll("the run was aborted");
                    return;
            }
        }
    }

    private void Complete(JsonObject frame)
    {
        if (!frame.TryGetPropertyValue("id", out var idNode) || idNode is null)
            return;
        TaskCompletionSource<string>? completion;
        lock (_pending)
        {
            if (!_pending.Remove(idNode.GetValue<int>(), out completion))
                return;
        }
        if (frame["ok"]?.GetValue<bool>() == true)
        {
            completion.SetResult(new JsonObject { ["ok"] = true, ["value"] = frame["value"]?.DeepClone() }.ToJsonString());
            return;
        }
        completion.SetResult(FailureEnvelope(frame["message"]?.GetValue<string>() ?? "tool call failed"));
    }

    private void FailAll(string message)
    {
        List<TaskCompletionSource<string>> pending;
        lock (_pending)
        {
            pending = [.. _pending.Values];
            _pending.Clear();
        }
        foreach (var completion in pending)
            completion.SetResult(FailureEnvelope(message));
    }

    private static string FailureEnvelope(string message)
        => new JsonObject { ["ok"] = false, ["message"] = message }.ToJsonString();
}
