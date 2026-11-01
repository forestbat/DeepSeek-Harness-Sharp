using System.Collections.Concurrent;
using System.Text.Json;
using Dsh.Core;
using Dsh.Plugins;
using Dsh.Runtime;

namespace Dsh.Boot;

/** Owns OS trust domains. A domain is co-trusted memory, not per-plugin address-space isolation. */
internal sealed class PluginSandboxSupervisor(Context context, HarnessHome home, HarnessSettings settings,
    PluginCapabilityFactory capabilities) : IDisposable
{
    private readonly Lock _sync = new();
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private readonly Dictionary<string, PluginSandboxSource> _sources = new(StringComparer.Ordinal);
    private readonly Dictionary<DomainKey, Domain> _domains = [];
    private readonly Dictionary<string, string> _failures = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _shutdown = new();
    private readonly HashSet<TaskCompletionSource> _inspectionCompletions = [];
    private int _inspections;
    private bool _disposed;

    internal sealed record DomainKey(string Name, bool Explicit, PluginPermissions Permissions);

    internal sealed class Domain(DomainKey key, HashSet<string> code, IReadOnlyList<string> members)
    {
        internal DomainKey Key { get; } = key;
        internal HashSet<string> Code { get; } = code;
        internal ConcurrentDictionary<string, Binding> Bindings { get; } = new(StringComparer.Ordinal);
        internal HashSet<string> Members { get; } = new(members, StringComparer.Ordinal);
        internal CancellationTokenSource Cancellation { get; } = new();
        internal PluginSandboxConnection? Connection;
        internal Task? Retirement;
        internal volatile string? Failure;
        internal volatile bool Retired;
    }

    internal sealed class Binding(string package, Context pluginContext, IPluginHost host)
    {
        internal string Package { get; } = package;
        internal Context Context { get; } = pluginContext;
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
        lock (_sync)
            return _failures.ContainsKey(package);
    }

    public PluginSandboxSource? Source(string package)
    {
        lock (_sync)
            return _sources.GetValueOrDefault(package);
    }

    public IReadOnlyList<string> DomainPackages(string package)
    {
        lock (_sync)
        {
            var domain = _domains.Values.FirstOrDefault(value => value.Members.Contains(package, StringComparer.Ordinal));
            return domain?.Members.ToArray() ?? [package];
        }
    }

    public void Register(PluginHost host, PluginSandboxSource source)
    {
        var package = source.Descriptor.Package;
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_domains.Values.Any(domain => domain.Members.Contains(package)))
                throw Failure("PLUGIN_SANDBOX_RESTART_REQUIRED", "Retire the old trust domain before replacing its source.");
            _sources[package] = source;
            host.Catalog.Remove(package);
            host.Catalog.Register(source.Descriptor, () => new Proxy(this, package));
        }
    }

    private HarnessSettings CurrentSettings()
        => File.Exists(home.SettingsFile) ? HarnessSettings.LoadForReview(home) : settings;

    private static DomainKey Key(string package, HarnessSettings active)
    {
        var configured = active.Plugins.GetValueOrDefault(package);
        if (active.Sandbox?.Enabled != true || configured?.Enabled == false || configured?.Execution == PluginExecution.Host)
            throw Failure("PLUGIN_SANDBOX_PROFILE_CHANGED", "The isolated plugin's active execution profile changed; retire its trust domain.");
        return new DomainKey(configured?.TrustDomain ?? package, configured?.TrustDomain is not null,
            configured?.Permissions ?? PluginPermissions.AllAllowed);
    }

    internal void CheckAuthority(Domain domain, string package)
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (domain.Retired || domain.Failure is not null || !domain.Bindings.ContainsKey(package))
                throw Failure("PLUGIN_SANDBOX_UNAVAILABLE", "The isolated plugin's trust domain is no longer available.");
        }
        HarnessSettings active;
        try { active = CurrentSettings(); }
        catch
        {
            Fault(domain, "The trust domain has no valid active settings profile; explicit retirement and reload are required.");
            throw;
        }
        string[] packages;
        lock (_sync) packages = domain.Bindings.Keys.ToArray();
        foreach (var member in packages)
        {
            DomainKey key;
            try { key = Key(member, active); }
            catch
            {
                Fault(domain, "The trust domain's active execution profile changed; explicit retirement and reload are required.");
                throw;
            }
            if (key != domain.Key)
            {
                Fault(domain, "The trust domain's active execution profile changed; explicit retirement and reload are required.");
                throw Failure("PLUGIN_SANDBOX_PROFILE_CHANGED", "The isolated plugin's trust or permission profile changed.");
            }
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
        }
        domain.Connection?.Abort();
        context.LoggerFor("plugin-sandbox").Warn("%s", reason);
        foreach (var binding in bindings)
            binding.Dispose();
    }

    private async Task<IDisposable?> ActivateAsync(string package, Context pluginContext, object? configuration,
        CancellationToken cancellationToken)
    {
        await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        Domain? domain = null;
        CancellationTokenSource? operation = null;
        try
        {
            PluginSandboxSource source;
            var active = CurrentSettings();
            var key = Key(package, active);
            lock (_sync)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                if (_failures.ContainsKey(package))
                    throw Failure("PLUGIN_SANDBOX_FAULTED", "The isolated plugin failed; explicitly retire and reload its trust domain.");
                source = _sources.GetValueOrDefault(package)
                    ?? throw Failure("PLUGIN_SANDBOX_SOURCE_MISSING", "The isolated plugin has no registered source.");
                domain = _domains.Values.FirstOrDefault(value => value.Members.Contains(package) && value.Key != key);
                if (domain is not null)
                    throw Failure("PLUGIN_SANDBOX_RESTART_REQUIRED", "Retire the old trust domain before activating a changed trust or permission profile.");
                _domains.TryGetValue(key, out domain);
            }
            if (domain is null)
            {
                PluginSandboxSource[] sources;
                lock (_sync)
                {
                    ObjectDisposedException.ThrowIf(_disposed, this);
                    if (_domains.Count + _inspections >= PluginSandboxMethods.MaximumWorkers)
                        throw Failure("PLUGIN_SANDBOX_WORKER_LIMIT", "The isolated worker capacity is exhausted.");
                    sources = _sources.Values.Where(value =>
                        active.Plugins.GetValueOrDefault(value.Descriptor.Package)?.Enabled != false
                        && active.Plugins.GetValueOrDefault(value.Descriptor.Package)?.Execution != PluginExecution.Host
                        && Key(value.Descriptor.Package, active) == key).ToArray();
                    domain = new Domain(key, PluginSandboxLaunch.CodeClosure(sources), sources.Select(value => value.Descriptor.Package).ToArray());
                    _domains.Add(key, domain);
                }
                operation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, domain.Cancellation.Token, _shutdown.Token);
                var captured = domain;
                var connection = await PluginSandboxConnection.StartAsync(
                    this, captured, capabilities, operation.Token).ConfigureAwait(false);
                domain.Connection = connection;
                lock (_sync)
                    if (_disposed || captured.Retired)
                    {
                        connection.Abort();
                        throw new ObjectDisposedException(nameof(PluginSandboxSupervisor));
                    }
            }
            operation ??= CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, domain.Cancellation.Token, _shutdown.Token);
            if (domain.Failure is not null || domain.Retired)
                throw Failure("PLUGIN_SANDBOX_FAULTED", "The isolated trust domain failed; explicit retirement is required.");
            var required = PluginSandboxLaunch.CodeClosure([source]);
            if (!required.IsSubsetOf(domain.Code))
                throw Failure("PLUGIN_SANDBOX_RESTART_REQUIRED", "The new artifact is outside the running worker's immutable code namespace. Retire and reload the entire trust domain.");
            var binding = new Binding(package, pluginContext, capabilities.Create(package, pluginContext));
            lock (_sync)
            {
                if (domain.Bindings.ContainsKey(package))
                {
                    binding.Dispose();
                    throw Failure("PLUGIN_SANDBOX_ALREADY_ACTIVE", "The isolated plugin is already active.");
                }
                domain.Bindings.TryAdd(package, binding);
                domain.Members.Add(package);
            }
            CheckAuthority(domain, package);
            var bytes = PluginConfigurationJson.Write(configuration);
            using var document = JsonDocument.Parse(bytes.Length == 0 ? "{}" : bytes);
            using var initialization = CancellationTokenSource.CreateLinkedTokenSource(operation.Token);
            initialization.CancelAfter(PluginSandboxLaunch.InitializationBudget);
            var loaded = await domain.Connection!.RequestAsync(PluginSandboxMethods.Load,
                new PluginSandboxLoadRequest(source.ArtifactPath, package, document.RootElement.Clone()),
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
                Fault(domain, cancellationToken.IsCancellationRequested
                    ? "The isolated trust domain was terminated by user cancellation."
                    : "The isolated trust domain could not initialize safely; explicit retirement and reload are required.");
            lock (_sync)
                _failures[package] = domain?.Failure ?? "The isolated worker could not be started safely.";
            throw;
        }
        finally
        {
            operation?.Dispose();
            _lifecycle.Release();
        }
    }

    public async Task<PluginSandboxInspection> InspectAsync(string? artifactPath, string? package,
        CancellationToken cancellationToken = default)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_domains.Count + _inspections >= PluginSandboxMethods.MaximumWorkers)
                throw Failure("PLUGIN_SANDBOX_WORKER_LIMIT", "The isolated worker capacity is exhausted.");
            _inspections++;
            _inspectionCompletions.Add(completion);
        }
        PluginSandboxConnection? connection = null;
        try
        {
            var form = PluginSandboxLaunch.InspectForm(artifactPath);
            var source = new PluginSandboxSource(PluginDescriptor.For(package ?? "inspection", form), artifactPath);
            var domain = new Domain(new DomainKey("inspection:" + Guid.NewGuid().ToString("N"), false,
                new PluginPermissions { Read = false, Write = false, Network = false }), PluginSandboxLaunch.CodeClosure([source]), []);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _shutdown.Token);
            timeout.CancelAfter(PluginSandboxLaunch.InspectionBudget);
            connection = await PluginSandboxConnection.StartAsync(this, domain, capabilities, timeout.Token).ConfigureAwait(false);
            var result = await connection.RequestAsync(PluginSandboxMethods.Inspect,
                new PluginSandboxInspectRequest(artifactPath, package), PluginSandboxJsonContext.Default.PluginSandboxInspectRequest,
                timeout.Token).ConfigureAwait(false);
            return result?.Deserialize(PluginSandboxJsonContext.Default.PluginSandboxInspection)
                ?? throw new IOException("The isolated inspection returned no metadata.");
        }
        finally
        {
            try { if (connection is not null) await connection.DisposeAsync().ConfigureAwait(false); }
            finally
            {
                lock (_sync)
                {
                    _inspections--;
                    _inspectionCompletions.Remove(completion);
                }
                completion.TrySetResult();
            }
        }
    }

    public async Task RetireAsync(string package, CancellationToken cancellationToken = default)
    {
        Task? retirement;
        lock (_sync)
        {
            var domain = _domains.Values.FirstOrDefault(value => value.Members.Contains(package, StringComparer.Ordinal));
            if (domain is null)
            {
                _failures.Remove(package);
                return;
            }
            domain.Retired = true;
            retirement = domain.Retirement ??= Task.Run(() => RetireCoreAsync(domain));
        }
        await retirement.ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
    }

    private async Task RetireCoreAsync(Domain domain)
    {
        domain.Cancellation.Cancel();
        domain.Connection?.Abort();
        // Wait for an in-progress launch/initialization to publish its owned connection before reaping.
        await _lifecycle.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            foreach (var binding in domain.Bindings.Values.ToArray())
                binding.Dispose();
            // Cancellation cannot interrupt revocation/reaping and leave the old authority alive.
            if (domain.Connection is not null)
                await domain.Connection.DisposeAsync().ConfigureAwait(false);
            lock (_sync)
            {
                if (ReferenceEquals(_domains.GetValueOrDefault(domain.Key), domain))
                {
                    _domains.Remove(domain.Key);
                    foreach (var member in domain.Members)
                        _failures.Remove(member);
                }
            }
        }
        finally { _lifecycle.Release(); }
    }

    public void Dispose()
    {
        Domain[] domains;
        Task[] inspections;
        lock (_sync)
        {
            if (_disposed)
                return;
            _disposed = true;
            domains = _domains.Values.ToArray();
            inspections = _inspectionCompletions.Select(completion => completion.Task).ToArray();
            foreach (var domain in domains)
                domain.Retired = true;
        }
        _shutdown.Cancel();
        foreach (var domain in domains)
        {
            domain.Cancellation.Cancel();
            domain.Connection?.Abort();
        }
        _lifecycle.Wait();
        try
        {
            foreach (var domain in domains)
                foreach (var binding in domain.Bindings.Values.ToArray())
                    binding.Dispose();
            var cleanup = domains.Select(domain => domain.Connection?.DisposeAsync().AsTask() ?? Task.CompletedTask);
            Task.WhenAll(cleanup.Concat(inspections)).WaitAsync(PluginSandboxLaunch.ShutdownBudget).GetAwaiter().GetResult();
        }
        finally { _lifecycle.Release(); }
    }

    internal static RuntimeException Failure(string code, string message) => new(code, message);

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
            owner.RetireAsync(package).GetAwaiter().GetResult();
        }
    }
}
