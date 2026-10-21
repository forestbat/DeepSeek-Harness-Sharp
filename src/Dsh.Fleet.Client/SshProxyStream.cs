using System.Buffers;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Dsh.Runtime;

namespace Dsh.Fleet;

/// <summary>Owns one authenticated SSH process independently of the streams using it.</summary>
internal sealed class SshProxyProcess : IAsyncDisposable
{
    private const int DiagnosticCapacity = 8192;
    private readonly object _gate = new();
    private readonly bool _persistent;
    private readonly CancellationTokenSource _stop = new();
    private readonly CancellationTokenSource _failure = new();
    private readonly TaskCompletionSource _authenticated = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _exited = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<int> _exitClassified = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Task _diagnostics = Task.CompletedTask;
    private Task _observing = Task.CompletedTask;
    private Task? _disposal;
    private string? _askPass;
    private string? _authenticationMarker;
    private string _failureDetail = "The SSH proxy transport failed.";
    private bool _started;
    private bool _disposed;
    private bool _discardDiagnosticLine;
    private bool _askPassRemovalFailed;
    private bool _resourcesReleased;

    internal SshProxyProcess(bool persistent)
    {
        _persistent = persistent;
        Process = new Process { EnableRaisingEvents = true };
        Process.Exited += OnExited;
        FailureToken = _failure.Token;
    }

    internal Process Process { get; }
    internal CancellationToken FailureToken { get; }
    internal Stream Input => Process.StandardInput.BaseStream;
    internal Stream Output => Process.StandardOutput.BaseStream;
    internal Task<int> ExitClassified => _exitClassified.Task;

    internal async Task StartAsync(MachineSpec machine, DnsEndPoint endpoint, int? localPort, CancellationToken cancellationToken)
    {
        try
        {
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                cancellationToken.ThrowIfCancellationRequested();
                var setup = SshTransport.CreateProxyStart(machine, endpoint, localPort);
                Process.StartInfo = setup.StartInfo;
                _askPass = setup.AskPass;
                _authenticationMarker = setup.AuthenticationMarker;
                if (!Process.Start())
                    throw new IOException("Failed to start the SSH proxy transport.");
                _started = true;
                _diagnostics = DrainDiagnosticsAsync(Process.StandardError);
                _observing = ObserveExitAsync();
                if (Process.HasExited)
                    _exited.TrySetResult();
                machine = null!;
            }
            using var registration = cancellationToken.UnsafeRegister(static state => ((SshProxyProcess)state!).Dispose(), this);
            await _authenticated.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            ThrowIfFailed();
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed), this);
        }
        catch
        {
            if (!cancellationToken.IsCancellationRequested)
                SignalFailure();
            Dispose();
            throw;
        }
        finally
        {
            machine = null!;
        }
    }

    private void OnExited(object? sender, EventArgs args)
    {
        lock (_gate)
        {
            if (_started && !_exitClassified.Task.IsCompleted)
                ClassifyExit();
            _exited.TrySetResult();
        }
    }

    private async Task ObserveExitAsync()
    {
        try
        {
            await _exited.Task.ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
            lock (_gate)
            {
                if (!_exitClassified.Task.IsCompleted)
                    ClassifyExit();
            }
            await _exitClassified.Task.ConfigureAwait(false);
        }
        finally
        {
            await _closed.Task.ConfigureAwait(false);
            await _diagnostics.ConfigureAwait(false);
        }
    }

    private void ClassifyExit()
    {
        try
        {
            var exitCode = Process.ExitCode;
            // A persistent forward has no successful unsolicited completion, including exit 0.
            if (exitCode != 0 || _persistent)
                SignalFailure();
            else if (!_authenticated.Task.IsCompleted)
                _authenticated.TrySetException(new IOException("SSH ended before authentication completed."));
            _exitClassified.TrySetResult(exitCode);
        }
        catch (Exception error)
        {
            // Exit classification also carries cleanup errors to asynchronous disposal.
            _exitClassified.TrySetException(error);
        }
    }

    internal void ThrowIfFailed(bool observeExit = false)
    {
        if (observeExit)
        {
            lock (_gate)
            {
                if (_started && !_disposed && Process.HasExited)
                    ClassifyExit();
            }
        }
        if (FailureToken.IsCancellationRequested)
            throw new IOException(_failureDetail);
    }

    private void SignalFailure()
    {
        lock (_gate)
        {
            if (_disposed)
                return;
            _authenticated.TrySetException(new IOException(_failureDetail));
            try
            {
                _failure.Cancel();
            }
            finally
            {
                Dispose();
            }
        }
    }

    private async Task DrainDiagnosticsAsync(StreamReader reader)
    {
        var buffer = ArrayPool<char>.Shared.Rent(DiagnosticCapacity);
        var used = 0;
        try
        {
            while (await reader.ReadAsync(buffer.AsMemory(used, DiagnosticCapacity - used), _stop.Token).ConfigureAwait(false) is var read && read != 0)
                used = ConsumeDiagnostics(buffer, used + read);
            if (used != 0 && !_discardDiagnosticLine)
                ObserveDiagnosticLine(buffer.AsSpan(0, used));
        }
        catch (Exception error) when (error is IOException or ObjectDisposedException or OperationCanceledException)
        {
            if (!Volatile.Read(ref _disposed))
            {
                _failureDetail = "The SSH diagnostic stream failed.";
                SignalFailure();
            }
        }
        finally
        {
            ArrayPool<char>.Shared.Return(buffer, clearArray: true);
        }
    }

    private int ConsumeDiagnostics(char[] buffer, int count)
    {
        var consumed = 0;
        for (var index = 0; index < count; index++)
        {
            if (buffer[index] != '\n')
                continue;
            if (!_discardDiagnosticLine)
                ObserveDiagnosticLine(buffer.AsSpan(consumed, index - consumed));
            _discardDiagnosticLine = false;
            consumed = index + 1;
        }
        var remaining = count - consumed;
        if (remaining == DiagnosticCapacity)
        {
            _discardDiagnosticLine = true;
            return 0;
        }
        buffer.AsSpan(consumed, remaining).CopyTo(buffer);
        return remaining;
    }

    private void ObserveDiagnosticLine(ReadOnlySpan<char> line)
    {
        if (_authenticationMarker is not null && line.Trim().Equals(_authenticationMarker, StringComparison.Ordinal))
        {
            // ssh_session2 sets up local forwards before it runs this target-only LocalCommand.
            DeleteAskPass();
            _authenticated.TrySetResult();
        }
        else if (line.Contains("Permission denied", StringComparison.OrdinalIgnoreCase))
            _failureDetail = "SSH authentication failed.";
        else if (line.Contains("open failed", StringComparison.OrdinalIgnoreCase))
            _failureDetail = "SSH could not connect to the remote proxy endpoint.";
        else if (line.Contains("forwarding failed", StringComparison.OrdinalIgnoreCase)
            || line.Contains("Address already in use", StringComparison.OrdinalIgnoreCase))
            _failureDetail = "SSH could not bind the local proxy forwarding port.";
    }

    private void DeleteAskPass(bool retryCleanup = false)
    {
        lock (_gate)
        {
            if (_askPass is not { } path)
                return;
            if (_askPassRemovalFailed && !retryCleanup)
                throw new IOException("The SSH authentication secret could not be removed.");
            try
            {
                File.Delete(path);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                _askPassRemovalFailed = true;
                // Retain ownership if removal fails; never expose a secret path.
                throw new IOException("The SSH authentication secret could not be removed.");
            }
            _askPass = null;
            _askPassRemovalFailed = false;
        }
    }

    internal void Dispose(bool retryCleanup = false)
    {
        lock (_gate)
        {
            if (_disposed)
            {
                DeleteAskPass(retryCleanup);
                return;
            }
            _disposed = true;
            try
            {
                _stop.Cancel();
                if (_started && !Process.HasExited)
                {
                    try
                    {
                        Process.Kill(entireProcessTree: true);
                    }
                    catch (InvalidOperationException)
                    {
                        // The exit event may win the race with this normal shutdown.
                    }
                }
            }
            finally
            {
                try
                {
                    if (_started)
                    {
                        Input.Dispose();
                        Output.Dispose();
                    }
                }
                finally
                {
                    try
                    {
                        DeleteAskPass(retryCleanup);
                    }
                    finally
                    {
                        _authenticated.TrySetCanceled();
                        _closed.TrySetResult();
                    }
                }
            }
        }
    }

    public ValueTask DisposeAsync() => ReleaseAsync(retryCleanup: true);

    internal ValueTask ReleaseAsync(bool retryCleanup)
    {
        lock (_gate)
        {
            if (_disposal is null)
                _disposal = ReleaseProcessAsync(retryCleanup);
            else if (retryCleanup && !_disposal.IsCompletedSuccessfully)
                _disposal = RetryProcessReleaseAsync(_disposal);
            return new ValueTask(_disposal);
        }
    }

    private async Task RetryProcessReleaseAsync(Task previous)
    {
        try
        {
            await previous.ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
        }
        catch (Exception) when (previous.IsFaulted || previous.IsCanceled)
        {
            // Only a new explicit disposal requests this retry after the prior attempt has settled.
            await ReleaseProcessAsync(retryCleanup: true).ConfigureAwait(false);
        }
    }

    private async Task ReleaseProcessAsync(bool retryCleanup)
    {
        try
        {
            Dispose(retryCleanup);
        }
        finally
        {
            if (!_resourcesReleased)
            {
                try
                {
                    await _observing.ConfigureAwait(false);
                }
                finally
                {
                    lock (_gate)
                    {
                        Process.Exited -= OnExited;
                        Process.Dispose();
                        _failure.Dispose();
                        _stop.Dispose();
                        _resourcesReleased = true;
                    }
                }
            }
        }
    }
}

/// <summary>Owns one command stream or one loopback connection to a shared SSH forward.</summary>
internal sealed class SshProxyStream : Stream, IOutboundTransportLifetime
{
    private readonly object _gate = new();
    private readonly SshProxyProcess _owner;
    private readonly bool _ownsProcess;
    private readonly Action<SshProxyStream> _released;
    private readonly TaskCompletionSource _closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Task _disposal;
    private CancellationTokenRegistration _failureRegistration;
    private Stream? _input;
    private Stream? _output;
    private Socket? _connectingSocket;
    private bool _disposed;

    internal SshProxyStream(SshProxyProcess owner, bool ownsProcess, Action<SshProxyStream> released)
    {
        _owner = owner;
        _ownsProcess = ownsProcess;
        _released = released;
        _disposal = FinishDisposalAsync();
    }

    internal Process Process => _owner.Process;
    public CancellationToken FailureToken => _owner.FailureToken;
    public object FailureOwner => _owner;

    internal async Task StartAsync(MachineSpec machine, DnsEndPoint endpoint, CancellationToken cancellationToken)
    {
        var starting = _owner.StartAsync(machine, endpoint, null, cancellationToken);
        machine = null!;
        await starting.ConfigureAwait(false);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _input = _owner.Input;
            _output = _owner.Output;
        }
        RegisterFailure();
        _owner.ThrowIfFailed();
    }

    internal async Task ConnectLoopbackAsync(int localPort, CancellationToken cancellationToken)
    {
        var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        try
        {
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                _connectingSocket = socket;
            }
            RegisterFailure();
            _owner.ThrowIfFailed();
            using var registration = cancellationToken.UnsafeRegister(static state => ((SshProxyStream)state!).Close(retryCleanup: false), this);
            await socket.ConnectAsync(IPAddress.Loopback, localPort, cancellationToken).ConfigureAwait(false);
            _owner.ThrowIfFailed();
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                cancellationToken.ThrowIfCancellationRequested();
                _input = _output = new NetworkStream(socket, ownsSocket: true);
                _connectingSocket = null;
            }
        }
        catch
        {
            lock (_gate)
            {
                if (ReferenceEquals(_connectingSocket, socket))
                    _connectingSocket = null;
            }
            socket.Dispose();
            throw;
        }
    }

    private void RegisterFailure()
    {
        var registration = FailureToken.UnsafeRegister(static state => ((SshProxyStream)state!).Close(retryCleanup: false), this);
        lock (_gate)
        {
            if (_disposed)
                registration.Unregister();
            else
                _failureRegistration = registration;
        }
    }

    private void CloseInputAtEnd()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            // -W may keep SSH alive after remote output EOF until local input also ends.
            _input!.Dispose();
        }
    }

    private void CheckEnd(int exitCode)
    {
        if (exitCode != 0)
            throw new IOException("The SSH proxy transport ended unexpectedly.");
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed), this);
    }

    public override bool CanRead => !Volatile.Read(ref _disposed) && _output is not null;
    public override bool CanWrite => !Volatile.Read(ref _disposed) && _input is not null;
    public override bool CanSeek => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() => _input!.Flush();
    public override Task FlushAsync(CancellationToken cancellationToken) => _input!.FlushAsync(cancellationToken);
    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed), this);
        if (buffer.Length == 0)
            return 0;
        var count = _output!.Read(buffer);
        if (count == 0 && _ownsProcess)
        {
            CloseInputAtEnd();
            CheckEnd(_owner.ExitClassified.GetAwaiter().GetResult());
        }
        else
            _owner.ThrowIfFailed(observeExit: count == 0);
        return count;
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed), this);
        cancellationToken.ThrowIfCancellationRequested();
        if (buffer.Length == 0)
            return 0;
        using var registration = cancellationToken.UnsafeRegister(static state => ((SshProxyStream)state!).Close(retryCleanup: false), this);
        var count = await _output!.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
        if (count == 0 && _ownsProcess)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CloseInputAtEnd();
            var exitCode = await _owner.ExitClassified.WaitAsync(cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            CheckEnd(exitCode);
        }
        else
            _owner.ThrowIfFailed(observeExit: count == 0);
        return count;
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed), this);
        _input!.Write(buffer);
    }

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed), this);
        using var registration = cancellationToken.UnsafeRegister(static state => ((SshProxyStream)state!).Close(retryCleanup: false), this);
        await _input!.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
    }

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        => WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            Close(retryCleanup: true);
        base.Dispose(disposing);
    }

    private void Close(bool retryCleanup)
    {
        Socket? connectingSocket;
        bool alreadyDisposed;
        lock (_gate)
        {
            alreadyDisposed = _disposed;
            _disposed = true;
            connectingSocket = _connectingSocket;
            _connectingSocket = null;
        }
        if (alreadyDisposed)
        {
            if (_ownsProcess && retryCleanup)
                _owner.Dispose(retryCleanup: true);
            RetryDisposal(retryCleanup);
            return;
        }
        try
        {
            _failureRegistration.Unregister();
            connectingSocket?.Dispose();
            try
            {
                _input?.Dispose();
                if (!ReferenceEquals(_input, _output))
                    _output?.Dispose();
            }
            finally
            {
                if (_ownsProcess)
                    _owner.Dispose(retryCleanup);
            }
        }
        finally
        {
            _closed.TrySetResult();
        }
    }

    private async Task FinishDisposalAsync(bool retryCleanup = false)
    {
        // Even a retry must not acquire the process/tunnel locks while holding the stream lock.
        await _closed.Task.ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
        if (_ownsProcess)
            await _owner.ReleaseAsync(retryCleanup).ConfigureAwait(false);
        _released(this);
    }

    private Task RetryDisposal(bool retryCleanup)
    {
        lock (_gate)
        {
            if (retryCleanup && !_disposal.IsCompletedSuccessfully)
                _disposal = RetryStreamReleaseAsync(_disposal);
            return _disposal;
        }
    }

    private async Task RetryStreamReleaseAsync(Task previous)
    {
        try
        {
            await previous.ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
        }
        catch (Exception) when (previous.IsFaulted || previous.IsCanceled)
        {
            await FinishDisposalAsync(retryCleanup: true).ConfigureAwait(false);
        }
    }

    public override ValueTask DisposeAsync() => ReleaseAsync(retryCleanup: true);

    internal async ValueTask ReleaseAsync(bool retryCleanup)
    {
        try
        {
            Close(retryCleanup);
        }
        finally
        {
            await RetryDisposal(retryCleanup: false).ConfigureAwait(false);
            GC.SuppressFinalize(this);
        }
    }
}
