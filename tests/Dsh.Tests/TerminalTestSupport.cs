using System.Text.Json;
using Cordis;
using Dsh.Core;
using Dsh.Llm;
using Dsh.Terminal;

namespace Dsh.Tests;

internal sealed class TerminalFakeAgent : IAgent
{
    private readonly AgentRegistry _agents;

    public TerminalFakeAgent(Context ctx, AgentRegistry agents, string directory)
    {
        Ctx = ctx;
        _agents = agents;
        var id = SessionId.Create($"session-{Guid.NewGuid():N}");
        Session = Session.Create(id, null, new SessionHeader
        {
            Version = SessionHeader.SessionFormatVersion,
            Id = id,
            CreatedAt = 0,
            Cwd = directory,
            IsSeeded = false,
        });
    }

    public SessionId Id => Session.Id;
    public Session Session { get; }
    public ScopeKey ScopeKey { get; } = new();
    public Context Ctx { get; }
    public AgentStatus Status => AgentStatus.Idle;
    public AgentOptions Options { get; } = new();

    public void Register() => _agents.Register(this);

    public void Cancel(AgentCancelCause cause, bool keepInbox = false) { }
    public Task WhenIdle() => Task.CompletedTask;
    public void Send(UserMessage message, string target, bool wakeup) { }
    public void Followup(UserMessage message) { }
    public void Steer(UserMessage message) { }
    public void Inject(UserMessage message) { }
}

internal sealed class StubTerminalSession : TerminalBackendSession
{
    private readonly object _gate = new();
    private bool _cancelled;

    public StubTerminalSession(string motd = "stub prompt", int pid = 42, TerminalSessionStatus? status = null)
    {
        Motd = motd;
        Pid = pid;
        StatusValue = status ?? TerminalSessionStatus.Running();
    }

    public string Motd { get; }
    public int? Pid { get; }
    public TerminalSessionStatus StatusValue { get; set; }
    public bool AutoSettle { get; set; } = true;
    public bool RejectOperation { get; set; }
    public string Viewport { get; set; } = "command output";
    public string Delta { get; set; } = "live output";
    public bool DeltaTruncated { get; set; }
    public StubTerminalOperation? Operation { get; private set; }
    public TaskCompletionSource? CloseGate { get; set; }
    public List<string> ClosedWith { get; } = [];

    public TerminalSendOperation StartSend(TerminalSendRequest request)
    {
        if (RejectOperation)
        {
            var failed = new StubTerminalOperation();
            failed.Fail(new InvalidOperationException("operation failed"));
            return failed;
        }
        var operation = new StubTerminalOperation();
        operation.ReadOutputHandler = () => new TerminalSendRead(Delta, DeltaTruncated);
        operation.CancelHandler = () =>
        {
            lock (_gate)
            {
                if (_cancelled)
                    return false;
                _cancelled = true;
                operation.Settle(new TerminalSendResult("^C", TerminalWaitReason.StdinRead, StatusValue, false));
                return true;
            }
        };
        Operation = operation;
        if (AutoSettle)
        {
            _ = Task.Run(() => operation.Settle(new TerminalSendResult(Viewport, TerminalWaitReason.StdinRead, StatusValue, false)));
        }
        return operation;
    }

    public TerminalReadResult Read(TerminalReadRequest request)
        => new("history", 1, 0, 1, false);

    public Task<TerminalSignalResult> Signal(TerminalSignal signal)
        => Task.FromResult(new TerminalSignalResult(true, signal == TerminalSignal.SIGINT ? 10 : 11));

    public TerminalSessionStatus Status() => StatusValue;

    public async Task Close(string reason)
    {
        if (CloseGate is not null)
            await CloseGate.Task;
        ClosedWith.Add(reason);
        StatusValue = TerminalSessionStatus.Exited(0, null);
    }
}

internal sealed class StubTerminalOperation : TerminalSendOperation
{
    private readonly TaskCompletionSource<TerminalSendResult> _promise = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool _finished;

    public Func<bool>? CancelHandler { get; set; }
    public Func<TerminalSendRead>? ReadOutputHandler { get; set; }

    public Task<TerminalSendResult> Done => _promise.Task;

    public TerminalSendRead ReadOutput() => ReadOutputHandler?.Invoke() ?? new TerminalSendRead("live output", false);

    public bool Cancel()
    {
        if (_finished)
            return false;
        return CancelHandler?.Invoke() ?? false;
    }

    public void Settle(TerminalSendResult result)
    {
        if (_finished)
            return;
        _finished = true;
        _promise.TrySetResult(result);
    }

    public void Fail(Exception error)
    {
        if (_finished)
            return;
        _finished = true;
        _promise.TrySetException(error);
    }
}

internal sealed class StubTerminalBackend : TerminalBackend
{
    private readonly Func<Task<TerminalBackendSession>> _spawn;

    public StubTerminalBackend(string type, Func<Task<TerminalBackendSession>>? spawn = null)
    {
        Type = type;
        _spawn = spawn ?? (() => Task.FromResult<TerminalBackendSession>(new StubTerminalSession()));
    }

    public string Type { get; }
    public List<StubTerminalSession> Sessions { get; } = [];

    public async Task<TerminalBackendSession> Spawn(TerminalBackendSpawnSpec spec)
    {
        var session = await _spawn();
        if (session is StubTerminalSession stub)
            Sessions.Add(stub);
        return session;
    }
}

internal static class TerminalTestTools
{
    public static Task<ToolExecutionResult> Execute(ToolRuntime tools, string name, object arguments, IAgent? agent = null)
        => tools.Execute(new ToolExecutionInput
        {
            CallId = ToolCallId.Create($"call-{Guid.NewGuid():N}"),
            Name = name,
            Arguments = JsonSerializer.SerializeToElement(arguments, DshJson.Options),
            Agent = agent,
            Signal = default,
        });

    public static string TextOf(ToolExecutionResult result)
        => string.Concat(result.Content.OfType<TextBlock>().Select(block => block.Text));
}
