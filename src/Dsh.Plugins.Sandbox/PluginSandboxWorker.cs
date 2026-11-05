using System.Runtime.CompilerServices;
using System.Text.Json;
using Dsh.Plugins;
using Dsh.Runtime;
using Dsh.Transport;
using static Dsh.Plugins.Sandbox.PluginSandboxWorkerWire;

namespace Dsh.Plugins.Sandbox;

/** The executable must dispatch this entry before composing any application Context. */
internal static class PluginSandboxWorker
{
    internal const string HostArgument = "--plugin-worker";

    internal static ValueTask<int>? TryRunAsync(string[] args)
    {
        if (Array.IndexOf(args, HostArgument) < 0)
            return null;
        return args.Length == 1 ? new ValueTask<int>(RunAsync()) : new ValueTask<int>(2);
    }

    private static async Task<int> RunAsync()
    {
        // Keep the binary pipe independently of Console.Out. Ordinary managed writes are
        // discarded, never inserted into framing; raw native stdout remains a visible fault.
        var input = Console.OpenStandardInput();
        var output = Console.OpenStandardOutput();
        Console.SetOut(TextWriter.Null);
        Console.SetIn(TextReader.Null);
        await using var stream = new StandardIoDuplexStream(input, output, ownsStreams: true);
        await using var peer = new JsonRpcPeer(stream, PluginSandboxMethods.MaximumFrameBytes);
        var session = new Session(peer);
        peer.RequestPreparation = session.Prepare;
        peer.Closed += session.Closed;
        peer.Start();
        return await session.Completion.ConfigureAwait(false);
    }

    internal sealed class Invocation
    {
        private readonly Lock _gate = new();
        private readonly CancellationTokenRegistration _lifetime;
        private int _cancellations;
        private int _active = 1;

        internal Invocation(string token, CancellationToken lifetime)
        {
            Token = token;
            _lifetime = lifetime.Register(Cancel);
        }

        internal string Token { get; }
        internal CancellationTokenSource Cancellation { get; } = new();
        internal bool Active => Volatile.Read(ref _active) != 0;

        internal void Cancel()
        {
            lock (_gate)
            {
                if (!Active)
                    return;
                _cancellations++;
            }
            try { Cancellation.Cancel(); }
            finally
            {
                lock (_gate)
                    if (--_cancellations == 0 && !Active)
                        Cancellation.Dispose();
            }
        }

        internal void End()
        {
            lock (_gate)
            {
                Interlocked.Exchange(ref _active, 0);
                _lifetime.Unregister();
                if (_cancellations == 0)
                    Cancellation.Dispose();
            }
        }
    }

    internal sealed class Session(JsonRpcPeer peer)
    {
        private readonly Lock _gate = new();
        private readonly SemaphoreSlim _lifecycle = new(1, 1);
        private readonly Dictionary<string, LoadedPackage> _packages = new(StringComparer.Ordinal);
        private readonly Dictionary<string, Artifact> _artifacts = new(StringComparer.Ordinal);
        private readonly Dictionary<string, Invocation> _invocations = new(StringComparer.Ordinal);
        private readonly CancellationTokenSource _lifetime = new();
        private readonly TaskCompletionSource<int> _closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly SemaphoreSlim _inbound = new(PluginSandboxMethods.MaximumPendingRequests);
        private readonly SemaphoreSlim _reverse = new(PluginSandboxMethods.MaximumPendingRequests);
        private int _toolCount;
        private int _failed;

        internal Task<int> Completion => _closed.Task;

        internal ValueTask<JsonElement?>? Prepare(JsonRpcMessage message, CancellationToken cancellationToken)
        {
            Invocation? invocation = null;
            PluginSandboxInvokeRequest? invoke = null;
            Exception? preparationFailure = null;
            try
            {
                if (message.Method == PluginSandboxMethods.Invoke)
                {
                    invoke = Read(message.Params, PluginSandboxJsonContext.Default.PluginSandboxInvokeRequest);
                    if (invoke.Invocation.Length == 0)
                        throw new InvalidOperationException("Missing invocation authority.");
                    lock (_gate)
                    {
                        if (_invocations.ContainsKey(invoke.Invocation))
                            throw new InvalidOperationException("Duplicate invocation authority.");
                        invocation = new Invocation(invoke.Invocation, _lifetime.Token);
                        _invocations.Add(invoke.Invocation, invocation);
                    }
                }
            }
            catch (Exception error)
            {
                preparationFailure = error;
            }
            // Constructors, initialization and synchronous reverse interop never occupy the reader.
            return new ValueTask<JsonElement?>(Task.Run(async () =>
            {
                var admitted = false;
                try
                {
                    await _inbound.WaitAsync(cancellationToken).ConfigureAwait(false);
                    admitted = true;
                    if (preparationFailure is not null)
                        throw preparationFailure;
                    if (invoke is not null && invocation is not null)
                        return await InvokeAsync(invoke, invocation).ConfigureAwait(false);
                    return await DispatchAsync(message, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception error)
                {
                    throw new JsonRpcException(error is JsonRpcException rpc ? rpc.Code : JsonRpcPeer.InternalError,
                        BoundedFailure(error));
                }
                finally
                {
                    if (invocation is not null)
                    {
                        invocation.End();
                        lock (_gate)
                            _invocations.Remove(invocation.Token);
                    }
                    if (admitted)
                        _inbound.Release();
                }
            }));
        }

        private async Task<JsonElement?> DispatchAsync(JsonRpcMessage message, CancellationToken cancellationToken)
        {
            switch (message.Method)
            {
                case PluginSandboxMethods.Ready:
                    return Serialize(new PluginSandboxReady(PluginSandboxMethods.Version, Environment.ProcessId),
                        PluginSandboxJsonContext.Default.PluginSandboxReady);
                case PluginSandboxMethods.Inspect:
                case PluginSandboxMethods.Load:
                case PluginSandboxMethods.Unload:
                    await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
                    try
                    {
                        if (message.Method == PluginSandboxMethods.Inspect)
                            return Serialize(await InspectAsync(Read(message.Params,
                                PluginSandboxJsonContext.Default.PluginSandboxInspectRequest)).ConfigureAwait(false),
                                PluginSandboxJsonContext.Default.PluginSandboxInspection);
                        if (message.Method == PluginSandboxMethods.Unload)
                            await UnloadAsync(Read(message.Params, PluginSandboxJsonContext.Default.PluginSandboxUnloadRequest)).ConfigureAwait(false);
                        else
                            await LoadAsync(Read(message.Params, PluginSandboxJsonContext.Default.PluginSandboxLoadRequest),
                                cancellationToken).ConfigureAwait(false);
                        return Ack();
                    }
                    finally
                    {
                        _lifecycle.Release();
                    }
                default:
                    throw new JsonRpcException(JsonRpcPeer.MethodNotFound, "Unknown worker method.");
            }
        }

        internal async Task<JsonElement> Reverse(string method, JsonElement parameters, CancellationToken cancellationToken)
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
            await _reverse.WaitAsync(linked.Token).ConfigureAwait(false);
            try
            {
                var result = await peer.RequestAsync(method, parameters, linked.Token).ConfigureAwait(false)
                    ?? throw new IOException("Missing parent response.");
                if (method != PluginSandboxMethods.HostCall
                    && !Read(result, PluginSandboxJsonContext.Default.PluginSandboxAck).Success)
                    throw new InvalidOperationException("Parent rejected the worker operation.");
                return result;
            }
            finally
            {
                _reverse.Release();
            }
        }

        internal void ReserveTool()
        {
            lock (_gate)
            {
                if (_toolCount >= PluginSandboxMethods.MaximumTools)
                    throw new InvalidOperationException("Worker tool registration limit exceeded.");
                _toolCount++;
            }
        }

        internal void ReleaseTool()
        {
            lock (_gate)
            {
                _toolCount--;
            }
        }

        private async Task<JsonElement> InvokeAsync(PluginSandboxInvokeRequest request, Invocation invocation)
        {
            LoadedPackage package;
            lock (_gate)
                if (!_packages.TryGetValue(request.Package, out package!))
                    throw new InvalidOperationException("Plugin package is not loaded.");
            using var registration = package.Host.Lifetime.Token.Register(invocation.Cancel);
            return await package.Host.InvokeAsync(request.ToolId, request.Arguments, invocation).ConfigureAwait(false);
        }

        private async Task LoadAsync(PluginSandboxLoadRequest request, CancellationToken cancellationToken)
        {
            var configuration = request.Configuration;
            lock (_gate)
            {
                if (_packages.ContainsKey(request.Package))
                    throw new InvalidOperationException("Plugin package is already loaded.");
            }
            Artifact? artifact = null;
            PluginDefinition? definition = null;
            PluginSandboxWorkerContext? lease = null;
            PluginSandboxWorkerHost? host = null;
            try
            {
                if (request.ArtifactPath is null)
                {
                    var registration = Compiled(request.Package).SingleOrDefault()
                        ?? throw new InvalidOperationException("Compiled plugin package was not found.");
                    definition = PluginCatalog.CreateDefinition(
                        PluginDescriptor.For(registration.Package, PluginForm.CompiledIn) with { Entry = registration.Entry },
                        registration.Create);
                }
                else
                {
                    var path = Path.GetFullPath(request.ArtifactPath);
                    if (!_artifacts.TryGetValue(path, out artifact))
                    {
                        artifact = Artifact.Open(path);
                        _artifacts.Add(path, artifact);
                    }
                    artifact.References++;
                    if (artifact.Native is { } native && native.Package != request.Package)
                        throw new InvalidOperationException("Native artifact package does not match the request.");
                    if (artifact.Native is null)
                    {
                        if (!artifact.Host!.Catalog.TryCreateDefinition(request.Package, out definition))
                            throw new InvalidOperationException("Managed artifact package was not found.");
                    }
                }
                host = new PluginSandboxWorkerHost(this, request.Package, artifact?.Native is not null);
                if (artifact?.Native is { } library)
                {
                    library.Configure(configuration.GetRawText());
                    library.Activate(host);
                }
                else
                {
                    var managed = definition ?? throw new InvalidOperationException("Managed plugin definition is missing.");
                    if (!PluginSandboxWorkerContext.Supports(managed.Inject, managed.InjectTypes))
                        throw new InvalidOperationException("The plugin requires services unavailable in the worker Context.");
                    lease = new PluginSandboxWorkerContext(host);
                    await lease.ActivateAsync(managed, configuration, cancellationToken).ConfigureAwait(false);
                }
                lock (_gate)
                    _packages.Add(request.Package, new LoadedPackage(host, artifact, lease));
            }
            catch (Exception activationError)
            {
                // Failed initialization can retain threads. The parent must retire the domain.
                string? cleanupFailure = null;
                if (host is not null)
                {
                    try { host.Stop(); }
                    catch (Exception error) { cleanupFailure = BoundedFailure(error); }
                }
                try
                {
                    if (lease is not null)
                        await lease.DisposeAsync().ConfigureAwait(false);
                }
                catch (Exception error) { cleanupFailure ??= BoundedFailure(error); }
                if (host is not null)
                {
                    try { host.ReleaseTools(); }
                    catch (Exception error) { cleanupFailure ??= BoundedFailure(error); }
                }
                if (artifact is not null)
                {
                    artifact.References--;
                    if (artifact.References == 0)
                    {
                        _artifacts.Remove(artifact.Path);
                        try { artifact.Release(); }
                        catch (Exception error) { cleanupFailure ??= BoundedFailure(error); }
                    }
                }
                if (cleanupFailure is not null)
                    throw new InvalidOperationException($"{BoundedFailure(activationError)}; cleanup: {cleanupFailure}");
                throw;
            }
        }

        private async Task UnloadAsync(PluginSandboxUnloadRequest request)
        {
            LoadedPackage? package;
            lock (_gate)
                _packages.Remove(request.Package, out package);
            if (package is null)
                return;
            await package.Host.StopAsync().ConfigureAwait(false);
            try
            {
                if (package.Lease is not null)
                    await package.Lease.DisposeAsync().ConfigureAwait(false);
            }
            finally
            {
                package.Host.ReleaseTools();
                if (package.Artifact is { } artifact && --artifact.References == 0)
                {
                    _artifacts.Remove(artifact.Path);
                    artifact.Release();
                }
            }
        }

        private static async Task<PluginSandboxInspection> InspectAsync(PluginSandboxInspectRequest request)
        {
            var result = await ProbeAsync(request).ConfigureAwait(false);
            await Task.Yield();
            if (result.Weak is not null && !PluginUnloader.WaitForCollection(result.Weak, out var report))
                return result.Inspection with { Errors = [.. result.Inspection.Errors, report!], Released = false };
            return result.Inspection;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static async Task<(PluginSandboxInspection Inspection, WeakReference? Weak)> ProbeAsync(PluginSandboxInspectRequest request)
        {
            Artifact? artifact = null;
            var metadata = new List<PluginSandboxMetadata>();
            var errors = new List<string>();
            WeakReference? weak = null;
            var released = true;
            try
            {
                if (request.ArtifactPath is null)
                {
                    foreach (var registration in Compiled(request.Package))
                    {
                        metadata.Add(await ProbePluginAsync(registration.Package, PluginForm.CompiledIn,
                            registration.Entry, registration.Create).ConfigureAwait(false));
                    }
                }
                else
                {
                    artifact = Artifact.Open(Path.GetFullPath(request.ArtifactPath));
                    if (artifact.Native is { } native)
                    {
                        if (request.Package is null || request.Package == native.Package)
                        {
                            metadata.Add(new PluginSandboxMetadata(native.Package, PluginForm.NativeLibrary, null, true));
                        }
                    }
                    else
                    {
                        foreach (var descriptor in artifact.Host!.Catalog.Descriptors)
                        {
                            if (request.Package is not null && descriptor.Package != request.Package)
                                continue;
                            if (artifact.Host.Catalog.TryGet(descriptor.Package, out var create))
                                metadata.Add(await ProbePluginAsync(descriptor.Package, descriptor.Form,
                                    descriptor.Entry, create).ConfigureAwait(false));
                        }
                    }
                }
                if (request.Package is not null && metadata.Count == 0)
                    errors.Add("Requested plugin package was not found.");
            }
            catch (Exception error)
            {
                errors.Add(BoundedFailure(error));
            }
            finally
            {
                if (artifact is not null)
                {
                    try { weak = artifact.Release(); }
                    catch (Exception error)
                    {
                        released = false;
                        errors.Add(BoundedFailure(error));
                    }
                }
            }
            return (new PluginSandboxInspection([.. metadata], [.. errors], released), weak);
        }

        private static async Task<PluginSandboxMetadata> ProbePluginAsync(string package, PluginForm form,
            string? entry, Func<IDshPlugin> create)
        {
            var plugin = create();
            try { return new PluginSandboxMetadata(package, form, entry, PluginSandboxWorkerContext.Supports(plugin.Inject, plugin.InjectTypes)); }
            finally { await DisposeProbeAsync(plugin).ConfigureAwait(false); }
        }

        private static IEnumerable<DshPluginRegistration> Compiled(string? package)
        {
            foreach (var registration in DshPluginCatalogRegistry.Snapshot())
            {
                if (package is not null && package != registration.Package)
                    continue;
                if (registration.DescriptorVersion != PluginDescriptor.CurrentVersion)
                    throw new InvalidOperationException("Compiled plugin descriptor version mismatch.");
                yield return registration;
            }
        }

        private static async ValueTask DisposeProbeAsync(IDshPlugin plugin)
        {
            if (plugin is IAsyncDisposable asynchronous)
                await asynchronous.DisposeAsync().ConfigureAwait(false);
            else if (plugin is IDisposable synchronous)
                synchronous.Dispose();
        }

        private static JsonElement Ack()
            => Serialize(new PluginSandboxAck(), PluginSandboxJsonContext.Default.PluginSandboxAck);

        private static string BoundedFailure(Exception error)
        {
            var message = error.GetBaseException().Message;
            return message.Length <= 2048 ? message : message[..2048];
        }

        internal void Closed(Exception? failure)
        {
            if (failure is not null)
                Interlocked.Exchange(ref _failed, 1);
            // User cancellation callbacks may block; EOF must still let the worker exit.
            _ = Task.Run(() => _lifetime.Cancel());
            _closed.TrySetResult(Volatile.Read(ref _failed));
        }

    }

    private sealed record LoadedPackage(PluginSandboxWorkerHost Host, Artifact? Artifact, PluginSandboxWorkerContext? Lease);

    /** One artifact owns one ALC/library, independently of its active package leases. */
    private sealed class Artifact(string path, PluginHost? host, PluginLoadContext? context, NativePluginLibrary? native)
    {
        internal string Path { get; } = path;
        internal PluginHost? Host { get; private set; } = host;
        private PluginLoadContext? Context { get; set; } = context;
        internal NativePluginLibrary? Native { get; private set; } = native;
        internal int References { get; set; }
        private IReadOnlyList<string> SharedDependencies { get; set; } = [];

        internal static Artifact Open(string path)
        {
            if (RuntimeFeature.IsDynamicCodeSupported && System.IO.Path.GetExtension(path).Equals(".dll", StringComparison.OrdinalIgnoreCase))
            {
                var directory = System.IO.Path.GetDirectoryName(path)!;
                var localPool = System.IO.Path.Combine(directory, ".shared");
                var sharedDirectory = Directory.Exists(localPool)
                    ? localPool
                    : System.IO.Path.Combine(System.IO.Path.GetDirectoryName(directory) ?? directory, ".shared");
                var host = new PluginHost { SharedPool = new SharedAssemblyPool(sharedDirectory) };
                try
                {
                    var result = host.TryLoad(path);
                    if (result.Context is not null && result.Packages.Count > 0 && result.Skipped.Count == 0)
                        return new Artifact(path, host, result.Context, null) { SharedDependencies = result.SharedDependencies };
                    foreach (var package in host.Catalog.PackageNames.ToArray())
                        host.Catalog.Remove(package);
                    host.SharedPool.Release(result.SharedDependencies);
                    result.Context?.Unload();
                    throw new InvalidOperationException("Managed artifact has no compatible plugin manifest.");
                }
                catch (BadImageFormatException)
                {
                    // Windows native ABI libraries also use .dll.
                }
            }
            var native = NativePluginLibrary.TryLoad(path);
            if (!native.Succeeded)
                throw new InvalidOperationException(native.Reason ?? "Native artifact load failed.");
            return new Artifact(path, null, null, native.Plugin);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        internal WeakReference? Release()
        {
            var native = Native;
            Native = null;
            if (native is not null)
            {
                try { native.Dispose(); }
                finally { native.Configure(""); }
            }
            if (Host is not null)
            {
                foreach (var package in Host.Catalog.PackageNames.ToArray())
                    Host.Catalog.Remove(package);
                Host.SharedPool?.Release(SharedDependencies);
                Host.SharedPool = null;
                SharedDependencies = [];
                Host = null;
            }
            var context = Context;
            Context = null;
            return context is null ? null : PluginUnloader.Unload(context);
        }
    }
}
