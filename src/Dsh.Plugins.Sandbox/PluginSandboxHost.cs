using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Dsh.Boot;
using Dsh.Plugins;
using Dsh.Runtime;

namespace Dsh.Plugins.Sandbox;

/** Discovers plugins in the shared worker and applies the same execution roles to every source. */
internal sealed class PluginSandboxHost : PluginHost, IDisposable
{
    private const int MaximumPackagesPerArtifact = 256;
    private const int MaximumPackageCharacters = 256;
    private readonly PluginExecutionEnvironment _environment;
    private readonly string _directory;
    private readonly Func<INativePlugin, IDshPlugin>? _nativeBridge;
    private readonly PluginSandboxSupervisor _supervisor;
    private readonly ConcurrentDictionary<string, PluginRuntimeEntry> _entries = new(StringComparer.Ordinal);
    internal readonly record struct PluginRuntimeEntry(string? Artifact, PluginDescriptor Descriptor,
        PluginExecution Execution, PluginSandboxMetadata? Metadata, string? Failure = null);
    private PluginSkip[] _failures = [];
    private readonly SemaphoreSlim _loading = new(1, 1);
    private readonly CancellationTokenSource _shutdown = new();
    private bool _disposed;

    internal PluginSandboxHost(Context context, PluginExecutionEnvironment environment, string directory,
        Func<INativePlugin, IDshPlugin>? nativeBridge, PluginCapabilityFactory capabilities)
    {
        _environment = environment;
        _directory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
        _nativeBridge = nativeBridge;
        Directory.CreateDirectory(_directory);
        _supervisor = new PluginSandboxSupervisor(context, environment, capabilities, _directory, this);
    }

    internal PluginRuntimeEntry? Entry(string package)
        => _entries.TryGetValue(package, out var entry) ? entry : null;

    internal void RecordEntry(PluginRuntimeEntry entry)
        => _entries.AddOrUpdate(entry.Descriptor.Package, static (_, replacement) => replacement,
            static (_, current, replacement) => replacement with { Failure = current.Failure }, entry);

    internal void SetFailure(string package, string? reason)
    {
        while (_entries.TryGetValue(package, out var entry)
            && !_entries.TryUpdate(package, entry with { Failure = reason }, entry))
        {
        }
    }

    internal void ClearFailures()
    {
        foreach (var entry in _entries)
            if (entry.Value.Failure is not null)
                SetFailure(entry.Key, null);
    }

    public override IReadOnlyList<PluginSkip> Failures => [.. Volatile.Read(ref _failures),
        .. _entries.Where(entry => entry.Value.Failure is not null).Select(entry => new PluginSkip(entry.Key, entry.Value.Failure!))];
    public override string? ArtifactPath(string package) => _entries.GetValueOrDefault(package).Artifact;
    public override PluginForm? Form(string package)
        => _entries.TryGetValue(package, out var entry) ? entry.Descriptor.Form : null;
    public override IReadOnlyList<string> DomainPackages(string package) => _supervisor.DomainPackages(package);
    public override bool IsFaulted(string package) => Entry(package)?.Failure is not null;
    public override Task RetireDomainAsync(string package, CancellationToken cancellationToken = default)
        => _supervisor.RetireDomainAsync(package, cancellationToken);

    public override async Task<IReadOnlyList<string>> InspectPackagesAsync(string path, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _shutdown.Token);
        var full = Path.GetFullPath(path);
        var inspection = await _supervisor.InspectAsync(full, null, linked.Token).ConfigureAwait(false);
        Validate(inspection, full);
        var active = CurrentSettings();
        foreach (var metadata in inspection.Packages)
            _ = Role(metadata, (active.Plugins.GetValueOrDefault(metadata.Package) ?? new PluginSetting()) with { Enabled = true });
        return inspection.Packages.Select(package => package.Package).ToArray();
    }

    public override async Task<PluginCatalogLoadResult> LoadAsync(string path, CancellationToken cancellationToken = default)
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

    public override async Task<PluginScanResult> ScanAsync(CancellationToken cancellationToken = default)
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
                    foreach (var source in _entries.Where(source => source.Value.Artifact is not null
                        && PluginSandboxLaunch.PathComparer.Equals(source.Value.Artifact, path)))
                        Catalog.Remove(source.Key);
                    skipped.Add(new PluginSkip(path, error.Message));
                }
            }
            await RegisterCompiledAsync(skipped, linked.Token).ConfigureAwait(false);
            Volatile.Write(ref _failures, skipped.ToArray());
            return new PluginScanResult(managed, native, Failures);
        }
        catch
        {
            Release(new PluginScanResult(managed, native, []));
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

    private async Task RegisterCompiledAsync(List<PluginSkip> skipped, CancellationToken signal)
    {
        var active = CurrentSettings();
        foreach (var registration in DshPluginCatalogRegistry.Snapshot())
        {
            signal.ThrowIfCancellationRequested();
            try { await RegisterCompiledPackageAsync(registration, active, signal).ConfigureAwait(false); }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                Catalog.Remove(registration.Package);
                skipped.Add(new PluginSkip(registration.Package, error.Message));
            }
        }
    }

    private async Task RegisterCompiledPackageAsync(DshPluginRegistration registration, PluginExecutionSettings active,
        CancellationToken signal)
    {
        var hasKnown = _entries.TryGetValue(registration.Package, out var known);
        if (hasKnown && known.Artifact is not null)
            return;
        var setting = active.Plugins.GetValueOrDefault(registration.Package);
        var role = PluginRoleResolver.Resolve(registration.Package, setting, PluginForm.CompiledIn);
        if (hasKnown && known.Execution == role && Catalog.TryDescribe(registration.Package, out _)
            && (role == PluginExecution.Host || setting?.Enabled == false || known.Metadata is not null))
        {
            if (known.Metadata is not null)
                _ = Role(known.Metadata, setting);
            return;
        }
        var descriptor = PluginDescriptor.For(registration.Package, PluginForm.CompiledIn) with { Entry = registration.Entry };
        PluginSandboxMetadata? metadata = null;
        if (role == PluginExecution.Host)
        {
            Catalog.Remove(registration.Package);
            Catalog.Register(descriptor, registration.Create);
            RecordEntry(new PluginRuntimeEntry(null, descriptor, role, metadata));
        }
        else
        {
            if (setting?.Enabled != false)
            {
                var inspection = await _supervisor.InspectAsync(null, registration.Package, signal).ConfigureAwait(false);
                Validate(inspection, registration.Package);
                metadata = inspection.Packages.SingleOrDefault(package => package.Package == registration.Package)
                    ?? throw new RuntimeException("PLUGIN_INSPECTION_MISMATCH", "The compiled plugin inspection did not describe the requested package.");
                _ = Role(metadata, setting);
            }
            _supervisor.Register(new PluginRuntimeEntry(null, descriptor, role, metadata));
        }
    }

    private PluginExecutionSettings CurrentSettings() => _environment.ReadSettings();

    private async Task<PluginCatalogLoadResult> LoadCoreAsync(string path, CancellationToken signal)
    {
        var active = CurrentSettings();
        var known = _entries.Where(source => source.Value.Artifact is not null
            && PluginSandboxLaunch.PathComparer.Equals(source.Value.Artifact, path)).Select(source => source.Key).ToArray();
        foreach (var package in known)
            if (_entries[package].Metadata is { } metadata)
                _ = Role(metadata, active.Plugins.GetValueOrDefault(package));
        if (known.Length > 0 && known.All(package => Catalog.TryDescribe(package, out _)
            && _entries[package].Execution == (active.Plugins.GetValueOrDefault(package)?.Enabled != false
                ? PluginRoleResolver.Resolve(package, active.Plugins.GetValueOrDefault(package), _entries[package].Descriptor.Form)
                : PluginExecution.Sandbox)))
            return new PluginCatalogLoadResult(known, new PluginScanResult([], [], []));
        var form = PluginSandboxLaunch.InspectForm(path);
        var inspection = await _supervisor.InspectAsync(path, null, signal).ConfigureAwait(false);
        Validate(inspection, path);
        var hostPackages = new Dictionary<string, PluginSandboxMetadata>(StringComparer.Ordinal);
        foreach (var metadata in inspection.Packages)
        {
            var setting = active.Plugins.GetValueOrDefault(metadata.Package);
            if (Role(metadata, setting) == PluginExecution.Host && setting?.Enabled != false)
                hostPackages.Add(metadata.Package, metadata);
        }
        if (inspection.Packages.Length == 0)
            return new PluginCatalogLoadResult([], new PluginScanResult([], [], []));
        var hosted = hostPackages.Count == 0 ? new PluginCatalogLoadResult([], new PluginScanResult([], [], []))
            : LoadHost(path, form, active, hostPackages, signal);
        var packages = new List<string>(hosted.Packages);
        foreach (var metadata in inspection.Packages)
        {
            if (hostPackages.ContainsKey(metadata.Package))
                continue;
            DemandUnique(metadata.Package, path);
            if (_entries.TryGetValue(metadata.Package, out var existing) && existing.Artifact is not null
                && PluginSandboxLaunch.PathComparer.Equals(existing.Artifact, path) && existing.Execution == PluginExecution.Sandbox
                && Catalog.TryDescribe(metadata.Package, out _))
            {
                packages.Add(metadata.Package);
                continue;
            }
            var descriptor = PluginDescriptor.For(metadata.Package, form);
            _supervisor.Register(new PluginRuntimeEntry(path, descriptor, PluginExecution.Sandbox, metadata));
            packages.Add(metadata.Package);
        }
        return new PluginCatalogLoadResult(packages, hosted.Loaded);
    }

    private PluginCatalogLoadResult LoadHost(string path, PluginForm form,
        PluginExecutionSettings active, Dictionary<string, PluginSandboxMetadata> hostPackages, CancellationToken signal)
    {
        signal.ThrowIfCancellationRequested();
        SharedPool ??= new SharedAssemblyPool(Path.Combine(_directory, ".shared"));
        var temporary = new PluginHost { SharedPool = SharedPool };
        var managed = new List<ManagedPlugin>();
        var native = new List<INativePlugin>();
        var loaded = new List<string>();
        var skipped = new List<PluginSkip>();
        if (form == PluginForm.ManagedAssembly && RuntimeFeature.IsDynamicCodeSupported)
        {
            var result = temporary.TryLoad(path);
            skipped.AddRange(result.Skipped);
            foreach (var package in result.Packages.Where(hostPackages.ContainsKey))
            {
                RegisterHost(temporary, package, path, active, hostPackages.GetValueOrDefault(package));
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
                RegisterHost(temporary, result.Plugin!.Package, path, active, hostPackages.GetValueOrDefault(result.Plugin.Package));
                native.Add(result.Plugin);
                loaded.Add(result.Plugin.Package);
            }
            catch
            {
                result.Plugin!.Dispose();
                throw;
            }
        }
        return new PluginCatalogLoadResult(loaded, new PluginScanResult(managed, native, skipped));
    }

    private void RegisterHost(PluginHost temporary, string package, string source, PluginExecutionSettings active,
        PluginSandboxMetadata? metadata)
    {
        if (PluginRoleResolver.Resolve(package, active.Plugins.GetValueOrDefault(package),
            metadata?.Form ?? PluginSandboxLaunch.InspectForm(source)) != PluginExecution.Host)
            throw new RuntimeException("PLUGIN_HOST_ROLE_REQUIRED", "The file plugin is not configured for host execution.");
        DemandUnique(package, source);
        if (!temporary.Catalog.TryDescribe(package, out var descriptor) || !temporary.Catalog.TryGet(package, out var create))
            throw new RuntimeException("PLUGIN_HOST_LOAD_FAILED", "The explicitly trusted plugin has no registered implementation.");
        Catalog.Remove(package);
        Catalog.Register(descriptor, create);
        RecordEntry(new PluginRuntimeEntry(source, descriptor, PluginExecution.Host, metadata));
    }

    private string? InstallDirectory(string path)
    {
        var directory = Path.GetDirectoryName(path)!;
        return !PluginSandboxLaunch.PathComparer.Equals(directory, _directory)
            && PluginSandboxLaunch.PathComparer.Equals(Path.GetDirectoryName(directory), _directory) ? directory : null;
    }

    private void DemandUnique(string package, string path)
    {
        if (_entries.TryGetValue(package, out var existing) && existing.Artifact is not null
            && !PluginSandboxLaunch.PathComparer.Equals(existing.Artifact, path))
            throw new RuntimeException("PLUGIN_DUPLICATE_PACKAGE", $"Plugin {package} is already registered from another artifact.");
    }

    private static PluginExecution Role(PluginSandboxMetadata metadata, PluginSetting? setting)
        => PluginRoleResolver.Resolve(metadata.Package, setting, metadata.Form, metadata.SupportsCapabilities, metadata.Entry);

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
