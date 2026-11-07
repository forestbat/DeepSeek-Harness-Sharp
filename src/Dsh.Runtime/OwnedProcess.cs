namespace Dsh.Runtime;

/// <summary>Owns a child process, including descendants and its redirected, otherwise unmodified standard streams.</summary>
public sealed class OwnedProcess : IAsyncDisposable
{
    private readonly IChildProcessLifetime _lifetime;
    private readonly CancellationTokenRegistration _cancellation;
    private readonly Lock _disposeGate = new();
    private Task? _dispose;

    public OwnedProcess(IChildProcessLifetime lifetime, CancellationToken cancellationToken)
    {
        _lifetime = lifetime;
        _cancellation = cancellationToken.UnsafeRegister(static state => ((IChildProcessLifetime)state!).Kill(), lifetime);
    }

    /// <summary>The owned launch PID, which can identify a supervisor rather than the payload.</summary>
    public int Id => _lifetime.Id;
    public Stream StandardInput => _lifetime.StandardInput;
    public Stream StandardOutput => _lifetime.StandardOutput;
    public Stream StandardError => _lifetime.StandardError;

    /// <summary>Waits for root exit and owned descendants to terminate. Keeps streams open for draining. Cancellation terminates the process.</summary>
    public async Task<int> WaitForExitAsync(CancellationToken cancellationToken = default)
    {
        using var registration = cancellationToken.UnsafeRegister(static state => ((IChildProcessLifetime)state!).Kill(), _lifetime);
        var exitCode = await _lifetime.Exit.ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        return exitCode;
    }

    public void Kill() => _lifetime.Kill();

    public ValueTask DisposeAsync()
    {
        lock (_disposeGate)
            return new ValueTask(_dispose ??= DisposeCoreAsync());
    }

    private async Task DisposeCoreAsync()
    {
        await _cancellation.DisposeAsync().ConfigureAwait(false);
        await _lifetime.DisposeAsync().ConfigureAwait(false);
    }
}
