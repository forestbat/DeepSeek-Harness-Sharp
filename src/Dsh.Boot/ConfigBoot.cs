using Dsh.Runtime;
using Dsh.Runtime.Composition;
using Dsh.Runtime.Logging;
using Dsh.Plugins;
using System.Reflection;

namespace Dsh.Boot;

public sealed record PluginCatalogInspection(IReadOnlyList<PluginDescriptor> Descriptors, IReadOnlyList<PluginSkip> Skipped);

public static class ConfigBoot
{
    private const string NativeBridgeType = "Dsh.Plugins.Native.Host.NativePluginBridge";
    private const string NativeBridgeAssembly = "Dsh.Plugins.Native.Host";

    public static async Task<HarnessApp> Compose(HarnessOptions options, PluginRuntimeFactory? runtimeFactory = null)
    {
        await Task.Yield();
        options.Home.Ensure();
        var settings = HarnessSettings.Load(options.Home);
        var home = HarnessStorage.ApplyStorageRoot(options.Home, settings);
        var paths = HarnessStorage.Paths(home, settings);
        var logging = LoggingSetup.Create(settings.Logging?.ToOptions(), paths.LogsPath, allowConsole: !options.IsTui);
        var credentials = new EnvCredentials(home, options.Cwd);
        var ctx = new Context(logging);
        if (settings.Notice is { } settingsNotice)
            ctx.LoggerFor("settings").Warn("%s", settingsNotice);
        ctx.SetOwn("dshHomePath", home.Root);
        ctx.SetOwn(DshPaths.PropName, paths);
        ctx.SetOwn("credentials", credentials);
        ctx.SetOwn("harnessOptions", options with { Home = home });
        ctx.SetOwn(OutboundProxy.PropName, CreateOutboundProxy(ctx, settings.Network));
        ctx.SetOwn(ToolDisclosureSwitch.PropName, settings.Tools?.ProgressiveDisclosure ?? false);
        _ = new ToolResultSpill(ctx, settings.Storage?.ToolResults is { } toolResults
            ? new ToolResultSpillOptions(toolResults.MaxBytes)
            : null);

        var pluginsDirectory = options.PluginsDirectory ?? Path.Combine(AppContext.BaseDirectory, "plugins");

        // 零配置不兜底:没配 provider/model 就留空,启动照常,首次发起 LLM 请求时才报错。
        var defaultModel = settings.ResolveDefaultModel();
        var provider = options.Provider ?? defaultModel?.Provider ?? "";
        var model = options.Model ?? defaultModel?.Model ?? "";

        var app = new HarnessApp
        {
            Ctx = ctx,
            Home = home,
            Credentials = credentials,
            Provider = provider,
            Model = model,
            ReasoningEffort = options.ReasoningEffort,
        };
        app.Track(logging);

        try
        {
            _ = new SafetyPolicySource(ctx, HarnessSettings.CreatePolicyReader(home, settings));
            var (pluginHost, sandbox, nativeBridge) = CreateRuntime(runtimeFactory, ctx,
                new PluginExecutionEnvironment(home, settings, options.Cwd), pluginsDirectory, app.Track);
            var discovery = sandbox is null ? pluginHost.Scan(pluginsDirectory, nativeBridge) : await sandbox.ScanAsync().ConfigureAwait(false);
            using var discoveryOwnership = new PluginDiscoveryOwnership(pluginHost, discovery);
            foreach (var skip in discovery.Skipped)
                ctx.LoggerFor("loader").Warn("%s", $"plugin skipped: {Path.GetFileName(skip.File)}: {skip.Reason}");
            foreach (var notice in pluginHost.SharedPool!.DrainNotices())
                ctx.LoggerFor("loader").Warn("%s", notice);
            var composition = await Composition.StartAsync(ctx, BuildEntries(ctx, pluginHost, settings));
            app.Composition = composition;
            ctx.LoggerFor("boot").Info("composition ready: %d plugin(s), home %s", composition.Activations.Count, home.Root);
            // 原生插件的所有权交给 manager:它在 /plugins remove、故障卸载与最终 Dispose 时统一 Deactivate + Free。
            var manager = new HarnessPluginManager(
                pluginHost, composition, home, settings, discovery.Managed, discovery.Native, discovery.Skipped, nativeBridge, pluginsDirectory, sandbox);
            app.Track(manager);
            discoveryOwnership.Transfer();
            ctx.Provide("pluginManager", manager);
        }
        catch
        {
            app.Dispose();
            throw;
        }
        return app;
    }

    public static async Task<PluginCatalogInspection> InspectPluginsAsync(HarnessOptions options, PluginRuntimeFactory? runtimeFactory = null)
    {
        options.Home.Ensure();
        var settings = HarnessSettings.Load(options.Home);
        var home = HarnessStorage.ApplyStorageRoot(options.Home, settings);
        var context = new Context();
        context.SetOwn("dshHomePath", home.Root);
        context.SetOwn("harnessOptions", options with { Home = home });
        context.SetOwn(OutboundProxy.PropName, CreateOutboundProxy(context, settings.Network));
        _ = new SafetyPolicySource(context, HarnessSettings.CreatePolicyReader(home, settings));
        var directory = options.PluginsDirectory ?? Path.Combine(AppContext.BaseDirectory, "plugins");
        var owned = new List<IDisposable>();
        try
        {
            var (host, sandbox, bridge) = CreateRuntime(runtimeFactory, context,
                new PluginExecutionEnvironment(home, settings, options.Cwd), directory, owned.Add);
            var discovered = sandbox is null ? host.Scan(directory, bridge) : await sandbox.ScanAsync().ConfigureAwait(false);
            using var discoveryOwnership = new PluginDiscoveryOwnership(host, discovered);
            return new PluginCatalogInspection(host.Catalog.Descriptors, discovered.Skipped);
        }
        finally
        {
            List<Exception>? errors = null;
            for (var index = owned.Count - 1; index >= 0; index--)
            {
                try { owned[index].Dispose(); }
                catch (Exception error) { (errors ??= []).Add(error); }
            }
            if (errors is not null)
                throw new AggregateException("plugin inspection dispose failed", errors);
        }
    }

    private static (PluginHost Host, PluginHost? Runtime, Func<INativePlugin, IDshPlugin>? NativeBridge) CreateRuntime(
        PluginRuntimeFactory? factory, Context context, PluginExecutionEnvironment environment,
        string directory, Action<IDisposable> track)
    {
        var nativeBridge = ResolveNativeBridge();
        var runtime = factory?.Invoke(context, environment, directory, nativeBridge, track);
        if (runtime is null && environment.ReadSettings().SandboxEnabled)
            throw new RuntimeException("PLUGIN_SANDBOX_UNAVAILABLE", "Plugin sandbox support is not included in this application.");
        var host = runtime ?? new PluginHost();
        host.RegisterCompiledIn();
        host.SharedPool = new SharedAssemblyPool(Path.Combine(directory, PluginHost.SharedPoolDirectoryName));
        context.SetOwn("pluginCatalog", host.Catalog);
        return (host, runtime, nativeBridge);
    }


    /** Invalid baselines retain their existing startup policy; a saved borrowed selection always starts fail-closed. */
    private static OutboundProxy CreateOutboundProxy(Context ctx, NetworkSettings? network)
    {
        var proxy = new OutboundProxy();
        try
        {
            proxy.Configure(network?.OutboundProxy?.Url, network?.OutboundProxy?.Bypass);
        }
        catch (ArgumentException error)
        {
            ctx.LoggerFor("network").Error("%s", error.Message);
        }
        if (network?.BorrowedOutboundMachine is not null)
            proxy.SuspendBorrowed();
        return proxy;
    }

    private static List<PluginEntry> BuildEntries(
        Context ctx,
        PluginHost host,
        HarnessSettings settings)
    {
        var plugins = settings.Plugins;
        var entries = new List<PluginEntry>();
        foreach (var name in host.Catalog.PackageNames.OrderBy(name => name, StringComparer.Ordinal))
        {
            var setting = plugins.GetValueOrDefault(name);
            if (setting is { Enabled: false })
                continue;
            if (!host.Catalog.TryCreateDefinition(name, out var definition))
            {
                ctx.LoggerFor("loader").Error("%s", $"plugin not found: {name}");
                continue;
            }
            entries.Add(new PluginEntry(definition!, setting is { Parameters.Count: > 0 } ? setting.Parameters : null));
        }
        foreach (var name in plugins.Keys)
        {
            if (!host.Catalog.PackageNames.Contains(name, StringComparer.Ordinal))
                ctx.LoggerFor("loader").Error("%s", $"plugin not found: {name}");
        }
        return entries;
    }

    /** 原生插件定义桥由 Dsh.Plugins.Native.Host 提供;未随本体发布时返回 null,原生插件将按跳过处理。 */
    private static Func<INativePlugin, IDshPlugin>? ResolveNativeBridge()
    {
        var bridge = FindNativeBridge();
        return bridge?.GetMethod("AsPlugin", [typeof(INativePlugin)]) is { } create
            ? plugin => (IDshPlugin)create.Invoke(null, [plugin])!
            : null;
    }

    /** 桥程序集默认不随宿主启动加载:先从已加载程序集查找,再按名加载;AOT 下已在镜像中,直接命中。 */
    private static Type? FindNativeBridge()
    {
        var bridge = AppDomain.CurrentDomain.GetAssemblies()
            .Select(assembly => assembly.GetType(NativeBridgeType))
            .FirstOrDefault(candidate => candidate is not null);
        if (bridge is not null)
            return bridge;
        try
        {
            return Assembly.Load(NativeBridgeAssembly).GetType(NativeBridgeType);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private sealed class PluginDiscoveryOwnership(PluginHost host, PluginScanResult discovery) : IDisposable
    {
        private bool _transferred;
        internal void Transfer() => _transferred = true;
        public void Dispose()
        {
            if (!_transferred)
                host.Release(discovery);
        }
    }

}
