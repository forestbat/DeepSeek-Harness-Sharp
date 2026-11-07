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
                if (SandboxEnvironment.IsLoaderInjection(name, overridingRuntime: true))
                    throw new InvalidOperationException("Sandboxed process environment cannot override runtime loader configuration.");
                environment[name] = value;
            }
        // Child-only technical paths do not alter the operator's environment.
        foreach (var name in SandboxEnvironment.PrivateDirectoryNames)
            if (name != "XDG_STATE_HOME")
                environment[name] = name is "TMPDIR" or "TMP" or "TEMP" ? temporaryDirectory : home;
        environment["DOTNET_CLI_HOME"] = home;
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

}
