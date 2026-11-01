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
    private readonly Func<HarnessSettings> _read;
    private readonly bool _startupEnabled;
    private const long MemoryLimitBytes = 1024L * 1024 * 1024;

    public ExternalProcessBoundary(Context context, HarnessHome home, HarnessSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        _context = context;
        _read = HarnessSettings.CreatePolicyReader(home, settings);
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
            var active = _read();
            if ((active.Sandbox?.Enabled == true) != _startupEnabled)
                throw new InvalidOperationException("Changing sandbox.enabled requires restarting the host; live plugin trust and process confinement cannot be partially switched.");
            return active;
        }
    }

    public bool Enabled => ActiveSettings.Sandbox?.Enabled == true;

    public string Revision
    {
        get
        {
            var active = ActiveSettings;
            return $"{active.Sandbox?.Enabled == true}:{SafetyBoundary.Revision(active.Safety ?? new SafetySettings())}";
        }
    }

    public string InvocationRevision
    {
        get
        {
            var run = SettingsMutationScope.Current?.Context;
            var active = ActiveSettings;
            var rules = active.Safety ?? new SafetySettings();
            return $"{active.Sandbox?.Enabled == true}:{SafetyBoundary.Revision(rules)}:"
                + string.Join(";", OpaquePermissionPolicy.DeniedWrites(rules, run,
                    run?.Agent?.Session.WorkingDirectory ?? Environment.CurrentDirectory));
        }
    }

    public void DemandUnconfined(string surface)
    {
        var rules = ActiveSettings.Safety ?? new SafetySettings();
        OpaquePermissionPolicy.DemandUnconfined(rules, surface, explicitHostGrant:
            SettingsMutationScope.Current?.Context.SafetyApprovalRevision == SafetyBoundary.AuthorizationRevision(rules, "safety"));
    }

    public void DemandConfiguredMcp(string surface, string lifecycleOwner, CancellationToken signal)
    {
        var revision = Revision;
        var safety = _context.Get<ISafetyBoundary>(ISafetyBoundary.ServiceName, false)
            ?? throw new InvalidOperationException("Configured MCP initialization requires the host safety boundary.");
        safety.AuthorizeConfiguredMcp(lifecycleOwner, signal);
        OpaquePermissionPolicy.DemandUnconfined(ActiveSettings.Safety ?? new SafetySettings(), surface, explicitHostGrant: true);
        signal.ThrowIfCancellationRequested();
        if (Revision != revision)
            throw new UnauthorizedAccessException("The MCP policy changed during initialization; reconnect under a fresh configuration.");
    }

    public async Task DemandAsync(SubprocessSpawnSpec spec)
    {
        var active = ActiveSettings;
        var run = SettingsMutationScope.Current?.Context;
        var script = spec.ExecutionKind == SubprocessExecutionKind.Script;
        PermissionRules rules = script ? active.Scripts?.Permissions ?? new ScriptPermissions() : active.Safety ?? new SafetySettings();
        var revision = ExecutionRevision(active, script);
        var safety = _context.Get<ISafetyBoundary>(ISafetyBoundary.ServiceName, false)
            ?? throw new InvalidOperationException("Opaque execution requires the host safety boundary.");
        if (run is null && spec.ExecutionKind == SubprocessExecutionKind.Mcp && spec.McpLifecycleOwner is { } owner)
            safety.AuthorizeConfiguredMcp(owner, spec.Signal);
        else
            await safety.AuthorizeOperationAsync(run, spec.Signal, run is null ? spec.ExecutionKind.ToString() : null,
                scriptPermissions: script).ConfigureAwait(false);
        spec.Signal.ThrowIfCancellationRequested();
        active = ActiveSettings;
        if (ExecutionRevision(active, script) != revision)
            throw new UnauthorizedAccessException("The execution policy changed during approval; no process was started. Authorize a new invocation.");
        rules = script ? active.Scripts?.Permissions ?? new ScriptPermissions() : active.Safety ?? new SafetySettings();
        var denied = OpaquePermissionPolicy.DeniedWrites(rules, run, spec.Cwd);
        if (active.Sandbox?.Enabled != true && (denied.Count != 0
            || script && active.Scripts?.Permissions is { } permissions && (!permissions.Read || !permissions.Write || !permissions.Network)))
            throw new NotSupportedException("The requested opaque restrictions need sandbox.enabled ON; sandbox-off execution cannot enforce them.");
    }

    private static string ExecutionRevision(HarnessSettings active, bool script)
    {
        var rules = script ? active.Scripts?.Permissions ?? new ScriptPermissions() : (PermissionRules?)active.Safety ?? new SafetySettings();
        var permissions = script ? active.Scripts?.Permissions : null;
        return $"{active.Sandbox?.Enabled == true}:{SafetyBoundary.Revision(rules)}:{permissions?.Read ?? true}:{permissions?.Write ?? true}:{permissions?.Network ?? true}";
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
            DeniedWritePaths = [_settingsPath, .. OpaquePermissionPolicy.DeniedWrites(
                script ? permissions ?? new ScriptPermissions() : active.Safety ?? new SafetySettings(),
                SettingsMutationScope.Current?.Context, spec.Cwd)],
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
