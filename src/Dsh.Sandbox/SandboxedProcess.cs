using Dsh.Runtime;

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
    /// <summary>Paths denied write access even when covered by a writable grant.</summary>
    public IReadOnlyList<string> DeniedWritePaths { get; init; } = [];
    /// <summary>Validated child-only entries. Ambient host environment and credentials are never inherited.</summary>
    public IReadOnlyDictionary<string, string?> Environment { get; init; } = EmptyEnvironment;
    public bool AllowNetwork { get; init; }
    /// <summary>Windows controls the Job process count; macOS rejects true, while Linux owns descendants through a PID namespace.</summary>
    public bool AllowChildProcesses { get; init; }
}

public static class SandboxedProcess
{
    /// <summary>The cached trusted native launcher selected from the host environment, never a child-supplied environment.</summary>
    public static string? BackendExecutablePath => OperatingSystem.IsLinux() ? LinuxSandbox.BackendExecutablePath
        : OperatingSystem.IsMacOS() ? "/usr/bin/sandbox-exec" : null;

    public static async Task<OwnedProcess> StartAsync(SandboxProcessOptions options, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsWindows() && !OperatingSystem.IsMacOS())
            throw new PlatformNotSupportedException("No sandbox backend exists for this operating system.");
        if (OperatingSystem.IsMacOS() && options is { AllowChildProcesses: true })
            throw new PlatformNotSupportedException("macOS sandbox child processes cannot currently be owned without escape races: XNU does not mediate setsid/setpgid with Seatbelt. Run a single executable or use a separately provisioned kernel-owned execution domain; no process-group approximation is used.");
        var policy = SandboxPolicy.Create(options);
        IChildProcessLifetime lifetime = OperatingSystem.IsLinux() ? LinuxSandbox.Start(policy)
            : OperatingSystem.IsWindows() ? WindowsSandbox.Start(policy, cancellationToken)
            : OperatingSystem.IsMacOS() ? MacSandbox.Start(policy)
            : throw new PlatformNotSupportedException("No sandbox backend exists for this operating system.");
        var process = new OwnedProcess(lifetime, cancellationToken);
        if (!cancellationToken.IsCancellationRequested)
            return process;
        await process.DisposeAsync().ConfigureAwait(false);
        throw new OperationCanceledException(cancellationToken);
    }
}
