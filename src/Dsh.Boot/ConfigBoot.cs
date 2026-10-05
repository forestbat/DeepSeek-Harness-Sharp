using Dsh.Runtime;
using Dsh.Runtime.Composition;
using Dsh.Runtime.Logging;
using Dsh.Plugins;
using System.Reflection;

namespace Dsh.Boot;

public static class ConfigBoot
{
    private const string NativeBridgeType = "Dsh.Plugins.Native.Host.NativePluginBridge";
    private const string NativeBridgeAssembly = "Dsh.Plugins.Native.Host";

    public static async Task<HarnessApp> Compose(HarnessOptions options)
    {
        await Task.Yield();
        options.Home.Ensure();
        var settings = HarnessSettings.Load(options.Home);
        var logging = LoggingSetup.Create(settings.Logging?.ToOptions(), options.Home.LogsPath, allowConsole: !options.IsTui);
        var credentials = new EnvCredentials(options.Home, options.Cwd);
        var ctx = new Context(logging);
        ctx.SetOwn("dshHomePath", options.Home.Root);
        ctx.SetOwn("credentials", credentials);
        ctx.SetOwn("harnessOptions", options);

        var pluginHost = new PluginHost();
        pluginHost.RegisterCompiledIn();
        var pluginsDirectory = options.PluginsDirectory ?? Path.Combine(AppContext.BaseDirectory, "plugins");
        pluginHost.SharedPool = new SharedAssemblyPool(Path.Combine(pluginsDirectory, ".shared"));
        var discovery = pluginHost.Scan(pluginsDirectory, ResolveNativeBridge());
        foreach (var skip in discovery.Skipped)
            ctx.LoggerFor("loader").Warn("%s", $"plugin skipped: {Path.GetFileName(skip.File)}: {skip.Reason}");
        foreach (var notice in pluginHost.SharedPool.DrainNotices())
            ctx.LoggerFor("loader").Warn("%s", notice);
        ctx.SetOwn("pluginCatalog", pluginHost.Catalog);

        // 零配置不兜底:没配 provider/model 就留空,启动照常,首次发起 LLM 请求时才报错。
        var defaultModel = settings.ResolveDefaultModel();
        var provider = options.Provider ?? defaultModel?.Provider ?? "";
        var model = options.Model ?? defaultModel?.Model ?? "";

        var app = new HarnessApp
        {
            Ctx = ctx,
            Home = options.Home,
            Credentials = credentials,
            Provider = provider,
            Model = model,
            ReasoningEffort = options.ReasoningEffort,
        };
        app.Track(logging);
        if (discovery.Native.Count > 0)
            app.Track(new PluginDisposables(discovery.Native));

        try
        {
            var composition = await Composition.StartAsync(ctx, BuildEntries(ctx, pluginHost, settings));
            app.Composition = composition;
            ctx.LoggerFor("boot").Info("composition ready: %d plugin(s), home %s", composition.Activations.Count, options.Home.Root);
            var manager = new HarnessPluginManager(pluginHost, composition, options.Home, settings, discovery.Managed, pluginsDirectory);
            app.Track(manager);
            ctx.Provide("pluginManager", manager);
        }
        catch
        {
            app.Dispose();
            throw;
        }
        return app;
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

    private sealed class PluginDisposables(IReadOnlyList<INativePlugin> plugins) : IDisposable
    {
        public void Dispose()
        {
            foreach (var plugin in plugins)
                plugin.Dispose();
        }
    }
}
