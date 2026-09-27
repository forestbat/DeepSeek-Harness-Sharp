using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Dsh.Core;

namespace Dsh.Ptc;

public sealed record PtcToolCall(string ToolName, JsonObject Arguments, int Id);

public sealed record PtcToolReply(bool Ok, JsonNode? Value, string? Message);

public sealed record PtcRunOutcome(bool Ok, string? Kind, string? Message, string? Stack, JsonNode? Value, string Logs);

/** 宿主侧客户端: spawn 同二进制 --ptc-host 子进程, 用 NDJSON 帧驱动编译执行并桥接绑定调用。 */
public sealed class PtcScriptHostClient
{
    private const int PollIntervalMs = 10;
    private const int GraceMs = 3000;
    private const int FrameSlackBytes = 1024 * 1024;
    private const int MaxStderrBytes = 64 * 1024;
    public const int DefaultOutputLimitBytes = 64 * 1024 * 1024;
    public const int MaxPendingCalls = 128;

    private readonly SubprocessService _subprocess;
    private readonly string _cwd;
    private readonly IReadOnlyList<string> _bindings;
    private readonly Func<PtcToolCall, CancellationToken, Task<PtcToolReply>> _dispatch;
    private readonly int _outputLimitBytes;
    private readonly int _maxPendingCalls;
    private readonly Lock _writeGate = new();

    public PtcScriptHostClient(
        SubprocessService subprocess,
        string cwd,
        IReadOnlyList<string> bindings,
        Func<PtcToolCall, CancellationToken, Task<PtcToolReply>> dispatch,
        int outputLimitBytes = DefaultOutputLimitBytes,
        int maxPendingCalls = MaxPendingCalls)
    {
        _subprocess = subprocess;
        _cwd = cwd;
        _bindings = bindings;
        _dispatch = dispatch;
        _outputLimitBytes = outputLimitBytes;
        _maxPendingCalls = maxPendingCalls;
    }

    /**
     * 自旋脚本宿主的启动命令: 常规是当前 apphost; 若当前进程是 dotnet 驱动(`dotnet exec` / `dotnet test`),
     * 直接把它当宿主会变成 `dotnet --ptc-host`(驱动收到未知参数报错, 帧非法), 故改为 `dotnet <入口程序集> --ptc-host`。
     * 两处入口(HarnessEntrypoint 与测试工程)都按"参数中任意位置出现 --ptc-host"派发宿主, 因此两种形式都成立。
     */
    public static (string FileName, string[] Arguments)? ResolveHostCommand()
    {
        var host = Environment.ProcessPath;
        if (string.IsNullOrEmpty(host))
            return null;
        var entryAssembly = Assembly.GetEntryAssembly()?.Location;
        if (string.Equals(Path.GetFileNameWithoutExtension(host), "dotnet", StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrEmpty(entryAssembly))
            return (host, [entryAssembly, PtcScriptHost.HostArgument]);
        return (host, [PtcScriptHost.HostArgument]);
    }

    public async Task<PtcRunOutcome> RunAsync(string program, long timeoutMs, CancellationToken signal)
    {
        var command = ResolveHostCommand();
        if (command is null)
            return new PtcRunOutcome(false, PtcFailureKinds.SandboxUnavailable, "the harness executable path is unavailable", null, null, "");
        using var handle = _subprocess.Spawn(new SubprocessSpawnSpec
        {
            Argv = [command.Value.FileName, .. command.Value.Arguments],
            Cwd = _cwd,
            RedirectStandardInput = true,
            Signal = signal,
            Stdout = new SubprocessCollect(_outputLimitBytes + FrameSlackBytes, null),
            Stderr = new SubprocessCollect(MaxStderrBytes, null),
        });
        try
        {
            return await DriveAsync(handle, program, timeoutMs, signal);
        }
        finally
        {
            await WaitExitAsync(handle);
        }
    }

    private async Task<PtcRunOutcome> DriveAsync(SubprocessHandle handle, string program, long timeoutMs, CancellationToken signal)
    {
        using var dispatch = CancellationTokenSource.CreateLinkedTokenSource(signal);
        var state = new Exchange { Program = program, Bindings = _bindings, Dispatch = dispatch };
        var stdin = handle.StandardInput;
        stdin.NewLine = "\n";
        stdin.AutoFlush = true;
        using var timeout = new CancellationTokenSource();
        var timeoutTask = Task.Delay(TimeSpan.FromMilliseconds(timeoutMs), timeout.Token);
        while (true)
        {
            await PumpAsync(handle, state, stdin);
            if (state.Done is not null)
            {
                await Task.WhenAll(state.Inflight);
                return OutcomeFromDone(state);
            }
            if (state.FatalKind is not null)
                return await AbortAsync(handle, stdin, state, state.FatalKind, state.FatalMessage ?? "script host failure");
            if (signal.IsCancellationRequested)
                return await AbortAsync(handle, stdin, state, PtcFailureKinds.Abort, "the run was cancelled");
            if (timeoutTask.IsCompleted)
                return await AbortAsync(handle, stdin, state, PtcFailureKinds.Timeout, $"the run exceeded {timeoutMs}ms");
            if (handle.Done.IsCompleted)
                return await WorkerExitAsync(handle, state, stdin);
            await Task.WhenAny(Task.Delay(PollIntervalMs), handle.Done, timeoutTask);
        }
    }

    private async Task<PtcRunOutcome> WorkerExitAsync(SubprocessHandle handle, Exchange state, StreamWriter stdin)
    {
        await PumpAsync(handle, state, stdin);
        if (state.Done is not null)
        {
            await Task.WhenAll(state.Inflight);
            return OutcomeFromDone(state);
        }
        if (state.FatalKind is not null)
            return Failure(state, state.FatalKind, state.FatalMessage ?? "script host failure");
        var stderr = handle.StderrReader.ReadFrom(0).Text.Trim();
        return Failure(state, PtcFailureKinds.WorkerExit,
            stderr.Length == 0 ? "the script host exited before reporting a result" : $"the script host exited: {stderr}");
    }

    private async Task PumpAsync(SubprocessHandle handle, Exchange state, StreamWriter stdin)
    {
        var read = handle.StdoutReader.ReadFrom(state.Offset);
        if (read.Lossy)
        {
            state.Fail(PtcFailureKinds.Protocol, "the script host output was truncated");
            return;
        }
        state.Offset = read.NextOffset;
        if (read.Text.Length == 0)
            return;
        state.Ledger += Encoding.UTF8.GetByteCount(read.Text);
        if (state.Ledger > _outputLimitBytes + FrameSlackBytes)
        {
            state.Fail(PtcFailureKinds.OutputLimit, $"program output exceeded {_outputLimitBytes} bytes");
            return;
        }
        state.Buffer.Append(read.Text);
        while (TryTakeLine(state.Buffer, out var line))
        {
            if (line.Length == 0)
                continue;
            var frame = ParseFrame(line, state);
            if (frame is null)
                return;
            await HandleFrameAsync(frame, state, stdin);
            if (state.Done is not null || state.FatalKind is not null)
                return;
        }
    }

    private async Task HandleFrameAsync(JsonObject frame, Exchange state, StreamWriter stdin)
    {
        try
        {
            switch (frame["t"]?.GetValue<string>())
            {
                case "ready":
                    await HandleReadyAsync(state, stdin);
                    break;
                case "log":
                    state.LogLines.Add(frame["text"]?.GetValue<string>() ?? "");
                    break;
                case "call":
                    StartCall(frame, state, stdin);
                    break;
                case "done":
                    state.Done = frame;
                    break;
                default:
                    state.Fail(PtcFailureKinds.Protocol, "the script host sent an unknown frame");
                    break;
            }
        }
        catch (Exception error) when (error is InvalidOperationException or FormatException or JsonException)
        {
            state.Fail(PtcFailureKinds.Protocol, "the script host sent a malformed frame");
        }
    }

    private Task HandleReadyAsync(Exchange state, StreamWriter stdin)
    {
        if (state.Ready)
            return Task.CompletedTask;
        state.Ready = true;
        return WriteAsync(stdin, new JsonObject
        {
            ["t"] = "boot",
            ["program"] = state.Program,
            ["bindings"] = new JsonArray(state.Bindings.Select(name => (JsonNode?)JsonValue.Create(name)).ToArray()),
            ["outputLimitBytes"] = _outputLimitBytes,
        });
    }

    private void StartCall(JsonObject frame, Exchange state, StreamWriter stdin)
    {
        if (!frame.TryGetPropertyValue("id", out var idNode) || idNode is null
            || !frame.TryGetPropertyValue("name", out var nameNode) || nameNode is null)
        {
            state.Fail(PtcFailureKinds.Protocol, "the script host sent a malformed call frame");
            return;
        }
        var id = idNode.GetValue<int>();
        var name = nameNode.GetValue<string>();
        if (name.Length == 0 || id <= state.LastCallId)
        {
            state.Fail(PtcFailureKinds.Protocol, "the script host sent an out-of-order call frame");
            return;
        }
        state.LastCallId = id;
        var arguments = frame["args"] as JsonObject ?? new JsonObject();
        if (state.Pending >= _maxPendingCalls)
        {
            state.Inflight.Add(ReplyAsync(stdin, id, new PtcToolReply(false, null, $"too many concurrent tool calls (max {_maxPendingCalls})")));
            return;
        }
        state.Pending++;
        state.Inflight.Add(DispatchAsync(stdin, id, name, arguments, state));
    }

    private async Task DispatchAsync(StreamWriter stdin, int id, string name, JsonObject arguments, Exchange state)
    {
        try
        {
            var reply = await _dispatch(new PtcToolCall(name, arguments, id), state.Dispatch.Token);
            await ReplyAsync(stdin, id, reply);
        }
        catch (Exception error)
        {
            await ReplyAsync(stdin, id, new PtcToolReply(false, null, error.Message));
        }
        finally
        {
            state.Pending--;
        }
    }

    private Task ReplyAsync(StreamWriter stdin, int id, PtcToolReply reply)
    {
        var frame = new JsonObject { ["t"] = "reply", ["id"] = id, ["ok"] = reply.Ok };
        if (reply.Ok)
            frame["value"] = reply.Value?.DeepClone();
        else
            frame["message"] = reply.Message ?? "tool call failed";
        return WriteAsync(stdin, frame);
    }

    private async Task<PtcRunOutcome> AbortAsync(SubprocessHandle handle, StreamWriter stdin, Exchange state, string kind, string message)
    {
        state.Dispatch.Cancel();
        try
        {
            await WriteAsync(stdin, new JsonObject { ["t"] = "abort" });
        }
        catch (IOException)
        {
        }
        handle.Terminate();
        await Task.WhenAny(Task.WhenAll(state.Inflight), Task.Delay(GraceMs));
        return Failure(state, kind, message);
    }

    private Task WriteAsync(StreamWriter stdin, JsonObject frame)
    {
        lock (_writeGate)
        {
            stdin.Write(frame.ToJsonString());
            stdin.Write('\n');
            stdin.Flush();
        }
        return Task.CompletedTask;
    }

    private static async Task WaitExitAsync(SubprocessHandle handle)
    {
        if (handle.Done.IsCompleted)
            return;
        var completed = await Task.WhenAny(handle.Done, Task.Delay(GraceMs));
        if (completed == handle.Done)
            return;
        handle.Terminate();
        await Task.WhenAny(handle.Done, Task.Delay(GraceMs));
    }

    private static bool TryTakeLine(StringBuilder buffer, out string line)
    {
        for (var index = 0; index < buffer.Length; index++)
        {
            if (buffer[index] != '\n')
                continue;
            line = buffer.ToString(0, index);
            buffer.Remove(0, index + 1);
            return true;
        }
        line = "";
        return false;
    }

    private static JsonObject? ParseFrame(string line, Exchange state)
    {
        try
        {
            var frame = JsonNode.Parse(line) as JsonObject;
            if (frame is null)
                state.Fail(PtcFailureKinds.Protocol, "the script host sent an invalid frame");
            return frame;
        }
        catch (JsonException)
        {
            state.Fail(PtcFailureKinds.Protocol, "the script host sent an invalid frame");
            return null;
        }
    }

    private static PtcRunOutcome OutcomeFromDone(Exchange state)
    {
        var done = state.Done!;
        if (done["ok"]?.GetValue<bool>() == true)
            return new PtcRunOutcome(true, null, null, null, done["value"]?.DeepClone(), state.LogsText());
        return new PtcRunOutcome(
            false,
            done["kind"]?.GetValue<string>() ?? PtcFailureKinds.InvalidOutput,
            done["message"]?.GetValue<string>() ?? "script host failed",
            done["stack"]?.GetValue<string>(),
            null,
            state.LogsText());
    }

    private static PtcRunOutcome Failure(Exchange state, string kind, string message)
        => new(false, kind, message, null, null, state.LogsText());

    private sealed class Exchange
    {
        public string Program { get; init; } = "";
        public IReadOnlyList<string> Bindings { get; init; } = [];
        public CancellationTokenSource Dispatch { get; init; } = null!;
        public StringBuilder Buffer { get; } = new();
        public List<string> LogLines { get; } = [];
        public List<Task> Inflight { get; } = [];
        public long Offset;
        public long Ledger;
        public bool Ready;
        public int Pending;
        public int LastCallId;
        public JsonObject? Done;
        public string? FatalKind;
        public string? FatalMessage;

        public void Fail(string kind, string message)
        {
            if (FatalKind is not null)
                return;
            FatalKind = kind;
            FatalMessage = message;
        }

        public string LogsText() => string.Join('\n', LogLines);
    }
}
