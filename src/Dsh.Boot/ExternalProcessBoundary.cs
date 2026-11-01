using Dsh.Core;
using Dsh.Runtime;
using Dsh.Sandbox;
using Dsh.Transport;

namespace Dsh.Boot;

/// <summary>
/// Opaque model programs cannot provide a trustworthy proposed settings diff.
/// While sandboxing is ON, direct writes are denied by the OS; changes must use mediated write/edit tools or plugin capabilities.
/// </summary>
public sealed class ExternalProcessBoundary : IExternalProcessPolicy
{
    private readonly string _settingsPath;
    private readonly Context _context;
    private readonly HarnessHome _home;
    private readonly HarnessSettings _settings;
    private readonly bool _startupEnabled;
    private const long MemoryLimitBytes = 1024L * 1024 * 1024;

    public ExternalProcessBoundary(Context context, HarnessHome home, HarnessSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        _context = context;
        _home = home;
        _settings = settings;
        var gate = context.Get<ISettingsMutationGate>(ISettingsMutationGate.ServiceName, false)
            ?? throw new InvalidOperationException("External execution requires the settings mutation boundary first.");
        if (!gate.Owns(home.SettingsFile))
            throw new InvalidOperationException("External execution and settings approvals must protect the same active settings file.");
        _settingsPath = gate.SettingsPath;
        _startupEnabled = settings.Sandbox?.Enabled == true;
        context.Provide(IExternalProcessPolicy.ServiceName, this);
    }

    private HarnessSettings ActiveSettings
    {
        get
        {
            var active = File.Exists(_home.SettingsFile) ? HarnessSettings.LoadForReview(_home) : _settings;
            if ((active.Sandbox?.Enabled == true) != _startupEnabled)
                throw new InvalidOperationException("Changing sandbox.enabled requires restarting the host; live plugin trust and process confinement cannot be partially switched.");
            return active;
        }
    }

    public bool Enabled => ActiveSettings.Sandbox?.Enabled == true;

    public (bool Enabled, bool Read, bool Write, bool Network) ScriptPolicy
    {
        get
        {
            var active = ActiveSettings;
            if (active.Sandbox?.Enabled != true)
                return (false, true, true, true);
            var permissions = active.Scripts?.Permissions;
            return (true, permissions?.Read ?? true, permissions?.Write ?? true, permissions?.Network ?? true);
        }
    }

    public SandboxProcessOptions CreateOptions(SubprocessSpawnSpec spec, string executable, string temporaryDirectory)
    {
        var active = ActiveSettings;
        if (active.Sandbox?.Enabled != true)
            throw new InvalidOperationException("OS-confined process options require sandbox.enabled to be ON.");
        var authority = _context.Get<PluginHostAuthority>(PluginHostAuthority.ServiceName, false)
            ?? throw new InvalidOperationException("Sandboxed execution requires the host runtime code authority to be initialized; restart the host with sandbox.enabled ON.");
        var script = spec.ExecutionKind == SubprocessExecutionKind.Script;
        var permissions = script ? active.Scripts?.Permissions : null;
        var allowRead = permissions?.Read ?? true;
        var allowWrite = permissions?.Write ?? true;
        if (!allowRead && allowWrite)
            throw new NotSupportedException("scripts.permissions read=false/write=true cannot be enforced by this direct filesystem backend; write-only directory binding would also grant reads. Use mediated write tools instead.");
        var home = Path.Combine(temporaryDirectory, "home");
        OwnerOnlyFiles.EnsureDirectory(home);
        var environment = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in new[] { "PATH", "LANG", "LC_ALL", "LC_CTYPE", "TZ", "SystemRoot", "WINDIR", "PATHEXT" })
        {
            if (System.Environment.GetEnvironmentVariable(name) is { } value)
                environment[name] = value;
        }
        if (spec.Env is not null)
        {
            foreach (var (name, value) in spec.Env)
            {
                if (IsLoaderVariable(name))
                    throw new InvalidOperationException("Model process environment cannot override runtime loader configuration.");
                environment[name] = value;
            }
        }
        // These are child-only technical paths, not product configuration or the operator's HOME.
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
        string[] writablePaths = [.. (allowWrite ? new[] { spec.Cwd } : []), temporaryDirectory, .. spec.AdditionalWritablePaths];
        string[] readOnlyPaths = [.. (allowRead && !allowWrite ? new[] { spec.Cwd } : []), .. spec.AdditionalReadOnlyPaths];
        return new SandboxProcessOptions
        {
            Executable = executable,
            Arguments = spec.Argv.Skip(1).ToArray(),
            WorkingDirectory = spec.Cwd,
            GuestOnlyWorkingDirectory = !allowRead && !allowWrite,
            RuntimePaths = runtimePaths.ToArray(),
            ReadOnlyPaths = [.. readOnlyPaths,
                .. authority.ReadOnlyPaths(writablePaths)],
            WritablePaths = writablePaths,
            DeniedWritePaths = [_settingsPath],
            AllowNetwork = permissions?.Network ?? true,
            AllowChildProcesses = true,
            Environment = environment,
            MemoryLimitBytes = MemoryLimitBytes,
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
