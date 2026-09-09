using Microsoft.Win32.SafeHandles;

namespace Dsh.Pty;

internal sealed class UnixPtySession : IDisposable
{
    private readonly int _pid;
    private readonly SafeFileHandle _handle;
    private readonly FileStream _stream;
    private readonly CancellationTokenSource _cts = new();
    private readonly object _gate = new();
    private Task? _monitorTask;
    private bool _stopping;
    private bool _disposed;

    public UnixPtySession(int pid, SafeFileHandle handle, FileStream stream)
    {
        _pid = pid;
        _handle = handle;
        _stream = stream;
    }

    public event Action<int?>? Exited;

    public int Pid => _pid;

    public FileStream Stream => _stream;

    public static UnixPtySession Start(PtyStartInfo info)
    {
        if (OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Unix PTY backend is not available on Windows");

        var (master, pid) = UnixPtyNative.Spawn(info);
        var handle = new SafeFileHandle((IntPtr)master, ownsHandle: true);
        var stream = new FileStream(handle, FileAccess.ReadWrite, 4096, isAsync: false);
        var session = new UnixPtySession(pid, handle, stream);
        session._monitorTask = session.MonitorAsync();
        return session;
    }

    public void Resize(int rows, int cols)
    {
        ThrowIfDisposed();
        UnixPtyNative.Resize(_handle, rows, cols);
    }

    public async Task StopAsync()
    {
        lock (_gate)
        {
            if (_stopping)
                return;
            _stopping = true;
        }

        UnixPtyNative.Terminate(_pid);
        var monitor = _monitorTask;
        if (monitor is not null)
        {
            var completed = await Task.WhenAny(monitor, Task.Delay(TimeSpan.FromSeconds(2)));
            if (completed != monitor)
            {
                UnixPtyNative.Kill(_pid);
                await monitor;
            }
        }

        Dispose();
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
                return;
            _disposed = true;
        }

        _cts.Cancel();
        UnixPtyNative.Kill(_pid);
        _stream.Dispose();
        _handle.Dispose();
        _monitorTask = null;
    }

    private async Task MonitorAsync()
    {
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                if (UnixPtyNative.TryWait(_pid, out var exitCode))
                {
                    Exited?.Invoke(exitCode);
                    return;
                }

                await Task.Delay(50, _cts.Token);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void ThrowIfDisposed()
    {
        lock (_gate)
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(UnixPtySession));
        }
    }
}
