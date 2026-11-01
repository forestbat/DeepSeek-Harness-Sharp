using System.Runtime.CompilerServices;
using System.Text.Json;
using Dsh.Plugins;
using Dsh.Transport;
using static Dsh.Boot.PluginSandboxWorkerWire;

namespace Dsh.Boot;

/** The executable must dispatch this entry before composing any application Context. */
public static class PluginSandboxWorker
{
    public const string HostArgument = "--plugin-worker";

    public static async Task<int> RunAsync(string[] args)
    {
        if (args.Length != 1 || args[0] != HostArgument)
            return 2;
        // Keep the binary pipe independently of Console.Out. Ordinary managed writes are
        // discarded, never inserted into framing; raw native stdout remains a visible fault.
        var input = Console.OpenStandardInput();
        var output = Console.OpenStandardOutput();
        Console.SetOut(TextWriter.Null);
        Console.SetIn(TextReader.Null);
        await using var stream = new StandardIoDuplexStream(input, output, ownsStreams: true);
        await using var peer = new JsonRpcPeer(stream, PluginSandboxMethods.MaximumFrameBytes);
        var session = new Session(peer, stream);
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

        internal Invocation(string token, string package, CancellationToken lifetime)
        {
            Token = token;
            Package = package;
            _lifetime = lifetime.Register(Cancel);
        }

        internal string Token { get; }
        internal string Package { get; }
        internal CancellationTokenSource Cancellation { get; } = new();
        internal TaskCompletionSource Finished { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
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

    internal sealed class Session(JsonRpcPeer peer, Stream stream)
    {
        private readonly Lock _gate = new();
        private readonly SemaphoreSlim _lifecycle = new(1, 1);
        private readonly Dictionary<string, LoadedPackage> _packages = new(StringComparer.Ordinal);
        private readonly Dictionary<string, Artifact> _artifacts = new(StringComparer.Ordinal);
        private readonly Dictionary<string, Invocation> _invocations = new(StringComparer.Ordinal);
        private readonly CancellationTokenSource _lifetime = new();
        private readonly TaskCompletionSource<int> _closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _inbound;
        private int _reverse;
        private int _toolCount;
        private int _toolBytes;
        private int _failed;

        internal Task<int> Completion => _closed.Task;

        internal ValueTask<JsonElement?>? Prepare(JsonRpcMessage message, CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref _inbound) > PluginSandboxMethods.MaximumInboundRequests)
            {
                Interlocked.Decrement(ref _inbound);
                Fail();
                return ValueTask.FromException<JsonElement?>(new IOException("Worker inbound request limit exceeded."));
            }
            Invocation? invocation = null;
            PluginSandboxInvokeRequest? invoke = null;
            Exception? preparationFailure = null;
            try
            {
                if (message.Method == PluginSandboxMethods.Invoke)
                {
                    invoke = Read(message.Params, PluginSandboxJsonContext.Default.PluginSandboxInvokeRequest);
                    Text(invoke.Package, 256);
                    Text(invoke.Invocation, 256);
                    if (invoke.Invocation.Length == 0)
                        throw new InvalidOperationException("Missing invocation authority.");
                    lock (_gate)
                    {
                        if (_invocations.ContainsKey(invoke.Invocation))
                            throw new InvalidOperationException("Duplicate invocation authority.");
                        invocation = new Invocation(invoke.Invocation, invoke.Package, _lifetime.Token);
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
                try
                {
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
                        invocation.Finished.TrySetResult();
                    }
                    Interlocked.Decrement(ref _inbound);
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
                case PluginSandboxMethods.Cancel:
                    var cancel = Read(message.Params, PluginSandboxJsonContext.Default.PluginSandboxCancelRequest);
                    Invocation? pending;
                    lock (_gate)
                        _invocations.TryGetValue(cancel.Invocation, out pending);
                    pending?.Cancel();
                    return Ack();
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
                        if (message.Method == PluginSandboxMethods.Load)
                        {
                            await LoadAsync(Read(message.Params, PluginSandboxJsonContext.Default.PluginSandboxLoadRequest),
                                cancellationToken).ConfigureAwait(false);
                            return Ack();
                        }
                        return Serialize(await UnloadAsync(Read(message.Params,
                            PluginSandboxJsonContext.Default.PluginSandboxUnloadRequest).Package).ConfigureAwait(false),
                            PluginSandboxJsonContext.Default.PluginSandboxUnloadReply);
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
            if (Interlocked.Increment(ref _reverse) > PluginSandboxMethods.MaximumInboundRequests)
            {
                Interlocked.Decrement(ref _reverse);
                Fail();
                throw new IOException("Worker reverse request limit exceeded.");
            }
            try
            {
                var result = Element(await peer.RequestAsync(method, parameters, cancellationToken).ConfigureAwait(false)
                    ?? throw new IOException("Missing parent response."));
                if (method != PluginSandboxMethods.HostCall
                    && !Read(result, PluginSandboxJsonContext.Default.PluginSandboxAck).Success)
                    throw new InvalidOperationException("Parent rejected the worker operation.");
                return result;
            }
            finally
            {
                Interlocked.Decrement(ref _reverse);
            }
        }

        internal void ReserveTool(int bytes)
        {
            lock (_gate)
            {
                if (_toolCount >= MaximumTools || bytes > MaximumRegistrationBytes - _toolBytes)
                    throw new InvalidOperationException("Worker tool registration budget exceeded.");
                _toolCount++;
                _toolBytes += bytes;
            }
        }

        internal void ReleaseTool(int bytes)
        {
            lock (_gate)
            {
                _toolCount--;
                _toolBytes -= bytes;
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
            Text(request.Package, 256);
            var configuration = Element(request.Configuration);
            lock (_gate)
            {
                if (_packages.ContainsKey(request.Package))
                    throw new InvalidOperationException("Plugin package is already loaded.");
                if (_packages.Count >= MaximumPackages)
                    throw new InvalidOperationException("Worker package budget exceeded.");
            }
            Artifact? artifact = null;
            IDshPlugin? candidate = null;
            IDshIsolatedPlugin? plugin = null;
            IAsyncDisposable? lease = null;
            PluginSandboxWorkerHost? host = null;
            try
            {
                if (request.ArtifactPath is null)
                {
                    var registration = Compiled(request.Package).SingleOrDefault()
                        ?? throw new InvalidOperationException("Compiled plugin package was not found.");
                    candidate = registration.Create();
                    plugin = candidate as IDshIsolatedPlugin
                        ?? throw new InvalidOperationException("Legacy full-Context plugins require explicit host authority.");
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
                        if (!artifact.Host!.Catalog.TryGet(request.Package, out var create))
                            throw new InvalidOperationException("Managed artifact package was not found.");
                        candidate = create();
                        plugin = candidate as IDshIsolatedPlugin
                            ?? throw new InvalidOperationException("Legacy full-Context plugins require explicit host authority.");
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
                    lease = await plugin!.InitializeAsync(host, configuration, cancellationToken).ConfigureAwait(false);
                }
                lock (_gate)
                    _packages.Add(request.Package, new LoadedPackage(host, artifact, plugin, lease));
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
                try { await DisposePluginAsync(candidate, lease).ConfigureAwait(false); }
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

        private async Task<PluginSandboxUnloadReply> UnloadAsync(string package)
        {
            Text(package, 256);
            var cleanup = await DetachAsync(package).ConfigureAwait(false);
            // The producer frame holding plugin/configuration/lease must be gone before GC.
            await Task.Yield();
            if (cleanup.Weak is not null && !PluginUnloader.WaitForCollection(cleanup.Weak, out var report))
                return new PluginSandboxUnloadReply(false, cleanup.Failure ?? report);
            return new PluginSandboxUnloadReply(cleanup.Failure is null, cleanup.Failure);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private async Task<(WeakReference? Weak, string? Failure)> DetachAsync(string package)
        {
            LoadedPackage? loaded;
            Invocation[] invocations;
            lock (_gate)
            {
                if (!_packages.Remove(package, out loaded))
                    return (null, "Plugin package is not loaded.");
                invocations = [.. _invocations.Values.Where(item => item.Package == package)];
            }
            string? failure = null;
            try { loaded.Host.Stop(); }
            catch (Exception error) { failure = BoundedFailure(error); }
            // A cancellation-ignoring callback is still running. Do not free its library or
            // report it stopped; the parent's retirement deadline terminates the domain.
            await Task.WhenAll(invocations.Select(item => item.Finished.Task)).ConfigureAwait(false);
            try
            {
                if (loaded.Lease is not null)
                    await loaded.Lease.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception error) { failure ??= BoundedFailure(error); }
            try { await DisposePluginAsync(loaded.Plugin, loaded.Lease).ConfigureAwait(false); }
            catch (Exception error) { failure ??= BoundedFailure(error); }
            try { loaded.Host.ReleaseTools(); }
            catch (Exception error) { failure ??= BoundedFailure(error); }
            WeakReference? weak = null;
            if (loaded.Artifact is { } artifact)
            {
                artifact.References--;
                if (artifact.References == 0)
                {
                    _artifacts.Remove(artifact.Path);
                    try { weak = artifact.Release(); }
                    catch (Exception error) { failure ??= BoundedFailure(error); }
                }
                else
                {
                    failure ??= "The artifact is retained by another active package; collection was not attempted.";
                }
            }
            else
            {
                failure ??= "Compiled-in code requires OS domain termination; its assembly cannot be collected.";
            }
            loaded = null;
            return (weak, failure);
        }

        private static async Task<PluginSandboxInspection> InspectAsync(PluginSandboxInspectRequest request)
        {
            if (request.Package is not null)
                Text(request.Package, 256);
            var result = await ProbeAsync(request).ConfigureAwait(false);
            await Task.Yield();
            if (result.Weak is not null && !PluginUnloader.WaitForCollection(result.Weak, out var report))
                return result.Inspection with { Errors = [.. result.Inspection.Errors, report!] };
            return result.Inspection;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static async Task<(PluginSandboxInspection Inspection, WeakReference? Weak)> ProbeAsync(PluginSandboxInspectRequest request)
        {
            Artifact? artifact = null;
            var metadata = new List<PluginSandboxMetadata>();
            var errors = new List<string>();
            WeakReference? weak = null;
            try
            {
                if (request.ArtifactPath is null)
                {
                    foreach (var registration in Compiled(request.Package))
                    {
                        if (metadata.Count >= MaximumPackages)
                            throw new InvalidOperationException("Worker metadata package limit exceeded.");
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
                            Text(native.Package, 256);
                            metadata.Add(new PluginSandboxMetadata(native.Package, PluginForm.NativeLibrary, null, true));
                        }
                    }
                    else
                    {
                        foreach (var descriptor in artifact.Host!.Catalog.Descriptors)
                        {
                            if (request.Package is not null && descriptor.Package != request.Package)
                                continue;
                            if (metadata.Count >= MaximumPackages)
                                throw new InvalidOperationException("Worker metadata package limit exceeded.");
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
                    catch (Exception error) { errors.Add(BoundedFailure(error)); }
                }
            }
            return (new PluginSandboxInspection([.. metadata], [.. errors]), weak);
        }

        private static async Task<PluginSandboxMetadata> ProbePluginAsync(string package, PluginForm form,
            string? entry, Func<IDshPlugin> create)
        {
            Text(package, 256);
            if (entry is not null)
                Text(entry, 256);
            var plugin = create();
            try { return new PluginSandboxMetadata(package, form, entry, plugin is IDshIsolatedPlugin); }
            finally { await DisposePluginAsync(plugin, null).ConfigureAwait(false); }
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

        private static async ValueTask DisposePluginAsync(IDshPlugin? plugin, IAsyncDisposable? lease)
        {
            if (ReferenceEquals(plugin, lease))
                return;
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

        private void Fail()
        {
            Interlocked.Exchange(ref _failed, 1);
            stream.Dispose();
        }
    }

    private sealed record LoadedPackage(PluginSandboxWorkerHost Host, Artifact? Artifact,
        IDshIsolatedPlugin? Plugin, IAsyncDisposable? Lease);

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
                    if (result.Context is not null && result.Packages.Count is > 0 and <= MaximumPackages
                        && result.Skipped.Count == 0)
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
