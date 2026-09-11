using System.Diagnostics;

namespace Dsh.Pty;

public sealed class PtySession : IDisposable
{
    private readonly PtyStartInfo _startInfo;
    private readonly object _gate = new();
    private UnixPtySession? _unix;
    private Process? _process;
    private bool _attached;
    private bool _disposed;
    private PtySessionStatus _status = PtySessionStatus.Running;
    private int? _exitCode;

    internal PtySession(PtySessionId id, PtyStartInfo startInfo)
    {
        Id = id;
        _startInfo = startInfo;
        Command = startInfo.Command;
        StartedAt = DateTimeOffset.UtcNow;
    }

    public PtySessionId Id { get; }

    public string Command { get; }

    public DateTimeOffset StartedAt { get; }

    public int? ProcessId => _unix?.Pid ?? _process?.Id;

    public PtySessionStatus Status
    {
        get
        {
            lock (_gate)
                return _status;
        }
    }

    public int? ExitCode
    {
        get
        {
            lock (_gate)
                return _exitCode;
        }
    }

    public bool IsAttached
    {
        get
        {
            lock (_gate)
                return _attached;
        }
    }

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (OperatingSystem.IsWindows())
        {
            _process = StartWindowsProcess(_startInfo);
            return;
        }

        _unix = UnixPtySession.Start(_startInfo);
        _unix.Exited += OnUnixExited;
    }

    public async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        var unix = _unix;
        if (unix is not null)
            return await unix.Stream.ReadAsync(buffer, cancellationToken);

        var process = _process;
        if (process is not null)
            return await process.StandardOutput.BaseStream.ReadAsync(buffer, cancellationToken);

        throw new ObjectDisposedException(nameof(PtySession));
    }

    public async ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
    {
        var unix = _unix;
        if (unix is not null)
        {
            await unix.Stream.WriteAsync(data, cancellationToken);
            return;
        }

        var process = _process;
        if (process is not null)
        {
            await process.StandardInput.BaseStream.WriteAsync(data, cancellationToken);
            return;
        }

        throw new ObjectDisposedException(nameof(PtySession));
    }

    public void Resize(int rows, int columns)
    {
        var unix = _unix;
        if (unix is not null)
        {
            unix.Resize(rows, columns);
            return;
        }

        if (_process is not null)
            return;

        throw new ObjectDisposedException(nameof(PtySession));
    }

    public void Attach()
    {
        lock (_gate)
            _attached = true;
    }

    public void Detach()
    {
        lock (_gate)
            _attached = false;
    }

    public async Task StopAsync()
    {
        var unix = _unix;
        if (unix is not null)
        {
            unix.Exited -= OnUnixExited;
            await unix.StopAsync();
            lock (_gate)
            {
                _status = PtySessionStatus.Exited;
                _exitCode ??= 0;
            }

            return;
        }

        var process = _process;
        if (process is not null)
        {
            try
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
            }
            catch (InvalidOperationException)
            {
            }

            lock (_gate)
            {
                _status = PtySessionStatus.Exited;
                _exitCode = process.ExitCode;
            }
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        if (_unix is not null)
        {
            _unix.Exited -= OnUnixExited;
            _unix.Dispose();
        }

        _process?.Dispose();
    }

    internal PtySessionInfo ToInfo()
    {
        lock (_gate)
            return new PtySessionInfo(Id, Command, StartedAt, ProcessId, _status, _exitCode, _attached);
    }

    private void OnUnixExited(int? exitCode)
    {
        lock (_gate)
        {
            _status = PtySessionStatus.Exited;
            _exitCode = exitCode;
        }
    }

    private static Process StartWindowsProcess(PtyStartInfo info)
    {
        var startInfo = new ProcessStartInfo(info.FileName)
        {
            WorkingDirectory = info.WorkingDirectory ?? Environment.CurrentDirectory,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var argument in info.Arguments)
            startInfo.ArgumentList.Add(argument);

        var process = new Process { StartInfo = startInfo };
        process.Start();
        process.StandardInput.AutoFlush = true;
        return process;
    }
}
