using System.Runtime.CompilerServices;
using Dsh.Plugins;
using Dsh.Runtime;

namespace Dsh.Boot;

public sealed record PluginSandboxLoadResult(IReadOnlyList<string> Packages, PluginScanResult Loaded);

/** Discovers plugins in the shared worker and applies the same execution roles to every source. */
public sealed class PluginSandboxCatalog : IDisposable
{
    private const int MaximumPackagesPerArtifact = 256;
    private const int MaximumPackageCharacters = 256;
    private readonly HarnessHome _home;
    private readonly PluginHost _host;
    private readonly HarnessSettings _settings;
    private readonly string _directory;
    private readonly Func<INativePlugin, IDshPlugin>? _nativeBridge;
    private readonly PluginSandboxSupervisor _supervisor;
    private readonly Dictionary<string, string?> _sources = new(StringComparer.Ordinal);
    private readonly Dictionary<string, PluginSandboxMetadata> _metadata = new(StringComparer.Ordinal);
    private readonly HashSet<string> _hostRoles = new(StringComparer.Ordinal);
    private PluginSkip[] _failures = [];
    private readonly SemaphoreSlim _loading = new(1, 1);
    private readonly CancellationTokenSource _shutdown = new();
    private bool _disposed;

    internal PluginSandboxCatalog(HarnessHome home, PluginHost host, HarnessSettings settings, string directory,
        Func<INativePlugin, IDshPlugin>? nativeBridge, PluginSandboxSupervisor supervisor)
    {
        _home = home;
        _host = host;
        _settings = settings;
        _directory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
        _nativeBridge = nativeBridge;
        _supervisor = supervisor;
        Directory.CreateDirectory(_directory);
    }

    public bool Enabled => _settings.Sandbox?.Enabled == true;
    public IReadOnlyList<PluginSkip> Failures => [.. Volatile.Read(ref _failures), .. _supervisor.Failures];
    public string? ArtifactPath(string package) => _sources.GetValueOrDefault(package);
    public IReadOnlyList<string> DomainPackages(string package) => _supervisor.DomainPackages(package);
    public bool IsFaulted(string package) => _supervisor.IsFaulted(package);
    public Task RetireAsync(string package, CancellationToken cancellationToken = default)
        => _supervisor.RetireAsync(package, cancellationToken);

    public async Task<IReadOnlyList<string>> InspectPackagesAsync(string path, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _shutdown.Token);
        var full = Path.GetFullPath(path);
        var inspection = await _supervisor.InspectAsync(full, null, linked.Token).ConfigureAwait(false);
        Validate(inspection, full);
        return inspection.Packages.Select(package => package.Package).ToArray();
    }

    public async Task<PluginSandboxLoadResult> LoadAsync(string path, CancellationToken cancellationToken = default)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _shutdown.Token);
        await _loading.WaitAsync(linked.Token).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return await LoadCoreAsync(Path.GetFullPath(path), linked.Token).ConfigureAwait(false);
        }
        finally { _loading.Release(); }
    }

    public async Task<PluginScanResult> ScanAsync(CancellationToken cancellationToken = default)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _shutdown.Token);
        await _loading.WaitAsync(linked.Token).ConfigureAwait(false);
        var managed = new List<ManagedPlugin>();
        var native = new List<INativePlugin>();
        var skipped = new List<PluginSkip>();
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            foreach (var path in Artifacts())
            {
                linked.Token.ThrowIfCancellationRequested();
                try
                {
                    var result = await LoadCoreAsync(path, linked.Token).ConfigureAwait(false);
                    managed.AddRange(result.Loaded.Managed);
                    native.AddRange(result.Loaded.Native);
                    skipped.AddRange(result.Loaded.Skipped);
                }
                catch (Exception error) when (error is not OperationCanceledException)
                {
                    skipped.Add(new PluginSkip(path, error.Message));
                }
            }
            await RegisterCompiledAsync(linked.Token).ConfigureAwait(false);
            Volatile.Write(ref _failures, skipped.ToArray());
            return new PluginScanResult(managed, native, Failures);
        }
        catch
        {
            ReleaseLoaded(_host, new PluginScanResult(managed, native, []));
            throw;
        }
        finally { _loading.Release(); }
    }

    private IEnumerable<string> Artifacts()
    {
        if (!Directory.Exists(_directory))
            yield break;
        foreach (var child in Directory.EnumerateDirectories(_directory).OrderBy(path => path, StringComparer.Ordinal))
        {
            if (Path.GetFileName(child) == ".shared")
                continue;
            foreach (var file in Directory.EnumerateFiles(child).OrderBy(path => path, StringComparer.Ordinal))
                if (IsArtifact(file))
                    yield return Path.GetFullPath(file);
        }
        foreach (var file in Directory.EnumerateFiles(_directory).OrderBy(path => path, StringComparer.Ordinal))
            if (IsArtifact(file))
                yield return Path.GetFullPath(file);
    }

    private static bool IsArtifact(string path)
        => Path.GetExtension(path) is var extension
            && (extension.Equals(".dll", StringComparison.OrdinalIgnoreCase)
                || extension.Equals(".so", StringComparison.OrdinalIgnoreCase)
                || extension.Equals(".dylib", StringComparison.OrdinalIgnoreCase));

    private async Task RegisterCompiledAsync(CancellationToken signal)
    {
        var active = CurrentSettings();
        foreach (var registration in DshPluginCatalogRegistry.Snapshot())
        {
            if (_sources.TryGetValue(registration.Package, out var artifact) && artifact is not null)
                continue;
            var setting = active.Plugins.GetValueOrDefault(registration.Package);
            if (_sources.ContainsKey(registration.Package) && _host.Catalog.PackageNames.Contains(registration.Package, StringComparer.Ordinal))
            {
                if (_metadata.TryGetValue(registration.Package, out var known))
                    DemandExecution(known, setting);
                if (setting?.Enabled == false || (_hostRoles.Contains(registration.Package) == (setting?.Execution == PluginExecution.Host)
                    && (setting?.Execution == PluginExecution.Host || _metadata.ContainsKey(registration.Package))))
                    continue;
            }
            var descriptor = PluginDescriptor.For(registration.Package, PluginForm.CompiledIn) with { Entry = registration.Entry };
            if (setting?.Execution == PluginExecution.Host)
            {
                if (!_hostRoles.Contains(registration.Package) || !_host.Catalog.PackageNames.Contains(registration.Package, StringComparer.Ordinal))
                {
                    _host.Catalog.Remove(registration.Package);
                    _host.Catalog.Register(descriptor, registration.Create);
                }
                _hostRoles.Add(registration.Package);
            }
            else
            {
                if (setting?.Enabled == false)
                {
                    _supervisor.Register(_host, new PluginSandboxSource(descriptor, null));
                    _sources[registration.Package] = null;
                    _hostRoles.Remove(registration.Package);
                    continue;
                }
                var inspection = await _supervisor.InspectAsync(null, registration.Package, signal).ConfigureAwait(false);
                Validate(inspection, registration.Package);
                var metadata = inspection.Packages.SingleOrDefault(package => package.Package == registration.Package)
                    ?? throw new RuntimeException("PLUGIN_INSPECTION_MISMATCH", "The compiled plugin inspection did not describe the requested package.");
                _metadata[metadata.Package] = metadata;
                DemandExecution(metadata, setting);
                _supervisor.Register(_host, new PluginSandboxSource(descriptor, null));
                _hostRoles.Remove(registration.Package);
            }
            _sources[registration.Package] = null;
        }
    }

    private HarnessSettings CurrentSettings()
        => File.Exists(_home.SettingsFile) ? HarnessSettings.LoadForReview(_home) : _settings;

    private async Task<PluginSandboxLoadResult> LoadCoreAsync(string path, CancellationToken signal)
    {
        var active = CurrentSettings();
        var known = _sources.Where(source => source.Value is not null
            && PluginSandboxLaunch.PathComparer.Equals(source.Value, path)).Select(source => source.Key).ToArray();
        foreach (var package in known)
            if (_metadata.TryGetValue(package, out var metadata))
                DemandExecution(metadata, active.Plugins.GetValueOrDefault(package));
        if (known.Length > 0 && known.All(package => _host.Catalog.PackageNames.Contains(package, StringComparer.Ordinal)
            && _hostRoles.Contains(package) == (active.Plugins.GetValueOrDefault(package) is { Enabled: not false, Execution: PluginExecution.Host })))
            return new PluginSandboxLoadResult(known, new PluginScanResult([], [], []));
        var form = PluginSandboxLaunch.InspectForm(path);
        var inspection = await _supervisor.InspectAsync(path, null, signal).ConfigureAwait(false);
        Validate(inspection, path);
        foreach (var metadata in inspection.Packages)
        {
            _metadata[metadata.Package] = metadata;
            DemandExecution(metadata, active.Plugins.GetValueOrDefault(metadata.Package));
        }
        if (inspection.Packages.Length == 0)
            return new PluginSandboxLoadResult([], new PluginScanResult([], [], []));
        var hostPackages = inspection.Packages.Where(package =>
            active.Plugins.GetValueOrDefault(package.Package) is { Enabled: not false, Execution: PluginExecution.Host })
            .Select(package => package.Package).ToHashSet(StringComparer.Ordinal);
        var hosted = hostPackages.Count == 0 ? new PluginSandboxLoadResult([], new PluginScanResult([], [], []))
            : LoadHost(path, form, active, hostPackages, signal);
        var packages = new List<string>(hosted.Packages);
        foreach (var metadata in inspection.Packages)
        {
            if (hostPackages.Contains(metadata.Package))
                continue;
            DemandUnique(metadata.Package, path);
            if (_sources.TryGetValue(metadata.Package, out var existing) && existing is not null
                && PluginSandboxLaunch.PathComparer.Equals(existing, path) && !_hostRoles.Contains(metadata.Package)
                && _host.Catalog.PackageNames.Contains(metadata.Package, StringComparer.Ordinal))
            {
                packages.Add(metadata.Package);
                continue;
            }
            var descriptor = PluginDescriptor.For(metadata.Package, form);
            _supervisor.Register(_host, new PluginSandboxSource(descriptor, path));
            _sources[metadata.Package] = path;
            _hostRoles.Remove(metadata.Package);
            packages.Add(metadata.Package);
        }
        return new PluginSandboxLoadResult(packages, hosted.Loaded);
    }

    private PluginSandboxLoadResult LoadHost(string path, PluginForm form,
        HarnessSettings active, HashSet<string> hostPackages, CancellationToken signal)
    {
        signal.ThrowIfCancellationRequested();
        _host.SharedPool ??= new SharedAssemblyPool(Path.Combine(_directory, ".shared"));
        var temporary = new PluginHost { SharedPool = _host.SharedPool };
        var managed = new List<ManagedPlugin>();
        var native = new List<INativePlugin>();
        var loaded = new List<string>();
        var skipped = new List<PluginSkip>();
        if (form == PluginForm.ManagedAssembly && RuntimeFeature.IsDynamicCodeSupported)
        {
            var result = temporary.TryLoad(path);
            skipped.AddRange(result.Skipped);
            foreach (var package in result.Packages.Where(hostPackages.Contains))
            {
                RegisterHost(temporary, package, path, active);
                managed.Add(new ManagedPlugin(package, result.Context!, result.SharedDependencies, InstallDirectory(path)));
                loaded.Add(package);
            }
        }
        else
        {
            if (_nativeBridge is null)
                throw new RuntimeException("PLUGIN_NATIVE_BRIDGE_MISSING", "This host has no native plugin ABI bridge.");
            var result = temporary.LoadNativePlugin(path, _nativeBridge);
            if (!result.Succeeded)
                throw new RuntimeException("PLUGIN_NATIVE_LOAD_FAILED", result.Reason ?? "Native plugin loading failed.");
            try
            {
                RegisterHost(temporary, result.Plugin!.Package, path, active);
                native.Add(result.Plugin);
                loaded.Add(result.Plugin.Package);
            }
            catch
            {
                result.Plugin!.Dispose();
                throw;
            }
        }
        return new PluginSandboxLoadResult(loaded, new PluginScanResult(managed, native, skipped));
    }

    private void RegisterHost(PluginHost temporary, string package, string source, HarnessSettings active)
    {
        DemandHost(package, active);
        DemandUnique(package, source);
        if (!temporary.Catalog.TryDescribe(package, out var descriptor) || !temporary.Catalog.TryGet(package, out var create))
            throw new RuntimeException("PLUGIN_HOST_LOAD_FAILED", "The explicitly trusted plugin has no registered implementation.");
        _host.Catalog.Remove(package);
        _host.Catalog.Register(descriptor, create);
        _sources[package] = source;
        _hostRoles.Add(package);
    }

    private string? InstallDirectory(string path)
    {
        var directory = Path.GetDirectoryName(path)!;
        return !PluginSandboxLaunch.PathComparer.Equals(directory, _directory)
            && PluginSandboxLaunch.PathComparer.Equals(Path.GetDirectoryName(directory), _directory) ? directory : null;
    }

    private void DemandUnique(string package, string path)
    {
        if (_sources.TryGetValue(package, out var existing) && existing is not null
            && !PluginSandboxLaunch.PathComparer.Equals(existing, path))
            throw new RuntimeException("PLUGIN_DUPLICATE_PACKAGE", $"Plugin {package} is already registered from another artifact.");
    }

    private static void DemandExecution(PluginSandboxMetadata metadata, PluginSetting? setting)
    {
        if (setting?.Enabled != false && setting?.Execution != PluginExecution.Host
            && (!metadata.SupportsCapabilities || metadata.Entry is not null))
            throw HostRoleRequired(metadata.Package);
    }

    private static void DemandHost(string package, HarnessSettings settings)
    {
        if (settings.Plugins.GetValueOrDefault(package)?.Execution != PluginExecution.Host)
            throw HostRoleRequired(package);
    }

    private static RuntimeException HostRoleRequired(string package)
        => new("PLUGIN_HOST_ROLE_REQUIRED",
            $"Plugin {package} uses the full Context API and requires plugins.\"{package}\".execution: host while sandbox.enabled is ON. Configure this role explicitly or migrate the plugin to IDshIsolatedPlugin; host execution is not OS-confined.");

    private static void Validate(PluginSandboxInspection inspection, string source)
    {
        if (inspection.Packages.Length > MaximumPackagesPerArtifact
            || inspection.Packages.Any(package => string.IsNullOrWhiteSpace(package.Package)
                || package.Package.Length > MaximumPackageCharacters || package.Package.AsSpan().Trim().Length != package.Package.Length
                || package.Package.Any(char.IsControl))
            || inspection.Packages.Select(package => package.Package).Distinct(StringComparer.Ordinal).Count() != inspection.Packages.Length)
            throw new RuntimeException("PLUGIN_INVALID_METADATA", "The isolated inspection returned invalid or duplicate package metadata.");
        if (inspection.Errors.Length > 0)
            throw new RuntimeException("PLUGIN_INSPECTION_FAILED", $"Plugin inspection failed for {Path.GetFileName(source)}: {string.Join("; ", inspection.Errors)}");
    }

    internal static void ReleaseLoaded(PluginHost host, PluginScanResult loaded)
    {
        foreach (var managed in loaded.Managed)
            host.Catalog.Remove(managed.Package);
        foreach (var native in loaded.Native)
        {
            host.Catalog.Remove(native.Package);
            native.Dispose();
        }
        var contexts = new HashSet<PluginLoadContext>();
        foreach (var managed in loaded.Managed)
            if (contexts.Add(managed.Context))
            {
                host.SharedPool?.Release(managed.SharedDependencies);
                managed.Context.Unload();
            }
    }

    public void Dispose()
    {
        _shutdown.Cancel();
        _loading.Wait();
        try
        {
            if (_disposed)
                return;
            _disposed = true;
            _supervisor.Dispose();
        }
        finally { _loading.Release(); }
    }
}
