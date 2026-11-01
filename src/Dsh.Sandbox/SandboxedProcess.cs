namespace Dsh.Sandbox;

public sealed record SandboxProcessOptions
{
    private static readonly IReadOnlyDictionary<string, string?> EmptyEnvironment =
        new System.Collections.ObjectModel.ReadOnlyDictionary<string, string?>(new Dictionary<string, string?>());
    public required string Executable { get; init; }
    public IReadOnlyList<string> Arguments { get; init; } = [];
    public string? WorkingDirectory { get; init; }
    /// <summary>Linux-only empty tmpfs at the requested working-directory pathname, without binding that host directory.</summary>
    public bool GuestOnlyWorkingDirectory { get; init; }
    public IReadOnlyList<string> RuntimePaths { get; init; } = [];
    public IReadOnlyList<string> ReadOnlyPaths { get; init; } = [];
    public IReadOnlyList<string> WritablePaths { get; init; } = [];
    /// <summary>Protected settings must be existing single-link regular files when inside a writable directory; unsafe aliases fail closed.</summary>
    public IReadOnlyList<string> DeniedWritePaths { get; init; } = [];
    /// <summary>Validated child-only entries. Ambient host environment and credentials are never inherited.</summary>
    public IReadOnlyDictionary<string, string?> Environment { get; init; } = EmptyEnvironment;
    public bool AllowNetwork { get; init; }
    /// <summary>Defaults to false. macOS currently rejects true because no exact descendant ownership primitive is available.</summary>
    public bool AllowChildProcesses { get; init; }
    /// <summary>Linux cgroup memory.max, Windows job committed-memory limit, or macOS observed RSS budget (not a kernel cap).</summary>
    public required long MemoryLimitBytes { get; init; }
}

public static class SandboxedProcess
{
    /// <summary>The cached trusted native launcher selected from the host environment, never a child-supplied environment.</summary>
    public static string? BackendExecutablePath => OperatingSystem.IsLinux() ? LinuxSandbox.BackendExecutablePath
        : OperatingSystem.IsMacOS() ? "/usr/bin/sandbox-exec" : null;

    public static async Task<SandboxProcess> StartAsync(SandboxProcessOptions options, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsWindows() && !OperatingSystem.IsMacOS())
            throw new PlatformNotSupportedException("No sandbox backend exists for this operating system.");
        if (OperatingSystem.IsMacOS() && options is { AllowChildProcesses: true })
            throw new PlatformNotSupportedException("macOS sandbox child processes cannot currently be owned without escape races: XNU does not mediate setsid/setpgid with Seatbelt. Run a single executable or use a separately provisioned kernel-owned execution domain; no process-group approximation is used.");
        using var policy = SandboxPolicy.Create(options);
        ISandboxLifetime lifetime = OperatingSystem.IsLinux() ? LinuxSandbox.Start(policy)
            : OperatingSystem.IsWindows() ? WindowsSandbox.Start(policy)
            : OperatingSystem.IsMacOS() ? MacSandbox.Start(policy)
            : throw new PlatformNotSupportedException("No sandbox backend exists for this operating system.");
        var process = new SandboxProcess(lifetime, cancellationToken);
        if (!cancellationToken.IsCancellationRequested)
            return process;
        await process.DisposeAsync().ConfigureAwait(false);
        throw new OperationCanceledException(cancellationToken);
    }
}

/// <summary>Owns a native sandbox, including descendants and its redirected, otherwise unmodified standard streams.</summary>
public sealed class SandboxProcess : IAsyncDisposable
{
    private readonly ISandboxLifetime _lifetime;
    private readonly CancellationTokenRegistration _cancellation;
    private readonly Lock _disposeGate = new();
    private Task? _dispose;

    internal SandboxProcess(ISandboxLifetime lifetime, CancellationToken cancellationToken)
    {
        _lifetime = lifetime;
        _cancellation = cancellationToken.UnsafeRegister(static state => ((ISandboxLifetime)state!).Kill(), lifetime);
    }

    /// <summary>The owned launch PID; on Linux it is bubblewrap's outer supervisor, not necessarily the payload PID.</summary>
    public int Id => _lifetime.Id;
    public Stream StandardInput => _lifetime.StandardInput;
    public Stream StandardOutput => _lifetime.StandardOutput;
    public Stream StandardError => _lifetime.StandardError;
    /// <summary>Positive native limit evidence, not an inference from SIGKILL or exit 137. macOS reports a measured RSS-budget termination.</summary>
    public bool WasMemoryLimitExceeded => _lifetime.WasMemoryLimitExceeded;

    /// <summary>Waits for root exit and owned descendants to terminate. Keeps streams open for draining. Cancellation terminates the sandbox.</summary>
    public async Task<int> WaitForExitAsync(CancellationToken cancellationToken = default)
    {
        using var registration = cancellationToken.UnsafeRegister(static state => ((ISandboxLifetime)state!).Kill(), _lifetime);
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

internal interface ISandboxLifetime : IAsyncDisposable
{
    int Id { get; }
    Stream StandardInput { get; }
    Stream StandardOutput { get; }
    Stream StandardError { get; }
    bool WasMemoryLimitExceeded { get; }
    Task<int> Exit { get; }
    void Kill();
}
