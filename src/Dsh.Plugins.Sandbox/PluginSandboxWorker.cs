using System.Runtime.CompilerServices;
using System.Text.Json;
using Dsh.Plugins;
using Dsh.Runtime;
using Dsh.Transport;
using StreamJsonRpc;
using StreamJsonRpc.Protocol;
using static Dsh.Plugins.Sandbox.PluginSandboxWorkerWire;

namespace Dsh.Plugins.Sandbox;

/** The executable must dispatch this entry before composing any application Context. */
internal static class PluginSandboxWorker
{
    internal const string HostArgument = "--plugin-worker";

    /** 参数不合法时的退出码: 非零即可, 值是任意的, 只为让调用方拿到明确失败。 */
    private const int InvalidInvocationExitCode = 2;

    internal static ValueTask<int>? TryRunAsync(string[] args)
    {
        if (Array.IndexOf(args, HostArgument) < 0)
            return null;
        if (args.Length != 1)
        {
            // 参数错误是预期内的调用方问题, 写成错误输出并返回非零码。
            // 不能靠抛未处理异常来表达: .NET 11 在终止前会先做进程内崩溃报告, 进程要十几秒才退出,
            // 调用方的超时会先触发, 表现为"工作进程无响应"而不是"参数非法"。
            Console.Error.WriteLine("The plugin worker requires only its worker argument.");
            return new ValueTask<int>(InvalidInvocationExitCode);
        }
        return new ValueTask<int>(RunAsync());
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
        var session = new Session();
        using var channel = JsonRpcChannel.ServeMethods(stream, PluginSandboxWire.Formatter(), session.Methods());
        session.AttachChannel(channel);
        _ = ObserveClosedAsync(channel, session);
        return await session.Completion.ConfigureAwait(false);
    }

    /** 连接结束就是 worker 的结束: 失败原因交给退出码, 正常 EOF 退出 0。 */
    private static async Task ObserveClosedAsync(JsonRpcChannel channel, Session session)
    {
        Exception? failure = null;
        try
        {
            await channel.Completion.ConfigureAwait(false);
        }
        catch (Exception error)
        {
            failure = error;
        }
        session.Closed(failure);
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

    internal sealed class Session
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
        private JsonRpcChannel? _channel;
        private int _toolCount;
        private int _failed;

        internal Task<int> Completion => _closed.Task;

        internal void AttachChannel(JsonRpcChannel channel) => _channel = channel;

        /** 五条宿主调用按线协议名登记, 不为此定义只实现一次的接口。 */
        internal (string Method, Delegate Handler)[] Methods() =>
        [
            (PluginSandboxMethods.Ready, (Func<PluginSandboxAck, CancellationToken, Task<PluginSandboxReady>>)ReadyAsync),
            (PluginSandboxMethods.Inspect, (Func<PluginSandboxInspectRequest, CancellationToken, Task<PluginSandboxInspection>>)InspectAsync),
            (PluginSandboxMethods.Load, (Func<PluginSandboxLoadRequest, CancellationToken, Task<PluginSandboxAck>>)LoadAsync),
            (PluginSandboxMethods.Unload, (Func<PluginSandboxUnloadRequest, CancellationToken, Task<PluginSandboxAck>>)UnloadAsync),
            (PluginSandboxMethods.Invoke, (Func<PluginSandboxInvokeRequest, CancellationToken, Task<JsonElement>>)InvokeAsync),
        ];

        public async Task<PluginSandboxReady> ReadyAsync(PluginSandboxAck request, CancellationToken cancellationToken)
        {
            await _inbound.WaitAsync(cancellationToken).ConfigureAwait(false);
            try { return new PluginSandboxReady(PluginSandboxMethods.Version, Environment.ProcessId); }
            finally { _inbound.Release(); }
        }

        public async Task<PluginSandboxInspection> InspectAsync(PluginSandboxInspectRequest request, CancellationToken cancellationToken)
        {
            await _inbound.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
                try { return await InspectAsync(request).ConfigureAwait(false); }
                finally { _lifecycle.Release(); }
            }
            catch (Exception error) { throw Failure(error); }
            finally { _inbound.Release(); }
        }

        public async Task<PluginSandboxAck> LoadAsync(PluginSandboxLoadRequest request, CancellationToken cancellationToken)
        {
            await _inbound.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
                try { await LoadCoreAsync(request, cancellationToken).ConfigureAwait(false); return new PluginSandboxAck(); }
                finally { _lifecycle.Release(); }
            }
            catch (Exception error) { throw Failure(error); }
            finally { _inbound.Release(); }
        }

        public async Task<PluginSandboxAck> UnloadAsync(PluginSandboxUnloadRequest request, CancellationToken cancellationToken)
        {
            await _inbound.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
                try { await UnloadAsync(request).ConfigureAwait(false); return new PluginSandboxAck(); }
                finally { _lifecycle.Release(); }
            }
            catch (Exception error) { throw Failure(error); }
            finally { _inbound.Release(); }
        }

        public async Task<JsonElement> InvokeAsync(PluginSandboxInvokeRequest request, CancellationToken cancellationToken)
        {
            if (request.Invocation.Length == 0)
                throw Failure(new InvalidOperationException("Missing invocation authority."));
            Invocation invocation;
            lock (_gate)
            {
                if (_invocations.ContainsKey(request.Invocation))
                    throw Failure(new InvalidOperationException("Duplicate invocation authority."));
                invocation = new Invocation(request.Invocation, _lifetime.Token);
                _invocations.Add(request.Invocation, invocation);
            }
            await _inbound.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                return await InvokeAsync(request, invocation).ConfigureAwait(false);
            }
            catch (Exception error) { throw Failure(error); }
            finally
            {
                invocation.End();
                lock (_gate)
                    _invocations.Remove(invocation.Token);
                _inbound.Release();
            }
        }

        private static JsonRpcException Failure(Exception error)
            => error as JsonRpcException
                ?? new JsonRpcException((int)JsonRpcErrorCode.InternalError, BoundedFailure(error));

        internal Task<PluginSandboxAck> RegisterToolAsync(PluginSandboxToolRequest request, CancellationToken cancellationToken)
            => BrokerAsync<PluginSandboxAck>(PluginSandboxMethods.RegisterTool, request, cancellationToken);

        internal Task<PluginSandboxAck> UnregisterToolAsync(PluginSandboxUnregisterTool request, CancellationToken cancellationToken)
            => BrokerAsync<PluginSandboxAck>(PluginSandboxMethods.UnregisterTool, request, cancellationToken);

        internal Task<JsonElement> HostCallAsync(PluginSandboxHostCall request, CancellationToken cancellationToken)
            => BrokerAsync<JsonElement>(PluginSandboxMethods.HostCall, request, cancellationToken);

        internal Task<PluginSandboxAck> LogAsync(PluginSandboxLog request, CancellationToken cancellationToken)
            => BrokerAsync<PluginSandboxAck>(PluginSandboxMethods.Log, request, cancellationToken);

        private async Task<T> BrokerAsync<T>(string method, object request, CancellationToken cancellationToken)
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
            await _reverse.WaitAsync(linked.Token).ConfigureAwait(false);
            try
            {
                var channel = _channel ?? throw new IOException("The parent broker is unavailable.");
                return await channel.InvokeAsync<T>(method, linked.Token, request).ConfigureAwait(false);
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

        private async Task LoadCoreAsync(PluginSandboxLoadRequest request, CancellationToken cancellationToken)
        {
            var configuration = request.Configuration;
            lock (_gate)
            {
                if (_packages.ContainsKey(request.Package))
                    throw new InvalidOperationException("Plugin package is already loaded.");
            }
            Artifact? artifact = null;
            PluginDefinition? definition = null;
            Context? context = null;
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
                    if (!Supports(managed.Inject, managed.InjectTypes))
                        throw new InvalidOperationException("The plugin requires services unavailable in the worker Context.");
                    context = new Context();
                    context.Provide(IPluginHostFactory.ServiceName, host);
                    var result = await managed.Apply(context, Configuration(configuration), cancellationToken).ConfigureAwait(false);
                    if (result is IAsyncDisposable asynchronous)
                        context.Effect(() => (Func<Task>)(() => asynchronous.DisposeAsync().AsTask()), $"apply({managed.Name})");
                    else if (result is not null)
                        context.Effect(() => result, $"apply({managed.Name})");
                    cancellationToken.ThrowIfCancellationRequested();
                }
                lock (_gate)
                    _packages.Add(request.Package, new LoadedPackage(host, artifact, context));
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
                    if (context is not null)
                        await context.Activation.DeactivateAsync().ConfigureAwait(false);
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
                if (package.Context is not null)
                    await package.Context.Activation.DeactivateAsync().ConfigureAwait(false);
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
            try { return new PluginSandboxMetadata(package, form, entry, Supports(plugin.Inject, plugin.InjectTypes)); }
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
            return message.Length <= PluginSandboxMethods.MaximumFailureCharacters ? message : message[..PluginSandboxMethods.MaximumFailureCharacters];
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

    private static bool Supports(IReadOnlyList<string> inject, IReadOnlyList<Type> injectTypes)
        => (inject.Contains(IPluginHostFactory.ServiceName, StringComparer.Ordinal)
                || injectTypes.Contains(typeof(IPluginHostFactory)))
            && inject.All(static name => name == IPluginHostFactory.ServiceName)
            && injectTypes.All(static type => type.IsAssignableFrom(typeof(PluginSandboxWorkerHost)));

    private sealed record LoadedPackage(PluginSandboxWorkerHost Host, Artifact? Artifact, Context? Context);

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
            if (RuntimeFeature.IsDynamicCodeSupported && PluginHost.InspectForm(path) == PluginForm.ManagedAssembly)
            {
                var host = new PluginHost { SharedPool = new SharedAssemblyPool(PluginHost.SharedPoolDirectory(path)) };
                var result = host.TryLoad(path);
                if (result.Context is not null && result.Packages.Count > 0 && result.Skipped.Count == 0)
                    return new Artifact(path, host, result.Context, null) { SharedDependencies = result.SharedDependencies };
                foreach (var package in host.Catalog.PackageNames.ToArray())
                    host.Catalog.Remove(package);
                host.SharedPool.Release(result.SharedDependencies);
                result.Context?.Unload();
                throw new InvalidOperationException("Managed artifact has no compatible plugin manifest.");
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
