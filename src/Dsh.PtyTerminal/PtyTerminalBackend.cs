using System.Text;
using Dsh.Pty;
using Dsh.Runtime;

namespace Dsh.PtyTerminal;

/** 真 PTY 终端后端: 以 Dsh.Pty 的 PtyHost/PtySession 承载交互 shell, 取代旧的 bash+管道实现。 */
public sealed class PtyTerminalBackend(ResolvedPtyTerminalConfig config) : TerminalBackend
{
    public const string BackendType = "shell";

    public string Type => BackendType;

    public async Task<TerminalBackendSession> Spawn(TerminalBackendSpawnSpec spec)
    {
        spec.Signal.ThrowIfCancellationRequested();
        var cwd = spec.Cwd ?? spec.Owner.Session.Header.Cwd ?? Environment.CurrentDirectory;
        var startInfo = new PtyStartInfo
        {
            FileName = config.Command,
            Arguments = config.Args,
            WorkingDirectory = cwd,
            Rows = config.Rows,
            Columns = config.Cols,
            Environment = ChildEnvironment(spec),
        };
        var session = await PtyHost.Default.StartAsync(startInfo, id: null, cancellationToken: spec.Signal);
        try
        {
            return new PtyTerminalSession(session, config);
        }
        catch (Exception error)
        {
            try
            {
                await StopPtyAsync(session);
            }
            catch (Exception closeError)
            {
                throw new TerminalBackendCleanupError(error, closeError);
            }
            throw;
        }
    }

    internal static async Task StopPtyAsync(PtySession session)
    {
        try
        {
            await PtyHost.Default.StopAsync(session.Id.Value);
        }
        finally
        {
            session.Dispose();
        }
    }

    private static IReadOnlyDictionary<string, string?> ChildEnvironment(TerminalBackendSpawnSpec spec)
        => new Dictionary<string, string?>
        {
            ["TERM"] = "xterm-256color",
            ["PAGER"] = "cat",
            ["GIT_PAGER"] = "cat",
            ["NO_COLOR"] = "1",
            ["DSH_SHELL"] = "1",
            ["DSH_SESSION_ID"] = spec.Owner.Id.Value,
            ["DSH_PTY_SESSION_ID"] = spec.SessionId.Value,
        };
}

/** 一个真 PTY 会话: 输出泵把 PTY 字节流经 UTF-8 解码与终端清洗后喂进有界滚动缓冲与当前发送操作。 */
internal sealed class PtyTerminalSession : TerminalBackendSession
{
    private static readonly string SubmitNewLine = OperatingSystem.IsWindows() ? "\r" : "\n";

    private readonly PtySession _session;
    private readonly ResolvedPtyTerminalConfig _config;
    private readonly TerminalSanitizer _sanitizer;
    private readonly TerminalOutputBuffer _scrollback;
    private readonly Decoder _decoder = Encoding.UTF8.GetDecoder();
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly object _gate = new();
    private readonly Task _pumpTask;
    private BufferedSendOperation? _active;
    private TerminalSessionStatus _status = TerminalSessionStatus.Running();
    private long _lastOutputAt;
    private bool _closing;
    private Task? _closeTask;

    public PtyTerminalSession(PtySession session, ResolvedPtyTerminalConfig config)
    {
        _session = session;
        _config = config;
        _sanitizer = new TerminalSanitizer(config.MaxReadBytes);
        _scrollback = new TerminalOutputBuffer(config.ScrollbackMaxBytes, config.ScrollbackLines);
        _lastOutputAt = Now();
        Motd = $"real PTY session over {config.Command} (pid {session.ProcessId?.ToString() ?? "unknown"}); ConPTY/openpty master, not a pipe";
        _pumpTask = PumpOutputAsync();
    }

    public string Motd { get; }

    public int? Pid => _session.ProcessId;

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
            var operation = new BufferedSendOperation(_config.MaxReadBytes, Interrupt);
            _active = operation;
            _lastOutputAt = Now();
            _ = RunSendAsync(operation, request);
            return operation;
        }
    }

    public TerminalReadResult Read(TerminalReadRequest request)
    {
        var offset = request.Offset ?? 0;
        var count = request.Count ?? 500;
        if (offset < 0)
            throw new InvalidOperationException("PTY read offset must be a non-negative safe integer");
        if (count <= 0)
            throw new InvalidOperationException("PTY read count must be a positive safe integer");
        return _scrollback.Read(offset, count, _config.MaxReadBytes);
    }

    public async Task<TerminalSignalResult> Signal(TerminalSignal signal)
    {
        lock (_gate)
        {
            if (_closing)
                throw new InvalidOperationException("PTY session is closing");
        }
        switch (signal)
        {
            case TerminalSignal.SIGINT:
                await WriteBytesAsync(new byte[] { 0x03 });
                break;
            case TerminalSignal.SIGTSTP:
                await WriteBytesAsync(new byte[] { 0x1a });
                break;
            case TerminalSignal.SIGTERM:
            case TerminalSignal.SIGKILL:
            case TerminalSignal.SIGHUP:
                await PtyTerminalBackend.StopPtyAsync(_session);
                MarkExited(TerminalSignalNames.Of(signal));
                break;
        }
        return new TerminalSignalResult(true, _session.ProcessId ?? 0);
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
        await PtyTerminalBackend.StopPtyAsync(_session);
        try
        {
            await _pumpTask.WaitAsync(TimeSpan.FromMilliseconds(_config.DisposeGraceMs));
        }
        catch (Exception error)
        {
            _ = error;
        }
        MarkExited(reason);
    }

    private void MarkExited(string? signal)
    {
        lock (_gate)
        {
            _status = TerminalSessionStatus.Exited(_session.ExitCode, signal);
            _active?.Settle(TerminalWaitReason.SessionExit, _status, _scrollback.Truncated);
            _active = null;
        }
    }

    private async Task PumpOutputAsync()
    {
        var buffer = new byte[8192];
        try
        {
            while (true)
            {
                int read;
                try
                {
                    read = await _session.ReadAsync(buffer);
                }
                catch (ObjectDisposedException)
                {
                    break;
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (IOException)
                {
                    break;
                }
                if (read == 0)
                    break;
                AppendSanitized(Decode(buffer.AsSpan(0, read), flush: false));
            }
        }
        finally
        {
            AppendSanitized(Decode([], flush: true));
            var tail = _sanitizer.Flush();
            if (tail.Length > 0)
                AppendOutput(tail);
            lock (_gate)
            {
                _status = ResolveStatus();
                _active?.Settle(TerminalWaitReason.SessionExit, _status, _scrollback.Truncated);
                _active = null;
            }
        }
    }

    private void AppendSanitized(string text)
    {
        if (text.Length == 0)
            return;
        var sanitized = _sanitizer.Push(text);
        if (sanitized.Text.Length > 0)
            AppendOutput(sanitized.Text);
    }

    private string Decode(ReadOnlySpan<byte> bytes, bool flush)
    {
        var chars = new char[bytes.Length + 4];
        var count = _decoder.GetChars(bytes, chars, flush);
        return count == 0 ? "" : new string(chars, 0, count);
    }

    private void AppendOutput(string text)
    {
        lock (_gate)
        {
            _lastOutputAt = Now();
            _scrollback.Append(text);
            _active?.Append(text);
        }
    }

    private async Task RunSendAsync(BufferedSendOperation operation, TerminalSendRequest request)
    {
        try
        {
            var input = $"{request.Text}{(request.Submit ? SubmitNewLine : "")}";
            if (input.Length > 0)
                await WriteBytesAsync(Encoding.UTF8.GetBytes(input), request.Signal);
            var deadline = Now() + _config.TimeoutMs;
            while (!operation.Settled)
            {
                if (IsClosing())
                    break;
                if (Status().Kind == "exited")
                {
                    SettleActive(operation, TerminalWaitReason.SessionExit);
                    return;
                }
                if (request.Signal.IsCancellationRequested)
                    operation.Cancel();
                if (Now() >= deadline)
                {
                    SettleActive(operation, TerminalWaitReason.Timeout);
                    return;
                }
                long idleFor;
                lock (_gate)
                    idleFor = Now() - _lastOutputAt;
                if (idleFor >= _config.IdleSilenceMs)
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

    private void Interrupt(BufferedSendOperation operation)
    {
        lock (_gate)
        {
            if (_active != operation || _closing)
                return;
        }
        _ = WriteInterruptAsync();
    }

    private async Task WriteInterruptAsync()
    {
        try
        {
            await WriteBytesAsync(new byte[] { 0x03 });
        }
        catch (Exception error)
        {
            _ = error;
        }
    }

    private async ValueTask WriteBytesAsync(ReadOnlyMemory<byte> bytes, CancellationToken signal = default)
    {
        await _writeGate.WaitAsync(signal);
        try
        {
            await _session.WriteAsync(bytes, signal);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    private void SettleActive(BufferedSendOperation operation, TerminalWaitReason reason)
    {
        lock (_gate)
        {
            if (_active != operation)
                return;
            _active = null;
            operation.Settle(reason, _status, _scrollback.Truncated);
        }
    }

    private void FailActive(BufferedSendOperation operation, Exception error)
    {
        lock (_gate)
        {
            if (_active != operation)
                return;
            _active = null;
            operation.Fail(error);
        }
    }

    private bool IsClosing()
    {
        lock (_gate)
            return _closing;
    }

    private TerminalSessionStatus ResolveStatus()
        => _session.Status == PtySessionStatus.Exited
            ? TerminalSessionStatus.Exited(_session.ExitCode, null)
            : TerminalSessionStatus.Running();

    private static long Now() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
}

public static class PtyTerminalPlugin
{
    public const string PluginName = "pty-terminal";

    public static IDisposable Register(Context ctx, PtyTerminalConfig? config = null)
    {
        var terminals = ctx.Get<TerminalSessionService>(TerminalSessionService.ServiceName)
            ?? throw new InvalidOperationException("pty-terminal requires the terminals service");
        return RegisterBackend(terminals, config);
    }

    public static IDisposable RegisterBackend(TerminalSessionService terminals, PtyTerminalConfig? config = null)
    {
        var resolved = PtyTerminalConfigResolver.Resolve(config);
        PtyTerminalConfigResolver.Validate(resolved);
        var backend = new PtyTerminalBackend(resolved);
        return new BackendRegistration(terminals.RegisterBackend(backend));
    }

    private sealed class BackendRegistration(Action unregister) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            unregister();
        }
    }
}
