using System.Collections.Concurrent;
using System.Text.Json;
using Dsh.Core;
using Dsh.Plugins;
using Dsh.Runtime;
using Dsh.Sandbox;

namespace Dsh.Boot;

/** Owns one co-trusted worker; plugins share memory and caller identity. */
internal sealed class PluginSandboxSupervisor(Context context, HarnessHome home, HarnessSettings settings,
    PluginCapabilityFactory capabilities, string pluginsDirectory) : IDisposable
{
    private static readonly JsonElement EmptyConfiguration = JsonSerializer.SerializeToElement(
        new Dictionary<string, object?>(), PluginConfigurationJsonContext.Default.Object);
    private readonly Lock _sync = new();
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private readonly Dictionary<string, PluginSandboxSource> _sources = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _failures = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _shutdown = new();
    private readonly string _pluginsDirectory = Path.TrimEndingDirectorySeparator(SandboxFileAccess.Canonicalize(pluginsDirectory));
    private Domain? _domain;
    private bool _disposed;

    internal sealed class Domain
    {
        internal string Directory { get; } = System.IO.Directory.CreateTempSubdirectory("dsh-plugin-worker-").FullName;
        internal HashSet<string> Runtime { get; } = new(SandboxRuntimePaths.ForCurrentHost(), PluginSandboxLaunch.PathComparer);
        internal ConcurrentDictionary<string, Binding> Bindings { get; } = new(StringComparer.Ordinal);
        internal HashSet<string> Members { get; } = new(StringComparer.Ordinal);
        internal CancellationTokenSource Cancellation { get; } = new();
        internal Dictionary<string, string> Artifacts { get; } = new(PluginSandboxLaunch.PathComparer);
        internal PluginSandboxConnection? Connection;
        internal Task? Retirement;
        internal volatile string? Failure;
        internal volatile bool Retired;
    }

    internal sealed class Binding(string package, IPluginHost host)
    {
        internal string Package { get; } = package;
        internal IPluginHost Host { get; } = host;
        internal volatile bool Loading = true;
        private int _disposed;
        internal void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
                (Host as IDisposable)?.Dispose();
        }
    }

    public IReadOnlyList<PluginSkip> Failures
    {
        get { lock (_sync) return _failures.Select(pair => new PluginSkip(pair.Key, pair.Value)).ToArray(); }
    }

    public bool IsFaulted(string package)
    {
        lock (_sync) return _failures.ContainsKey(package);
    }

    public PluginSandboxSource? Source(string package)
    {
        lock (_sync) return _sources.GetValueOrDefault(package);
    }

    public IReadOnlyList<string> DomainPackages(string package)
    {
        lock (_sync)
            return _domain is { } domain && (domain.Members.Contains(package) || domain.Failure is not null)
                ? domain.Members.Append(package).Distinct(StringComparer.Ordinal).ToArray() : [package];
    }

    public void Register(PluginHost host, PluginSandboxSource source)
    {
        var package = source.Descriptor.Package;
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_domain?.Bindings.ContainsKey(package) == true)
                throw Failure("PLUGIN_SANDBOX_RESTART_REQUIRED", "Retire the shared worker before replacing an active source.");
            _sources[package] = source;
            host.Catalog.Remove(package);
            host.Catalog.Register(source.Descriptor, () => new Proxy(this, package));
        }
    }

    private HarnessSettings CurrentSettings()
        => File.Exists(home.SettingsFile) ? HarnessSettings.LoadForReview(home) : settings;

    private void DemandExecution(string package, HarnessSettings active)
    {
        var configured = active.Plugins.GetValueOrDefault(package);
        var form = Source(package)?.Descriptor.Form ?? PluginForm.ManagedAssembly;
        if (active.Sandbox?.Enabled != true || configured?.Enabled == false
            || PluginSetting.EffectiveExecution(configured, form) != PluginExecution.Sandbox)
            throw Failure("PLUGIN_SANDBOX_PROFILE_CHANGED", "The isolated plugin's active execution profile changed; retire the shared worker.");
    }

    internal void CheckAuthority(Domain domain, string package)
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (domain.Retired || domain.Failure is not null || !domain.Bindings.ContainsKey(package))
                throw Failure("PLUGIN_SANDBOX_UNAVAILABLE", "The shared plugin worker is no longer available.");
        }
        try
        {
            DemandExecution(package, CurrentSettings());
        }
        catch
        {
            Fault(domain, "The shared worker's active settings profile changed; explicit retirement and reload are required.");
            throw;
        }
    }

    internal void Fault(Domain domain, string reason)
    {
        Binding[] bindings;
        lock (_sync)
        {
            if (domain.Retired || domain.Failure is not null)
                return;
            domain.Failure = reason;
            bindings = domain.Bindings.Values.ToArray();
            foreach (var package in domain.Members)
                _failures[package] = reason;
            domain.Retirement ??= Task.Run(() => RetireCoreAsync(domain));
        }
        domain.Connection?.Abort();
        context.LoggerFor("plugin-sandbox").Warn("%s", reason);
        foreach (var binding in bindings)
            binding.Dispose();
    }

    internal void Diagnostic(string message) => context.LoggerFor("plugin-sandbox").Warn("%s", message);

    // The lifecycle semaphore serializes staging, launch, inspection and activation.
    private async Task<Domain> SharedAsync(CancellationToken cancellationToken)
    {
        Domain domain;
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            domain = _domain ??= new Domain();
            if (domain.Retired || domain.Failure is not null)
                throw Failure("PLUGIN_SANDBOX_FAULTED", "The shared worker failed; explicitly retire and reload it.");
        }
        if (domain.Connection is null)
        {
            using var operation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, domain.Cancellation.Token, _shutdown.Token);
            try
            {
                var connection = await PluginSandboxConnection.StartAsync(this, domain, operation.Token).ConfigureAwait(false);
                domain.Connection = connection;
                lock (_sync)
                    if (_disposed || domain.Retired)
                    {
                        connection.Abort();
                        throw new ObjectDisposedException(nameof(PluginSandboxSupervisor));
                    }
            }
            catch
            {
                Fault(domain, "The shared plugin worker could not start.");
                throw;
            }
        }
        return domain;
    }

    private string? Stage(Domain domain, PluginSandboxSource source, out string? stagingDirectory)
    {
        stagingDirectory = null;
        if (source.ArtifactPath is not { } artifact)
            return null;
        var original = SandboxFileAccess.Canonicalize(artifact);
        SandboxFileAccess.DemandOrdinaryFile(original, allowMissing: false);
        stagingDirectory = Path.Combine(domain.Directory, Guid.NewGuid().ToString("N"));
        try
        {
            var sourceDirectory = Path.GetDirectoryName(original)!;
            var installed = PluginSandboxLaunch.PathComparer.Equals(Path.GetDirectoryName(sourceDirectory), _pluginsDirectory);
            var destination = Path.Combine(stagingDirectory, "plugin");
            System.IO.Directory.CreateDirectory(destination);
            if (installed)
                CopyDirectory(sourceDirectory, destination);
            else
                foreach (var file in System.IO.Directory.EnumerateFiles(sourceDirectory))
                    if (IsCodeFile(file))
                        CopyFile(file, Path.Combine(destination, Path.GetFileName(file)));
            var localShared = Path.Combine(sourceDirectory, ".shared");
            if (!installed && System.IO.Directory.Exists(localShared))
                CopyDirectory(localShared, Path.Combine(destination, ".shared"));
            if (installed && Path.GetDirectoryName(sourceDirectory) is { } parent
                && System.IO.Directory.Exists(Path.Combine(parent, ".shared")))
                CopyDirectory(Path.Combine(parent, ".shared"), Path.Combine(stagingDirectory, ".shared"));
            return Path.Combine(destination, Path.GetFileName(original));
        }
        catch
        {
            System.IO.Directory.Delete(stagingDirectory, recursive: true);
            throw;
        }
    }

    private static bool IsCodeFile(string path)
    {
        var name = Path.GetFileName(path);
        return name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith(".so", StringComparison.OrdinalIgnoreCase)
            || name.Contains(".so.", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith(".dylib", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith(".deps.json", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith(".runtimeconfig.json", StringComparison.OrdinalIgnoreCase);
    }

    private static void CopyFile(string source, string destination)
    {
        SandboxFileAccess.DemandOrdinaryFile(source, allowMissing: false);
        System.IO.Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        File.Copy(source, destination);
        var attributes = File.GetAttributes(destination);
        if ((attributes & FileAttributes.ReadOnly) != 0)
            File.SetAttributes(destination, attributes & ~FileAttributes.ReadOnly);
    }

    private static void CopyDirectory(string source, string destination)
    {
        if ((File.GetAttributes(source) & FileAttributes.ReparsePoint) != 0)
            throw new NotSupportedException("Plugin code directory roots must be ordinary directories.");
        System.IO.Directory.CreateDirectory(destination);
        foreach (var path in System.IO.Directory.EnumerateFileSystemEntries(source))
        {
            var attributes = File.GetAttributes(path);
            if ((attributes & FileAttributes.ReparsePoint) != 0)
                continue;
            var target = Path.Combine(destination, Path.GetFileName(path));
            if ((attributes & FileAttributes.Directory) != 0)
                CopyDirectory(path, target);
            else
                CopyFile(path, target);
        }
    }

    private async Task<IDisposable?> ActivateAsync(string package, Context pluginContext, object? configuration,
        CancellationToken cancellationToken)
    {
        await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        Domain? domain = null;
        try
        {
            DemandExecution(package, CurrentSettings());
            PluginSandboxSource source;
            lock (_sync)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                if (_failures.ContainsKey(package))
                    throw Failure("PLUGIN_SANDBOX_FAULTED", "The isolated plugin failed; explicitly retire and reload the shared worker.");
                source = _sources.GetValueOrDefault(package)
                    ?? throw Failure("PLUGIN_SANDBOX_SOURCE_MISSING", "The isolated plugin has no registered source.");
            }
            domain = await SharedAsync(cancellationToken).ConfigureAwait(false);
            var artifact = source.ArtifactPath is { } original && domain.Artifacts.TryGetValue(original, out var existing)
                ? existing : Stage(domain, source, out _);
            if (source.ArtifactPath is { } path && artifact is not null)
                domain.Artifacts[path] = artifact;
            var binding = new Binding(package, capabilities.Create(package, pluginContext));
            lock (_sync)
            {
                if (!domain.Bindings.TryAdd(package, binding))
                {
                    binding.Dispose();
                    throw Failure("PLUGIN_SANDBOX_ALREADY_ACTIVE", "The isolated plugin is already active.");
                }
                domain.Members.Add(package);
            }
            CheckAuthority(domain, package);
            var configurationJson = configuration is null ? EmptyConfiguration
                : JsonSerializer.SerializeToElement(configuration, PluginConfigurationJsonContext.Default.Object);
            using var initialization = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, domain.Cancellation.Token, _shutdown.Token);
            initialization.CancelAfter(PluginSandboxLaunch.InitializationBudget);
            var loaded = await domain.Connection!.RequestAsync(PluginSandboxMethods.Load,
                new PluginSandboxLoadRequest(artifact, package, configurationJson),
                PluginSandboxJsonContext.Default.PluginSandboxLoadRequest, initialization.Token).ConfigureAwait(false);
            if (loaded?.Deserialize(PluginSandboxJsonContext.Default.PluginSandboxAck)?.Success != true)
                throw new IOException("The isolated plugin did not acknowledge successful initialization.");
            CheckAuthority(domain, package);
            binding.Loading = false;
            return new Lease(this, package, domain);
        }
        catch
        {
            if (domain is not null)
            {
                Fault(domain, cancellationToken.IsCancellationRequested
                    ? "The shared plugin worker was terminated by user cancellation."
                    : "An isolated plugin could not initialize; explicit retirement and reload are required.");
            }
            lock (_sync)
                _failures[package] = domain?.Failure ?? "The isolated worker could not be started.";
            throw;
        }
        finally { _lifecycle.Release(); }
    }

    public async Task<PluginSandboxInspection> InspectAsync(string? artifactPath, string? package,
        CancellationToken cancellationToken = default)
    {
        await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        string? staging = null;
        Domain? domain = null;
        try
        {
            domain = await SharedAsync(cancellationToken).ConfigureAwait(false);
            var source = new PluginSandboxSource(PluginDescriptor.For(package ?? "inspection", PluginSandboxLaunch.InspectForm(artifactPath)), artifactPath);
            var staged = Stage(domain, source, out staging);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, domain.Cancellation.Token, _shutdown.Token);
            timeout.CancelAfter(PluginSandboxLaunch.InspectionBudget);
            var result = await domain.Connection!.RequestAsync(PluginSandboxMethods.Inspect,
                new PluginSandboxInspectRequest(staged, package), PluginSandboxJsonContext.Default.PluginSandboxInspectRequest,
                timeout.Token).ConfigureAwait(false);
            var inspection = result?.Deserialize(PluginSandboxJsonContext.Default.PluginSandboxInspection)
                ?? throw new IOException("The isolated inspection returned no metadata.");
            if (!inspection.Released)
                Fault(domain, "The isolated inspection could not release its code; explicit retirement and reload are required.");
            return inspection;
        }
        finally
        {
            try
            {
                if (domain?.Failure is null && staging is not null && System.IO.Directory.Exists(staging))
                    System.IO.Directory.Delete(staging, recursive: true);
            }
            finally { _lifecycle.Release(); }
        }
    }

    public async Task RetireDomainAsync(string package, CancellationToken cancellationToken = default)
    {
        Domain domain;
        Task retirement;
        lock (_sync)
        {
            if (_domain is not { } current
                || current.Members.Count != 0 && !current.Members.Contains(package) && current.Failure is null)
            {
                _failures.Remove(package);
                return;
            }
            domain = current;
            domain.Retired = true;
            retirement = domain.Retirement ??= Task.Run(() => RetireCoreAsync(domain));
        }
        await retirement.WaitAsync(PluginSandboxLaunch.ShutdownBudget).ConfigureAwait(false);
        lock (_sync)
            if (ReferenceEquals(_domain, domain))
            {
                _domain = null;
                _failures.Clear();
            }
        cancellationToken.ThrowIfCancellationRequested();
    }

    private async Task RetireCoreAsync(Domain domain)
    {
        domain.Cancellation.Cancel();
        domain.Connection?.Abort();
        await _lifecycle.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            foreach (var binding in domain.Bindings.Values)
                binding.Dispose();
            if (domain.Connection is not null)
                await domain.Connection.ShutdownAsync().ConfigureAwait(false);
            System.IO.Directory.Delete(domain.Directory, recursive: true);
        }
        finally { _lifecycle.Release(); }
    }

    public void Dispose()
    {
        Task? retirement = null;
        lock (_sync)
        {
            if (_disposed)
                return;
            _disposed = true;
            if (_domain is { } domain)
            {
                domain.Retired = true;
                retirement = domain.Retirement ??= Task.Run(() => RetireCoreAsync(domain));
            }
        }
        _shutdown.Cancel();
        retirement?.WaitAsync(PluginSandboxLaunch.ShutdownBudget).GetAwaiter().GetResult();
    }

    internal static RuntimeException Failure(string code, string message) => new(code, message);

    private async Task ReleasePackageAsync(string package, Domain domain)
    {
        await _lifecycle.WaitAsync().ConfigureAwait(false);
        try
        {
            if (domain.Retired || domain.Failure is not null)
                return;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(domain.Cancellation.Token, _shutdown.Token);
            timeout.CancelAfter(PluginSandboxLaunch.ShutdownBudget);
            var result = await domain.Connection!.RequestAsync(PluginSandboxMethods.Unload,
                new PluginSandboxUnloadRequest(package), PluginSandboxJsonContext.Default.PluginSandboxUnloadRequest,
                timeout.Token).ConfigureAwait(false);
            if (result?.Deserialize(PluginSandboxJsonContext.Default.PluginSandboxAck)?.Success != true)
                throw new IOException("The isolated plugin did not acknowledge unloading.");
            domain.Connection.ReleasePackage(package);
            if (domain.Bindings.TryRemove(package, out var binding))
                binding.Dispose();
            lock (_sync)
                domain.Members.Remove(package);
        }
        catch
        {
            Fault(domain, "An isolated plugin could not unload; explicit retirement and reload are required.");
            throw;
        }
        finally { _lifecycle.Release(); }
    }

    private sealed class Proxy(PluginSandboxSupervisor owner, string package) : IDshPlugin
    {
        public string[] Inject => ["tools", IPluginHostFactory.ServiceName];
        public Task<IDisposable?> ApplyAsync(Context ctx, object? config, CancellationToken cancellationToken = default)
            => owner.ActivateAsync(package, ctx, config, cancellationToken);
    }

    private sealed class Lease(PluginSandboxSupervisor owner, string package, Domain domain) : IDisposable
    {
        private int _disposed;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0 || domain.Retired)
                return;
            owner.ReleasePackageAsync(package, domain).GetAwaiter().GetResult();
        }
    }
}
