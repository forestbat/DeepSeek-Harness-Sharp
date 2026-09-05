using System.Text;
using Cordis;
using Dsh.Core;
using Dsh.Tools;

namespace Dsh.Terminal;

public sealed class BashTerminalBackend : TerminalBackend
{
    private readonly Context _ctx;
    private readonly ResolvedTerminalBashConfig _config;

    public BashTerminalBackend(Context ctx, ResolvedTerminalBashConfig config)
    {
        _ctx = ctx;
        _config = config;
    }

    public string Type => _config.BackendType;

    public async Task<TerminalBackendSession> Spawn(TerminalBackendSpawnSpec spec)
    {
        spec.Signal.ThrowIfCancellationRequested();
        var subprocess = _ctx.Get<SubprocessService>(SubprocessService.ServiceName)
            ?? throw new InvalidOperationException("terminal-bash requires the subprocess service");
        var shellPath = await subprocess.ResolveExecutable(_config.ShellPath, signal: spec.Signal);
        var cwd = spec.Cwd ?? spec.Owner.Session.Header.Cwd ?? Environment.CurrentDirectory;
        var argv = new List<string> { shellPath };
        argv.AddRange(_config.ShellArgs);
        var handle = subprocess.Spawn(new SubprocessSpawnSpec
        {
            Argv = argv,
            Cwd = cwd,
            Env = ChildEnvironment(spec),
            RedirectStandardInput = true,
            Signal = spec.Signal,
        });
        var session = new PipeTerminalSession(handle, _config);
        try
        {
            await session.InitializeAsync(spec.Signal);
            return session;
        }
        catch (Exception error)
        {
            try
            {
                await session.Close("PTY startup failed");
            }
            catch (Exception closeError)
            {
                throw new TerminalBackendCleanupError(error, closeError);
            }
            throw;
        }
    }

    private IReadOnlyDictionary<string, string?> ChildEnvironment(TerminalBackendSpawnSpec spec)
    {
        var common = new Dictionary<string, string?>
        {
            ["TERM"] = "dumb",
            ["PAGER"] = "cat",
            ["GIT_PAGER"] = "cat",
            ["DSH_SHELL"] = "1",
            ["DSH_SESSION_ID"] = spec.Owner.Id.Value,
            ["DSH_PTY_SESSION_ID"] = spec.SessionId.Value,
        };
        if (_config.ShellDialect == ShellDialect.Pwsh)
        {
            common["NO_COLOR"] = "1";
            return common;
        }
        common["PS1"] = TerminalPrompt.ControlledPrompt;
        common["PROMPT_COMMAND"] = $"printf \"\\033]{TerminalPrompt.MarkerPrefix}%s\\007\" \"$?\"; PS1='{TerminalPrompt.ControlledPrompt}'";
        common["BASH_SILENCE_DEPRECATION_WARNING"] = "1";
        return common;
    }
}

public static class TerminalBash
{
    public const string PluginName = "terminal-bash";

    public static IDisposable Register(Context ctx, TerminalBashConfig? config = null)
    {
        var resolved = TerminalBashConfigResolver.Resolve(config);
        TerminalBashConfigResolver.Validate(resolved);
        var terminals = ctx.Get<TerminalSessionService>(TerminalSessionService.ServiceName)
            ?? throw new InvalidOperationException("terminal-bash requires the terminals service");
        var backend = new BashTerminalBackend(ctx, resolved);
        return new TerminalDisposable(terminals.RegisterBackend(backend));
    }

    private sealed class TerminalDisposable(Action dispose) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            dispose();
        }
    }
}

internal sealed class BoundedTextBuffer
{
    private readonly int _maxBytes;
    private readonly int? _maxLines;
    private string _value = "";
    private bool _dropped;

    public BoundedTextBuffer(int maxBytes, int? maxLines = null)
    {
        _maxBytes = maxBytes;
        _maxLines = maxLines;
    }

    public void Append(string text)
    {
        if (text.Length == 0)
            return;
        _value += text;
        if (_maxLines is { } maxLines)
        {
            var lines = _value.Split('\n');
            if (lines.Length > maxLines)
            {
                _value = string.Join('\n', lines[^maxLines..]);
                _dropped = true;
            }
        }
        var tail = TerminalText.Utf8Tail(_value, _maxBytes);
        _value = tail.Text;
        _dropped |= tail.Truncated;
    }

    public TerminalSendRead Consume()
    {
        var delta = _value;
        var truncated = _dropped;
        _value = "";
        _dropped = false;
        return new TerminalSendRead(delta, truncated);
    }

    public (string Text, bool Truncated) Snapshot()
        => (_value, _dropped);
}

internal sealed class PipeSendOperation : TerminalSendOperation
{
    private readonly BoundedTextBuffer _output;
    private readonly TaskCompletionSource<TerminalSendResult> _promise = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Action _onCancel;
    private bool _finished;
    private bool _cancellationRequested;

    public PipeSendOperation(int maxBytes, Action onCancel)
    {
        _output = new BoundedTextBuffer(maxBytes);
        _onCancel = onCancel;
    }

    public Task<TerminalSendResult> Done => _promise.Task;

    public bool Settled => _finished;

    public bool CancelRequested => _cancellationRequested;

    public void Append(string text)
    {
        if (!_finished)
            _output.Append(text);
    }

    public void Settle(TerminalWaitReason waitReason, TerminalSessionStatus sessionStatus, bool inheritedTruncation)
    {
        if (_finished)
            return;
        _finished = true;
        var read = _output.Snapshot();
        _promise.TrySetResult(new TerminalSendResult(read.Text, waitReason, sessionStatus, read.Truncated || inheritedTruncation));
    }

    public void Fail(Exception error)
    {
        if (_finished)
            return;
        _finished = true;
        _promise.TrySetException(error);
    }

    public TerminalSendRead ReadOutput() => _output.Consume();

    public bool Cancel()
    {
        if (_finished)
            return false;
        _cancellationRequested = true;
        _onCancel();
        return true;
    }
}

internal sealed class PipeTerminalSession : TerminalBackendSession
{
    private readonly SubprocessHandle _handle;
    private readonly ResolvedTerminalBashConfig _config;
    private readonly TerminalSanitizer _sanitizer;
    private readonly BoundedTextBuffer _scrollback;
    private readonly object _gate = new();
    private readonly Task _pumpTask;
    private TerminalSessionStatus _status = TerminalSessionStatus.Running();
    private PipeSendOperation? _active;
    private long _cursor;
    private bool _closing;
    private Task? _closeTask;
    private bool _promptSeen;
    private bool _promptTextSeen;
    private string _promptTail = "";
    private long _lastOutputAt;

    public PipeTerminalSession(SubprocessHandle handle, ResolvedTerminalBashConfig config)
    {
        _handle = handle;
        _config = config;
        _sanitizer = new TerminalSanitizer(config.MaxReadBytes);
        _scrollback = new BoundedTextBuffer(config.ScrollbackMaxBytes, config.ScrollbackLines);
        _lastOutputAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        _pumpTask = PumpOutputAsync();
        _ = ObserveExitAsync();
    }

    public string Motd { get; private set; } = "";

    public int? Pid => _handle.Pid;

    public async Task InitializeAsync(CancellationToken signal = default)
    {
        try
        {
            var operation = StartSend(new TerminalSendRequest("", false, signal));
            var result = await operation.Done;
            if (result.WaitReason == TerminalWaitReason.SessionExit)
                throw new InvalidOperationException("PTY shell exited during startup");
            if (result.WaitReason == TerminalWaitReason.Timeout)
                throw new InvalidOperationException("PTY shell did not reach readiness before startup timeout");
            Motd = result.Viewport;
        }
        catch
        {
            signal.ThrowIfCancellationRequested();
            throw;
        }
    }

    public TerminalSendOperation StartSend(TerminalSendRequest request)
    {
        lock (_gate)
        {
            if (_closing)
                throw new InvalidOperationException("PTY session is closing");
            if (_status.Kind == "exited")
                throw new InvalidOperationException("PTY session has exited");
            if (_active is not null)
                throw new TerminalError("PTY session already has an active send", TerminalErrorCodes.SendActive);
            request.Signal.ThrowIfCancellationRequested();
            PipeSendOperation? operation = null;
            operation = new PipeSendOperation(_config.MaxReadBytes, () => Interrupt(operation!));
            _active = operation;
            ResetReadiness();
            _ = RunSendAsync(operation, request);
            return operation;
        }
    }

    public TerminalReadResult Read(TerminalReadRequest request)
    {
        var snapshot = _scrollback.Snapshot();
        var lines = snapshot.Text.Length == 0 ? [] : snapshot.Text.Split('\n').ToList();
        var totalLines = snapshot.Text.Length == 0 ? 0 : lines.Count;
        var offset = request.Offset ?? 0;
        var count = request.Count ?? 500;
        if (offset < 0)
            throw new InvalidOperationException("PTY read offset must be a non-negative safe integer");
        if (count <= 0)
            throw new InvalidOperationException("PTY read count must be a positive safe integer");
        if (offset >= totalLines)
            return new TerminalReadResult("", totalLines, offset, offset, snapshot.Truncated);
        var end = totalLines - offset;
        var start = Math.Max(0, end - count);
        var requested = string.Join('\n', lines.Skip(start).Take(end - start));
        var bounded = TerminalText.Utf8Tail(requested, _config.MaxReadBytes);
        var returnedLines = bounded.Text.Length == 0 ? 0 : bounded.Text.Split('\n').Length;
        return new TerminalReadResult(
            bounded.Text,
            totalLines,
            offset,
            offset + returnedLines,
            snapshot.Truncated || bounded.Truncated);
    }

    public async Task<TerminalSignalResult> Signal(TerminalSignal signal)
    {
        lock (_gate)
        {
            if (_closing)
                throw new InvalidOperationException("PTY session is closing");
        }
        if (signal is TerminalSignal.SIGKILL or TerminalSignal.SIGTERM)
            _handle.Terminate();
        return new TerminalSignalResult(true, _handle.Pid);
    }

    public TerminalSessionStatus Status()
    {
        lock (_gate)
            return _status;
    }

    public Task Close(string reason)
    {
        lock (_gate)
        {
            if (_closing)
                return _closeTask ?? Task.CompletedTask;
            _closing = true;
            _closeTask = CloseOnceAsync(reason);
            return _closeTask;
        }
    }

    private async Task CloseOnceAsync(string reason)
    {
        _handle.Terminate();
        try
        {
            await _handle.Done;
            await _pumpTask;
        }
        catch (Exception error)
        {
            throw new InvalidOperationException($"PTY cleanup failed ({reason})", error);
        }
        lock (_gate)
        {
            _status = TerminalSessionStatus.Exited(null, "SIGKILL");
            _active?.Settle(TerminalWaitReason.SessionExit, _status, _scrollback.Snapshot().Truncated);
            _active = null;
        }
    }

    private async Task PumpOutputAsync()
    {
        while (!_handle.Done.IsCompleted)
        {
            DrainOutput();
            await Task.Delay(_config.PollIntervalMs);
        }
        DrainOutput();
        lock (_gate)
        {
            var tail = _sanitizer.Flush();
            if (tail.Length > 0)
                AppendOutputUnlocked(tail);
        }
    }

    private void DrainOutput()
    {
        var read = _handle.StdoutReader.ReadFrom(_cursor);
        _cursor = read.NextOffset;
        if (read.Text.Length == 0)
            return;
        var sanitized = _sanitizer.Push(read.Text);
        lock (_gate)
        {
            AppendOutputUnlocked(sanitized.Text);
            if (sanitized.Prompt)
            {
                _promptSeen = true;
                _promptTail = "";
                _lastOutputAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            }
            if (sanitized.PromptTail is { } tail)
            {
                var remaining = Math.Max(0, TerminalPrompt.ControlledPrompt.Length + 1 - _promptTail.Length);
                _promptTail += tail.Length <= remaining ? tail : tail[..remaining];
                if (tail.Length > remaining)
                    _promptTail = $"{TerminalPrompt.ControlledPrompt}\0";
                _promptTextSeen = _promptTail == TerminalPrompt.ControlledPrompt;
            }
        }
    }

    private void AppendOutputUnlocked(string text)
    {
        if (text.Length == 0)
            return;
        _lastOutputAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        _scrollback.Append(text);
        _active?.Append(text);
    }

    private void ResetReadiness()
    {
        _lastOutputAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        _promptSeen = false;
        _promptTextSeen = false;
        _promptTail = "";
    }

    private async Task RunSendAsync(PipeSendOperation operation, TerminalSendRequest request)
    {
        try
        {
            var input = $"{request.Text}{(request.Submit ? "\n" : "")}";
            if (input.Length > 0)
                await _handle.StandardInput.WriteAsync(input.AsMemory());
            var deadline = DateTimeOffset.UtcNow.AddMilliseconds(_config.TimeoutMs).ToUnixTimeMilliseconds();
            while (!operation.Settled)
            {
                if (_closing)
                    break;
                if (_status.Kind == "exited")
                {
                    SettleActive(operation, TerminalWaitReason.SessionExit);
                    return;
                }
                if (request.Signal.IsCancellationRequested)
                    operation.Cancel();
                if (DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() >= deadline)
                {
                    SettleActive(operation, TerminalWaitReason.Timeout);
                    return;
                }
                long idleFor;
                bool promptReady;
                lock (_gate)
                {
                    idleFor = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - _lastOutputAt;
                    promptReady = _promptSeen && _promptTextSeen && idleFor >= _config.PollIntervalMs;
                }
                if (promptReady)
                {
                    SettleActive(operation, TerminalWaitReason.StdinRead);
                    return;
                }
                if (idleFor >= _config.IdleSilenceMs + (_promptSeen ? _config.HandoffGraceMs : 0))
                {
                    SettleActive(operation, TerminalWaitReason.InferredIdle);
                    return;
                }
                await Task.Delay(_config.PollIntervalMs);
            }
        }
        catch (Exception error)
        {
            FailActive(operation, error);
        }
    }

    private void Interrupt(PipeSendOperation operation)
    {
        lock (_gate)
        {
            if (_active != operation || _closing)
                return;
        }
        _handle.Terminate();
    }

    private void SettleActive(PipeSendOperation operation, TerminalWaitReason reason)
    {
        lock (_gate)
        {
            if (_active != operation)
                return;
            _active = null;
            operation.Settle(reason, _status, _scrollback.Snapshot().Truncated);
        }
    }

    private void FailActive(PipeSendOperation operation, Exception error)
    {
        lock (_gate)
        {
            if (_active != operation)
                return;
            _active = null;
            operation.Fail(error);
        }
    }

    private async Task ObserveExitAsync()
    {
        var outcome = await _handle.Done;
        await _pumpTask;
        lock (_gate)
        {
            _status = TerminalSessionStatus.Exited(outcome.ExitCode, outcome.Signal);
            _active?.Settle(TerminalWaitReason.SessionExit, _status, _scrollback.Snapshot().Truncated);
            _active = null;
        }
    }
}
