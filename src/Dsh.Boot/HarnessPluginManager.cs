using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Dsh.Plugins;
using Dsh.Runtime;
using Dsh.Runtime.Composition;

namespace Dsh.Boot;

public sealed class HarnessPluginManager : IDisposable
{
    private const int CollectionGcRounds = 30;
    private const int UninstallRetryAttempts = 15;
    private const int UninstallRetryDelayMs = 25;

    private readonly PluginHost _host;
    private readonly Composition _composition;
    private readonly HarnessHome _home;
    private readonly HarnessSettings _settings;
    private readonly string _pluginsDirectory;
    private readonly Func<INativePlugin, IDshPlugin>? _nativeBridge;
    private readonly IReadOnlyList<PluginSkip> _loadFailures;
    private readonly PluginHost? _sandbox;
    private readonly Dictionary<string, PluginLoadContext> _loadedContexts = new(StringComparer.Ordinal);
    private readonly Dictionary<string, IReadOnlyList<string>> _sharedDependencies = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _installedDirectories = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _artifactPaths = new(StringComparer.Ordinal);
    private readonly Dictionary<string, INativePlugin> _nativePlugins = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, WeakReference?> _leaked = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _mutation = new(1, 1);

    public HarnessPluginManager(
        PluginHost host,
        Composition composition,
        HarnessHome home,
        HarnessSettings settings,
        IReadOnlyList<ManagedPlugin> loadedOnStart,
        IReadOnlyList<INativePlugin> nativeOnStart,
        IReadOnlyList<PluginSkip> loadFailures,
        Func<INativePlugin, IDshPlugin>? nativeBridge,
        string pluginsDirectory,
        PluginHost? sandbox)
    {
        _host = host;
        _composition = composition;
        _home = home;
        _settings = settings;
        _pluginsDirectory = pluginsDirectory;
        _nativeBridge = nativeBridge;
        _loadFailures = loadFailures;
        _sandbox = sandbox;
        Track(new PluginScanResult(loadedOnStart, nativeOnStart, []));
    }

    public IReadOnlyList<PluginSkip> LoadFailures => _sandbox is null
        ? _loadFailures
        : _loadFailures.Concat(_sandbox.Failures).Distinct().ToArray();

    public IReadOnlyList<string> PackageNames => _host.Catalog.PackageNames
        .Concat(_installedDirectories.Keys)
        .Concat(_settings.Plugins.Keys)
        .Distinct(StringComparer.Ordinal)
        .OrderBy(name => name, StringComparer.Ordinal)
        .ToList();

    public string Describe(string package)
    {
        if (_sandbox?.IsFaulted(package) == true)
            return "faulted [sandbox] (domain stopped; enable explicitly to recover; failed calls are not replayed)";
        if (_nativePlugins.TryGetValue(package, out var native) && native.State == NativePluginState.Faulted)
            return "faulted [native-library] (unloaded on fault; quarantined after repeat)";
        var activation = _composition.Find(package);
        var state = activation?.State switch
        {
            ActivationState.Active => "active",
            ActivationState.Activating => "activating",
            ActivationState.Deactivating => "deactivating (unloading)",
            ActivationState.Failed => $"failed: {activation.Error}",
            ActivationState.Disposed => "removed",
            ActivationState.Pending => "pending (dependencies missing)",
            null when _settings.Plugins.GetValueOrDefault(package) is { Enabled: false } => "disabled",
            _ => "available",
        };
        var descriptor = Descriptor(package);
        var line = descriptor is null ? state : $"{state} [{FormName(descriptor)}]";
        return HasLeakedPackage(package) ? $"{line}, leaked" : line;
    }

    public async Task<string> AddAsync(string packageOrPath,
        SettingsCommit? commit = null,
        CancellationToken cancellationToken = default)
    {
        await _mutation.WaitAsync(cancellationToken);
        try { return await AddCoreAsync(packageOrPath, commit, cancellationToken); }
        finally { _mutation.Release(); }
    }

    private async Task<string> AddCoreAsync(string packageOrPath,
        SettingsCommit? commit,
        CancellationToken cancellationToken)
    {
        var spec = packageOrPath.Trim();
        if (spec.Length == 0)
            return "usage: /plugins add <package|path>";
        if (IsLibraryPath(spec) || File.Exists(spec))
            return await AddFromPathAsync(spec, commit, cancellationToken);
        if (IsKnown(spec))
            return await EnableCoreAsync(spec, commit);
        return $"plugin package not found: {spec}";
    }

    public async Task<string> RemoveAsync(string package, bool force = false,
        SettingsCommit? commit = null)
    {
        await _mutation.WaitAsync();
        try { return await RemoveCoreAsync(package, force, commit); }
        finally { _mutation.Release(); }
    }

    private async Task<string> RemoveCoreAsync(string package, bool force,
        SettingsCommit? commit)
    {
        var trimmed = package.Trim();
        if (trimmed.Length == 0)
            return "usage: /plugins remove <package>";
        if (!IsKnown(trimmed))
            return $"plugin {trimmed} is not active";
        var domain = CaptureDomain(trimmed);
        await PersistAsync(trimmed, _ => new PluginSetting { Enabled = false }, commit);
        var message = _sandbox is null
            ? await DetachAsync(trimmed, force)
            : await RebuildDomainAsync(trimmed, domain, force);
        if (_sandbox is not null)
            message = $"{Removed(trimmed, domain.Any(HasLeakedPackage))}; {message}";
        if (domain.Any(HasLeakedPackage))
            return message;
        _artifactPaths.Remove(trimmed);
        var uninstall = await UninstallInstalledAsync(trimmed);
        return uninstall is null ? message : $"{message}; {uninstall}";
    }

    private async Task<string> DetachAsync(string package, bool force)
    {
        var (weak, leaked) = await UnloadAsync(package, force, reclaim: IsManaged(package), removeRegistration: true);
        if (leaked)
            _leaked[package] = null;
        else
            ClearSettledLeak(package);
        if ((weak is not null && !await VerifyCollectionAsync(package, weak))
            || _leaked.GetValueOrDefault(package)?.IsAlive == true)
            return $"plugin {package} deactivated; installation retained because the load context is still referenced";
        return Removed(package, leaked);
    }

    private async Task<string?> UninstallInstalledAsync(string package)
    {
        if (!_installedDirectories.Remove(package, out var directory))
            return null;
        // A deployment can contain multiple packages. Removing one must not delete its siblings' files.
        if (_installedDirectories.Values.Contains(directory, StringComparer.Ordinal))
            return "installation directory retained for other packages";
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                PluginInstall.Uninstall(directory, _pluginsDirectory);
                return $"已删除安装目录 {directory}";
            }
            catch (Exception error) when (attempt + 1 < UninstallRetryAttempts && error is IOException or UnauthorizedAccessException)
            {
                GC.Collect(2, GCCollectionMode.Forced, blocking: true);
                GC.WaitForPendingFinalizers();
                await Task.Delay(UninstallRetryDelayMs);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                _installedDirectories[package] = directory;
                return $"删除安装目录失败: {directory}: {error.Message}";
            }
        }
    }

    public async Task<string> DisableAsync(string package,
        SettingsCommit? commit = null)
    {
        await _mutation.WaitAsync();
        try { return await DisableCoreAsync(package, commit); }
        finally { _mutation.Release(); }
    }

    private async Task<string> DisableCoreAsync(string package,
        SettingsCommit? commit)
    {
        var trimmed = package.Trim();
        if (trimmed.Length == 0)
            return "usage: /plugins disable <package>";
        if (!IsKnown(trimmed))
            return $"plugin package not found: {trimmed}";
        var domain = CaptureDomain(trimmed);
        await PersistAsync(trimmed, existing => Toggled(existing, enabled: false), commit);
        if (_sandbox is not null)
        {
            var result = await RebuildDomainAsync(trimmed, domain, force: false);
            return $"plugin {trimmed} disabled; {result}";
        }
        if (_composition.Root.Scheduler.Find(trimmed) is not null)
        {
            var outcome = await UnloadAsync(trimmed, force: false, reclaim: false, removeRegistration: false);
            if (outcome.Leaked)
            {
                _leaked[trimmed] = null;
                return $"plugin {trimmed} disabled with timeout; effects may still be running (leaked)";
            }
        }
        return $"plugin {trimmed} disabled (written to settings.yaml)";
    }

    public async Task<string> EnableAsync(string package,
        SettingsCommit? commit = null)
    {
        await _mutation.WaitAsync();
        try { return await EnableCoreAsync(package, commit); }
        finally { _mutation.Release(); }
    }

    public async Task<string> ReloadAsync(string package, string? artifactPath = null,
        SettingsCommit? commit = null,
        CancellationToken cancellationToken = default)
    {
        await _mutation.WaitAsync(cancellationToken);
        try { return await ReloadCoreAsync(package, artifactPath, commit, cancellationToken); }
        finally { _mutation.Release(); }
    }

    /** Releases in-process plugin state before a separate local Host process mutates shared files. */
    public async Task<string?> DetachForExternalMutationAsync(string package, bool reclaimArtifact,
        CancellationToken cancellationToken = default)
    {
        await _mutation.WaitAsync(cancellationToken);
        try
        {
            var trimmed = package.Trim();
            return trimmed.Length == 0 || !IsKnown(trimmed)
                ? null
                : await ReleaseDomainForMutationAsync(trimmed, CaptureDomain(trimmed), reclaimArtifact, cancellationToken);
        }
        finally { _mutation.Release(); }
    }

    /** Reconciles in-process plugin activations with updated settings.yaml and plugins directory artifacts. */
    public async Task SynchronizeWithDiskAsync(CancellationToken cancellationToken = default, bool discoverArtifacts = true)
    {
        await _mutation.WaitAsync(cancellationToken);
        try { await SynchronizeCoreAsync(cancellationToken, discoverArtifacts); }
        finally { _mutation.Release(); }
    }

    private async Task<string> EnableCoreAsync(string package,
        SettingsCommit? commit)
    {
        var trimmed = package.Trim();
        if (trimmed.Length == 0)
            return "usage: /plugins enable <package>";
        if (!IsKnown(trimmed))
            return $"plugin package not found: {trimmed}";
        var domain = CaptureDomain(trimmed);
        var recover = _sandbox?.IsFaulted(trimmed) == true;
        await PersistAsync(trimmed, existing => Toggled(existing, enabled: true), commit);
        if (recover)
            return await RebuildDomainAsync(trimmed, domain, force: false);
        if (_composition.Root.Scheduler.Find(trimmed) is not null)
            return $"plugin {trimmed} enabled (already active)";
        if (_sandbox is not null)
            Track(await _sandbox.ScanAsync());
        return await ActivateAsync(trimmed);
    }

    public async Task<string> SetPermissionAsync(string package, PluginPermission permission, bool allowed,
        CancellationToken cancellationToken = default,
        SettingsCommit? commit = null)
    {
        await _mutation.WaitAsync(cancellationToken);
        try { return await SetPermissionCoreAsync(package, permission, allowed, cancellationToken, commit); }
        finally { _mutation.Release(); }
    }

    private async Task<string> SetPermissionCoreAsync(string package, PluginPermission permission, bool allowed,
        CancellationToken cancellationToken,
        SettingsCommit? commit)
    {
        var trimmed = package.Trim();
        var key = PluginPermissions.KeyOf(permission);
        if (trimmed.Length == 0)
            return $"usage: /plugins {(allowed ? "enable" : "ban")}_{key} <package>";
        if (!IsKnown(trimmed))
            return $"plugin package not found: {trimmed}";
        cancellationToken.ThrowIfCancellationRequested();
        await PersistAsync(trimmed, existing => (existing ?? new PluginSetting()) with
        {
            Permissions = (existing?.Permissions ?? PluginPermissions.AllAllowed).With(permission, allowed),
        }, commit);
        var result = $"plugin {trimmed} {key} permission {(allowed ? "allowed" : "denied")}";
        if (_sandbox is null)
            return $"{result} in its future isolation profile (sandbox is off; no OS access restrictions are enforced)";
        if (IsHostRole(trimmed))
            return $"{result} for broker calls; host-role code retains unrestricted in-process access";
        return $"{result}; the next broker operation uses the current permissions";
    }

    private static bool IsLibraryPath(string spec)
        => spec.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
            || spec.EndsWith(".so", StringComparison.OrdinalIgnoreCase)
            || spec.EndsWith(".dylib", StringComparison.OrdinalIgnoreCase);

    private async Task<string> AddFromPathAsync(string spec,
        SettingsCommit? commit,
        CancellationToken signal)
    {
        signal.ThrowIfCancellationRequested();
        var path = Path.GetFullPath(spec);
        if (!File.Exists(path))
            return $"plugin file not found: {path}";
        var packages = _sandbox is not null
            ? await _sandbox.InspectPackagesAsync(path, signal)
            : InspectLegacyPackages(path);
        if (packages.Count == 0)
            return $"assembly has no DSH plugin: {path}";
        await PersistAsync(packages, existing => Toggled(existing, enabled: true), commit);
        signal.ThrowIfCancellationRequested();
        string installPath;
        try
        {
            installPath = PluginInstall.Install(path, _pluginsDirectory);
        }
        catch (InvalidOperationException error)
        {
            return $"plugin install failed: {error.Message}";
        }
        var directory = Path.GetDirectoryName(installPath)!;
        foreach (var package in packages)
            _installedDirectories[package] = directory;
        return await LoadAndActivateArtifactAsync(installPath, signal);
    }

    private async Task<string> LoadAndActivateArtifactAsync(string installPath, CancellationToken signal)
    {
        var parentDirectory = Path.GetDirectoryName(installPath)!;
        var directory = IsInstallationDirectory(parentDirectory) ? parentDirectory : null;
        if (_sandbox is not null)
        {
            var loaded = await _sandbox.LoadAsync(installPath, signal);
            Track(loaded.Loaded);
            if (directory is not null)
                foreach (var package in loaded.Packages)
                    _installedDirectories[package] = directory;
            if (loaded.Packages.Count == 0)
            {
                var failures = loaded.Loaded.Skipped.Concat(_sandbox.Failures.Where(skip => skip.File == installPath));
                var reasons = string.Join("; ", failures.Select(skip => skip.Reason).Distinct(StringComparer.Ordinal));
                return reasons.Length == 0 ? "plugin load failed: no packages were registered" : $"plugin load failed: {reasons}";
            }
            return string.Join('\n', await ActivatePackagesAsync(loaded.Packages));
        }
        if (Path.GetExtension(installPath) is ".so" or ".dylib" || !RuntimeFeature.IsDynamicCodeSupported)
            return await ActivateNativeAsync(installPath);
        PluginLoadResult result;
        try
        {
            result = _host.TryLoad(installPath);
        }
        catch (BadImageFormatException)
        {
            return await ActivateNativeAsync(installPath);
        }
        catch (FileLoadException error)
        {
            return $"assembly load failed: {installPath}: {error.Message}";
        }
        if (result.Packages.Count == 0)
        {
            var reasons = string.Join("; ", result.Skipped.Select(skip => skip.Reason));
            return reasons.Length == 0 ? $"assembly has no DSH plugin: {installPath}" : $"plugin load skipped: {reasons}";
        }
        Track(new PluginScanResult(result.Packages.Select(package =>
            new ManagedPlugin(package, result.Context!, result.SharedDependencies, directory, installPath)).ToArray(), [], []));
        var messages = await ActivatePackagesAsync(result.Packages);
        foreach (var notice in _host.SharedPool?.DrainNotices() ?? [])
            messages.Add($"note: {notice}");
        return string.Join('\n', messages);
    }

    private async Task<string> ReloadCoreAsync(string package, string? artifactPath,
        SettingsCommit? commit, CancellationToken signal)
    {
        var trimmed = package.Trim();
        if (trimmed.Length == 0)
            return "usage: /plugins reload <package> [path]";
        _settings.Plugins = HarnessSettings.Load(_home).Plugins;
        var sourcePath = await ValidateReloadSourceAsync(trimmed, artifactPath, signal);
        if (sourcePath.Error is not null)
            return sourcePath.Error;
        if (sourcePath.Path is null && !IsKnown(trimmed))
            return $"plugin package not found: {trimmed}";
        var existingArtifact = _artifactPaths.GetValueOrDefault(trimmed);
        if (_sandbox is not null && sourcePath.Path is { } source && existingArtifact is not null
            && !string.Equals(existingArtifact, Path.GetFullPath(PluginInstall.ResolveInstallPath(source, _pluginsDirectory)),
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            return $"plugin {trimmed} reload requires the existing artifact file name: {Path.GetFileName(existingArtifact)}";
        if (_settings.Plugins.GetValueOrDefault(trimmed) is { Enabled: false } || !_settings.Plugins.ContainsKey(trimmed) && sourcePath.Path is not null)
            await PersistAsync(trimmed, existing => Toggled(existing, enabled: true), commit);
        var domain = CaptureDomain(trimmed);
        var previousDirectory = _installedDirectories.GetValueOrDefault(trimmed);
        var unloaded = await ReleaseDomainForMutationAsync(trimmed, domain, reclaimArtifact: true, signal);
        if (unloaded is not null)
            return unloaded;
        signal.ThrowIfCancellationRequested();
        var targetArtifact = sourcePath.Path is not null
            ? await InstallReloadArtifactAsync(trimmed, sourcePath.Path, previousDirectory)
            : (Path: existingArtifact, Error: (string?)null);
        if (targetArtifact.Error is not null)
            return targetArtifact.Error;
        if (targetArtifact.Path is { } path)
            return $"plugin {trimmed} reloaded; {await LoadAndActivateArtifactAsync(path, signal)}";
        if (_sandbox is not null)
            Track(await _sandbox.ScanAsync(signal));
        var messages = await ActivatePackagesAsync(domain);
        return messages.Count == 0 ? $"plugin {trimmed} reloaded" : $"plugin {trimmed} reloaded; {string.Join("; ", messages)}";
    }

    private async Task<(string? Path, string? Error)> ValidateReloadSourceAsync(string package, string? artifactPath, CancellationToken signal)
    {
        if (string.IsNullOrWhiteSpace(artifactPath))
            return (null, null);
        var fullPath = Path.GetFullPath(artifactPath.Trim());
        if (!File.Exists(fullPath))
            return (null, $"plugin file not found: {fullPath}");
        var inspected = _sandbox is not null
            ? await _sandbox.InspectPackagesAsync(fullPath, signal)
            : InspectLegacyPackages(fullPath);
        return inspected.Contains(package, StringComparer.Ordinal)
            ? (fullPath, null)
            : (null, $"plugin {package} was not found in {fullPath}");
    }

    private async Task<string?> ReleaseDomainForMutationAsync(string package, IReadOnlyList<string> domain,
        bool reclaimArtifact, CancellationToken signal)
    {
        var retained = $"plugin {package} change aborted: a previous load context is still referenced";
        if (reclaimArtifact && domain.Any(member => _leaked.GetValueOrDefault(member)?.IsAlive == true))
            return retained;
        if (_sandbox is not null && reclaimArtifact)
            await _sandbox.RetireDomainAsync(package, signal);
        foreach (var member in domain)
        {
            if (await StopActivationAsync(member, force: false))
            {
                _leaked[member] = null;
                return $"plugin {package} change aborted: previous activation did not settle (marked leaked)";
            }
            ClearSettledLeak(member);
        }
        if (!reclaimArtifact)
            return null;
        var collected = true;
        foreach (var member in domain)
        {
            var reclaim = IsManaged(member) || _nativePlugins.ContainsKey(member) || _artifactPaths.ContainsKey(member);
            var weak = ReleaseRegistration(member, reclaim: reclaim, removeRegistration: reclaim);
            if (weak is not null && !await VerifyCollectionAsync(member, weak))
                collected = false;
        }
        return collected ? null : retained;
    }

    private async Task<(string? Path, string? Error)> InstallReloadArtifactAsync(string package, string sourcePath, string? previousDirectory)
    {
        try
        {
            var installed = PluginInstall.Install(sourcePath, _pluginsDirectory, overwrite: true);
            var directory = Path.GetDirectoryName(installed)!;
            if (previousDirectory is not null && !PathsEqual(previousDirectory, directory))
                await UninstallInstalledAsync(package);
            _installedDirectories[package] = directory;
            return (installed, null);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return (null, $"plugin install failed: {error.Message}");
        }
    }

    private async Task SynchronizeCoreAsync(CancellationToken signal, bool discoverArtifacts)
    {
        var previous = _settings.Plugins;
        _settings.Plugins = HarnessSettings.Load(_home).Plugins;
        await StopUnavailablePackagesAsync(signal);
        if (!discoverArtifacts || _sandbox is null)
            await LoadKnownArtifactsAsync(previous, signal);
        if (discoverArtifacts)
            await LoadUntrackedArtifactsAsync(signal);
        foreach (var package in _host.Catalog.PackageNames.OrderBy(name => name, StringComparer.Ordinal).ToArray())
            if (_settings.Plugins.GetValueOrDefault(package)?.Enabled != false && _composition.Find(package) is null
                && !HasLeakedPackage(package))
                await ActivateAsync(package);
    }

    private async Task StopUnavailablePackagesAsync(CancellationToken signal)
    {
        var packages = _composition.Activations.Select(item => item.Name).Concat(_artifactPaths.Keys)
            .Distinct(StringComparer.Ordinal).ToArray();
        foreach (var package in packages)
        {
            signal.ThrowIfCancellationRequested();
            var disabled = _settings.Plugins.GetValueOrDefault(package) is { Enabled: false };
            var missing = _artifactPaths.TryGetValue(package, out var artifact) && !File.Exists(artifact);
            if (!disabled && !missing)
                continue;
            if (await StopActivationAsync(package, force: false))
            {
                _leaked[package] = null;
                continue;
            }
            ClearSettledLeak(package);
            if (!missing || HasLeakedPackage(package))
                continue;
            var weak = ReleaseRegistration(package, reclaim: true, removeRegistration: true);
            _artifactPaths.Remove(package);
            _installedDirectories.Remove(package);
            if (weak is not null)
                await VerifyCollectionAsync(package, weak);
        }
    }

    private async Task LoadKnownArtifactsAsync(IReadOnlyDictionary<string, PluginSetting> previous, CancellationToken signal)
    {
        var comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var reload = _artifactPaths.Where(entry => _settings.Plugins.GetValueOrDefault(entry.Key)?.Enabled != false
            && !HasLeakedPackage(entry.Key) && (!_host.Catalog.TryDescribe(entry.Key, out _)
                || _sandbox is not null && previous.GetValueOrDefault(entry.Key) is { Enabled: false }));
        foreach (var file in reload.Select(entry => entry.Value).Distinct(comparer).ToArray())
        {
            signal.ThrowIfCancellationRequested();
            if (_sandbox is null)
            {
                LoadUntrackedFile(file, Path.GetDirectoryName(file)!);
                continue;
            }
            try
            {
                Track((await _sandbox.LoadAsync(file, signal)).Loaded);
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                _composition.Root.Logger.Error("%s", $"plugin reload failed for {Path.GetFileName(file)}: {error.Message}");
            }
        }
    }

    private async Task LoadUntrackedArtifactsAsync(CancellationToken signal)
    {
        if (_sandbox is not null)
        {
            Track(await _sandbox.ScanAsync(signal));
            return;
        }
        if (!Directory.Exists(_pluginsDirectory))
            return;
        var tracked = new HashSet<string>(_artifactPaths.Values,
            OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        foreach (var subdirectory in Directory.EnumerateDirectories(_pluginsDirectory).OrderBy(path => path, StringComparer.Ordinal))
        {
            if (Path.GetFileName(subdirectory) == PluginHost.SharedPoolDirectoryName)
                continue;
            foreach (var file in Directory.EnumerateFiles(subdirectory).OrderBy(path => path, StringComparer.Ordinal))
            {
                var fullPath = Path.GetFullPath(file);
                if (IsLibraryPath(fullPath) && !tracked.Contains(fullPath))
                    LoadUntrackedFile(fullPath, subdirectory);
            }
        }
    }

    private void LoadUntrackedFile(string fullPath, string subdirectory)
    {
        if (Path.GetExtension(fullPath).Equals(".dll", StringComparison.OrdinalIgnoreCase) && RuntimeFeature.IsDynamicCodeSupported)
        {
            try
            {
                var result = _host.TryLoad(fullPath);
                if (result.Packages.Count > 0)
                {
                    var directory = IsInstallationDirectory(subdirectory) ? subdirectory : null;
                    Track(new PluginScanResult(result.Packages.Select(package =>
                        new ManagedPlugin(package, result.Context!, result.SharedDependencies, directory, fullPath)).ToArray(), [], []));
                }
                return;
            }
            catch (BadImageFormatException)
            {
            }
            catch (Exception)
            {
                return;
            }
        }
        if (_nativeBridge is null)
            return;
        var native = _host.LoadNativePlugin(fullPath, _nativeBridge);
        if (native.Succeeded)
            Track(new PluginScanResult([], [native.Plugin!], []));
    }

    private static bool PathsEqual(string left, string right)
        => string.Equals(Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    // With isolation disabled, discovery retains legacy in-process metadata execution, but never publishes it to the live catalog.
    private static IReadOnlyList<string> InspectLegacyPackages(string path)
    {
        if (Path.GetExtension(path) == ".dll" && RuntimeFeature.IsDynamicCodeSupported)
        {
            try
            {
                return PluginHost.InspectManagedPackages(path);
            }
            catch (BadImageFormatException)
            {
                // Windows native libraries also have a .dll extension.
            }
        }
        var native = NativePluginLibrary.TryLoad(path);
        if (!native.Succeeded)
            throw new InvalidOperationException($"native plugin inspection failed: {native.Reason}");
        using var plugin = native.Plugin!;
        return [plugin.Package];
    }

    private async Task<string> ActivateNativeAsync(string installPath)
    {
        if (_nativeBridge is null)
            return "native plugin bridge is not available in this build";
        var load = _host.LoadNativePlugin(installPath, _nativeBridge);
        if (!load.Succeeded)
            return $"native plugin load failed: {load.Reason}";
        var plugin = load.Plugin!;
        _nativePlugins[plugin.Package] = plugin;
        _artifactPaths[plugin.Package] = installPath;
        var parent = Path.GetDirectoryName(installPath)!;
        if (IsInstallationDirectory(parent))
            _installedDirectories[plugin.Package] = parent;
        return await ActivateAsync(plugin.Package);
    }

    private async Task<List<string>> ActivatePackagesAsync(IEnumerable<string> packages)
    {
        var messages = new List<string>();
        foreach (var package in packages)
            if (_settings.Plugins.GetValueOrDefault(package)?.Enabled != false)
                messages.Add(await ActivateAsync(package));
        return messages;
    }

    private async Task<string> ActivateAsync(string package)
    {
        if (_composition.Find(package) is { } existing)
            return $"plugin {package} is already active ({existing.State})";
        if (!_host.Catalog.TryCreateDefinition(package, out var definition))
            return $"plugin package not found: {package}";
        var setting = _settings.Plugins.GetValueOrDefault(package);
        var activation = await _composition.AddAsync(definition!, setting is { Parameters.Count: > 0 } ? setting.Parameters : null);
        return activation.State == ActivationState.Active
            ? $"plugin {package} activated"
            : $"plugin {package} failed to activate: {activation.Error}";
    }

    private string[] CaptureDomain(string package)
    {
        var members = new HashSet<string>(_sandbox?.DomainPackages(package) ?? [], StringComparer.Ordinal) { package };
        if (_artifactPaths.TryGetValue(package, out var artifact))
            members.UnionWith(_artifactPaths.Where(entry => PathsEqual(entry.Value, artifact)).Select(entry => entry.Key));
        var contexts = _loadedContexts.Where(entry => members.Contains(entry.Key)).Select(entry => entry.Value).ToHashSet();
        members.UnionWith(_loadedContexts.Where(entry => contexts.Contains(entry.Value)).Select(entry => entry.Key));
        return members.ToArray();
    }

    private async Task<string> RebuildDomainAsync(string package, IReadOnlyList<string> domain, bool force)
    {
        await _sandbox!.RetireDomainAsync(package);
        var leaked = false;
        foreach (var member in domain)
        {
            var failed = await StopActivationAsync(member, force);
            if (failed)
            {
                _leaked[member] = null;
                leaked = true;
            }
            else
                ClearSettledLeak(member);
        }
        if (leaked)
            return Removed(package, leaked: true) + "; domain was not restarted because old activations did not settle";
        foreach (var member in domain)
        {
            var weak = ReleaseRegistration(member, reclaim: true, removeRegistration: true);
            if (weak is not null)
                await VerifyCollectionAsync(member, weak);
        }
        if (domain.Any(HasLeakedPackage))
            return $"plugin {package} domain was not restarted because a previous load context is still referenced";
        Track(await _sandbox.ScanAsync());
        foreach (var member in domain)
            if (_settings.Plugins.GetValueOrDefault(member) is { Enabled: false } && !IsHostRole(member))
                _host.Catalog.Remove(member);
        var messages = await ActivatePackagesAsync(domain);
        return messages.Count == 0 ? Removed(package, leaked: false) : string.Join(';', messages);
    }

    private void Track(PluginScanResult loaded)
    {
        foreach (var entry in loaded.Managed)
        {
            _loadedContexts[entry.Package] = entry.Context;
            if (entry.SharedDependencies.Count > 0)
                _sharedDependencies[entry.Package] = entry.SharedDependencies;
            if (entry.ArtifactPath is { } artifactPath)
                _artifactPaths[entry.Package] = artifactPath;
            if (entry.InstallDirectory is { } directory && IsInstallationDirectory(directory))
                _installedDirectories[entry.Package] = directory;
        }
        foreach (var plugin in loaded.Native)
        {
            _nativePlugins[plugin.Package] = plugin;
            if (plugin is NativePluginLibrary library)
            {
                _artifactPaths[plugin.Package] = library.Context.SourcePath;
                if (Path.GetDirectoryName(library.Context.SourcePath) is { } directory && IsInstallationDirectory(directory))
                    _installedDirectories[plugin.Package] = directory;
            }
        }
        if (_sandbox is not null)
        {
            foreach (var package in _host.Catalog.PackageNames)
            {
                if (_sandbox.ArtifactPath(package) is not { } path)
                    continue;
                _artifactPaths[package] = path;
                if (Path.GetDirectoryName(path) is { } directory && IsInstallationDirectory(directory))
                    _installedDirectories[package] = directory;
            }
        }
    }

    private bool IsInstallationDirectory(string directory)
        => string.Equals(Path.GetDirectoryName(Path.GetFullPath(directory)), Path.GetFullPath(_pluginsDirectory).TrimEnd(Path.DirectorySeparatorChar),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private async Task<(WeakReference? Weak, bool Leaked)> UnloadAsync(string package, bool force, bool reclaim, bool removeRegistration)
    {
        var leaked = await StopActivationAsync(package, force);
        return (leaked ? null : ReleaseRegistration(package, reclaim, removeRegistration), leaked);
    }

    private async Task<bool> StopActivationAsync(string package, bool force)
    {
        var active = _composition.Root.Scheduler.Find(package) is not null;
        var activation = active ? await _composition.Root.Scheduler.UnloadAsync(package, force: force) : null;
        var leaked = active && activation?.State != ActivationState.Disposed;
        Release(ref activation);
        await _composition.Root.Scheduler.SettleAsync();
        return leaked;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private WeakReference? ReleaseRegistration(string package, bool reclaim, bool removeRegistration)
    {
        if (reclaim && _loadedContexts.Remove(package, out var context))
        {
            _host.Catalog.Remove(package);
            _sharedDependencies.Remove(package, out var shared);
            // One assembly can register several packages; unload its ALC and shared lease only after the last registration is gone.
            if (!_loadedContexts.Values.Contains(context))
            {
                if (shared is not null)
                    _host.SharedPool?.Release(shared);
                var weak = PluginUnloader.Unload(context);
                Release(ref context);
                return weak;
            }
        }
        if (removeRegistration && _nativePlugins.Remove(package, out var native))
        {
            _host.Catalog.Remove(package);
            native.Dispose();
        }
        if (removeRegistration && _sandbox is not null
            && !IsHostRole(package))
            _host.Catalog.Remove(package);
        return null;
    }

    private static void Release<T>(ref T? value) where T : class => value = null;

    private bool IsKnown(string package)
        => _composition.Root.Scheduler.Find(package) is not null
            || _host.Catalog.PackageNames.Contains(package, StringComparer.Ordinal)
            || _installedDirectories.ContainsKey(package)
            || _settings.Plugins.ContainsKey(package);

    private bool IsManaged(string package)
        => Descriptor(package)?.Form == PluginForm.ManagedAssembly;

    private PluginDescriptor? Descriptor(string package)
        => _host.Catalog.TryDescribe(package, out var descriptor) ? descriptor : null;

    private PluginForm ExecutionForm(string package)
        => Descriptor(package)?.Form ?? _sandbox?.Form(package) ?? PluginForm.ManagedAssembly;

    private bool IsHostRole(string package)
        => PluginRoleResolver.Resolve(package, _settings.Plugins.GetValueOrDefault(package), ExecutionForm(package)) == PluginExecution.Host;

    private static string FormName(PluginDescriptor descriptor)
    {
        var form = descriptor.Form switch
        {
            PluginForm.CompiledIn => "compiled-in",
            PluginForm.ManagedAssembly => "managed-assembly",
            _ => "native-library",
        };
        return descriptor.Capabilities.HasFlag(PluginCapabilities.Unload) ? $"{form}, reclaimable" : form;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private async Task<bool> VerifyCollectionAsync(string package, WeakReference weak)
    {
        var (collected, report) = await Task.Run(() =>
        {
            var success = PluginUnloader.WaitForCollection(weak, out var detail, maxRounds: CollectionGcRounds);
            return (success, detail);
        });
        if (collected)
        {
            _leaked.TryRemove(package, out _);
            return true;
        }
        _leaked[package] = weak;
        _composition.Root.Logger.Error("%s", $"plugin {package} load context could not be collected: {report}");
        return false;
    }

    private bool HasLeakedPackage(string package)
        => _leaked.TryGetValue(package, out var context) && context?.IsAlive != false;

    private void ClearSettledLeak(string package)
    {
        if (_leaked.GetValueOrDefault(package)?.IsAlive != true)
            _leaked.TryRemove(package, out _);
    }

    private async Task PersistAsync(string package, Func<PluginSetting?, PluginSetting> update,
        SettingsCommit? commit)
    {
        await HarnessSettings.UpdatePluginAsync(_home, package, update, commit);
        _settings.Plugins = HarnessSettings.Load(_home).Plugins;
    }

    private async Task PersistAsync(IReadOnlyList<string> packages, Func<PluginSetting?, PluginSetting> update,
        SettingsCommit? commit)
    {
        await HarnessSettings.UpdatePluginsAsync(_home, packages, update, commit);
        _settings.Plugins = HarnessSettings.Load(_home).Plugins;
    }

    private static PluginSetting Toggled(PluginSetting? existing, bool enabled)
        => (existing ?? new PluginSetting()) with { Enabled = enabled };

    public void Dispose()
    {
        if (_sandbox is not null)
            foreach (var package in _host.Catalog.PackageNames.ToArray())
                if (!IsHostRole(package))
                    _host.Catalog.Remove(package);
        foreach (var group in _loadedContexts.GroupBy(entry => entry.Value))
        {
            foreach (var entry in group)
                _host.Catalog.Remove(entry.Key);
            if (_sharedDependencies.TryGetValue(group.First().Key, out var shared))
                _host.SharedPool?.Release(shared);
            if (group.Key.IsCollectible)
                group.Key.Unload();
        }
        _loadedContexts.Clear();
        _sharedDependencies.Clear();
        foreach (var entry in _nativePlugins)
        {
            _host.Catalog.Remove(entry.Key);
            entry.Value.Dispose();
        }
        _nativePlugins.Clear();
        _installedDirectories.Clear();
        _artifactPaths.Clear();
        _mutation.Dispose();
    }

    private static string Removed(string package, bool leaked)
        => leaked
            ? $"plugin {package} removed, but settlement timed out (effects may still be running; marked leaked)"
            : $"plugin {package} removed";
}
