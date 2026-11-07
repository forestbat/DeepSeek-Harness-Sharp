using Dsh.Runtime;
using Dsh.Transport;

namespace Dsh.Sandbox;

/// <summary>Builds child process options only for launchers that explicitly request an OS sandbox.</summary>
public static class SubprocessSandboxOptions
{
    public static SandboxProcessOptions Create(SubprocessSpawnSpec spec, string executable, string temporaryDirectory,
        bool allowRead, bool allowWrite, bool allowNetwork, string? deniedWritePath = null)
    {
        if (!allowRead && allowWrite)
            throw new NotSupportedException("scripts.permissions read=false/write=true cannot be enforced by this filesystem backend; use mediated write tools instead.");
        var home = Path.Combine(temporaryDirectory, "home");
        OwnerOnlyFiles.EnsureDirectory(home);
        var environment = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in new[] { "PATH", "LANG", "LC_ALL", "LC_CTYPE", "TZ", "SystemRoot", "WINDIR", "PATHEXT" })
            if (System.Environment.GetEnvironmentVariable(name) is { } value)
                environment[name] = value;
        if (spec.Env is not null)
            foreach (var (name, value) in spec.Env)
            {
                if (IsLoaderVariable(name))
                    throw new InvalidOperationException("Sandboxed process environment cannot override runtime loader configuration.");
                environment[name] = value;
            }
        // Child-only technical paths do not alter the operator's environment.
        foreach (var name in new[] { "HOME", "USERPROFILE", "APPDATA", "LOCALAPPDATA", "DOTNET_CLI_HOME", "XDG_CONFIG_HOME", "XDG_CACHE_HOME", "XDG_DATA_HOME", "XDG_RUNTIME_DIR" })
            environment[name] = home;
        foreach (var name in new[] { "TMPDIR", "TMP", "TEMP" })
            environment[name] = temporaryDirectory;
        if (SandboxRuntimePaths.DotnetRoot() is { } frameworkRoot)
            environment["DOTNET_ROOT"] = frameworkRoot;
        var runtimePaths = new HashSet<string>(SandboxRuntimePaths.ForExecutable(executable),
            OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        if (!OperatingSystem.IsWindows())
            foreach (var path in new[] { "/etc/ssl/certs", "/usr/share/ca-certificates", "/etc/ld.so.cache", "/etc/resolv.conf", "/etc/hosts", "/etc/nsswitch.conf" })
                if (Directory.Exists(path) || File.Exists(path))
                    runtimePaths.Add(path);
        return new SandboxProcessOptions
        {
            Executable = executable,
            Arguments = spec.Argv.Skip(1).ToArray(),
            WorkingDirectory = spec.Cwd,
            GuestOnlyWorkingDirectory = !allowRead && !allowWrite,
            RuntimePaths = runtimePaths.ToArray(),
            ReadOnlyPaths = [.. (allowRead && !allowWrite ? new[] { spec.Cwd } : []), .. spec.AdditionalReadOnlyPaths],
            WritablePaths = [.. (allowWrite ? new[] { spec.Cwd } : []), temporaryDirectory, .. spec.AdditionalWritablePaths],
            DeniedWritePaths = deniedWritePath is null ? [] : [deniedWritePath],
            AllowNetwork = allowNetwork,
            AllowChildProcesses = true,
            Environment = environment,
        };
    }

    private static bool IsLoaderVariable(string name)
        => name.StartsWith("LD_", StringComparison.OrdinalIgnoreCase)
           || name.StartsWith("DYLD_", StringComparison.OrdinalIgnoreCase)
           || name.StartsWith("DOTNET_", StringComparison.OrdinalIgnoreCase)
           || name.StartsWith("COMPlus_", StringComparison.OrdinalIgnoreCase)
           || name.Equals("CORECLR_ENABLE_PROFILING", StringComparison.OrdinalIgnoreCase)
           || name.StartsWith("CORECLR_PROFILER", StringComparison.OrdinalIgnoreCase);
}
