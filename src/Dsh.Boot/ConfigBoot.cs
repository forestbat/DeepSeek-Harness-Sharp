using Cordis;
using Cordis.Loader;
using Cordis.Logging;
using Cordis.Plugins;
using Dsh.Boot.Profiles;
using Dsh.Plugins;

namespace Dsh.Boot;

public static class ConfigBoot
{
    public static async Task<HarnessApp> ComposeProfile(
        string profileName,
        IReadOnlyList<Dictionary<string, object?>>? patches,
        HarnessOptions options)
    {
        var (configPath, combinedPatches) = PrepareProfile(options.Home, profileName, patches);
        return await Compose(configPath, options, combinedPatches);
    }

    private static string ProfileConfigTemplate(string profileName)
    {
        var templateName = profileName is "sdk-minimal" or "sdk" ? "minimal" : "standard";
        var path = Path.Combine(AppContext.BaseDirectory, "Profiles", "Templates", $"{templateName}.yaml");
        var template = File.Exists(path) ? File.ReadAllText(path) : "[]\n";
        var entrypoint = profileName switch
        {
            "tui" => "@deepseek-ai/dsh-tui",
            "web" => "@deepseek-ai/dsh-web",
            "acp" => "@deepseek-ai/dsh-acp",
            "lsp" => "@deepseek-ai/dsh-lsp",
            _ => null,
        };
        if (entrypoint is not null && !template.Contains(entrypoint, StringComparison.Ordinal))
        {
            template = template.TrimEnd() + $"\n- name: \"{entrypoint}\"\n";
        }

        return template;
    }

    public static (string ConfigPath, IReadOnlyList<Dictionary<string, object?>>? Patches) PrepareProfile(
        HarnessHome home,
        string profileName,
        IReadOnlyList<Dictionary<string, object?>>? patches)
    {
        ProfileStore.InitProfile(home, profileName);
        var profileDir = ProfileStore.ResolveProfileDir(home, profileName);
        var combined = new List<Dictionary<string, object?>>();
        AddPatches(Path.Combine(profileDir, "cordis.patch.yml"));
        AddPatches(Path.Combine(home.Root, "cordis.patch.yml"));
        if (patches is { Count: > 0 })
            combined.AddRange(patches);

        var configPath = Path.Combine(profileDir, "cordis.yml");
        if (!File.Exists(configPath))
            File.WriteAllText(configPath, ProfileConfigTemplate(profileName));
        return (configPath, combined.Count == 0 ? null : combined);

        void AddPatches(string path)
        {
            if (File.Exists(path))
                combined.AddRange(LoadPatches(path));
        }
    }

    public static async Task<HarnessApp> Compose(string configPath, HarnessOptions options, IReadOnlyList<Dictionary<string, object?>>? patches = null)
    {
        var fullPath = Path.GetFullPath(configPath);
        if (!File.Exists(fullPath))
            throw new CordisException("CONFIG_NOT_FOUND", $"config file not found: {fullPath}");
        var configDirectory = Path.GetDirectoryName(fullPath)!;

        options.Home.Ensure();
        var credentials = new EnvCredentials(options.Home, options.Cwd);
        var ctx = new Context { BaseUrl = new Uri(configDirectory + "/").AbsoluteUri };
        ctx.SetOwn("dshHomePath", options.Home.Root);
        ctx.SetOwn("credentials", credentials);
        ctx.SetOwn("harnessOptions", options);

        var pluginHost = new PluginHost();
        pluginHost.ScanDirectory(AppContext.BaseDirectory);
        ctx.SetOwn("pluginCatalog", pluginHost.Catalog);

        var loader = new Loader(ctx);
        loader.Builtins["group"] = new PluginDefinition
        {
            Name = "group",
            Callback = new DelegatePluginCallback((pluginCtx, config) => ConstructGroup(pluginCtx, config)),
        };
        loader.Builtins["include"] = new PluginDefinition
        {
            Name = "include",
            Callback = new DelegatePluginCallback((pluginCtx, config) => ConstructInclude(pluginCtx, config)),
        };
        loader.Builtins["timer"] = typeof(TimerService);
        loader.Builtins["logger-console"] = PluginDefinition.From((pluginCtx, config) =>
        {
            _ = new ConsoleExporter(pluginCtx);
            return null;
        }, "logger-console");
        loader.Importer = new DshModuleImporter(pluginHost.Catalog);

        var settings = HarnessSettings.Load(options.Home);
        var defaultModel = settings.ResolveDefaultModel();
        var provider = options.Provider ?? defaultModel?.Provider ?? HarnessComposer.DefaultProvider;
        var model = options.Model ?? defaultModel?.Model ?? HarnessComposer.DefaultModel;

        var app = new HarnessApp
        {
            Ctx = ctx,
            Home = options.Home,
            Credentials = credentials,
            Provider = provider,
            Model = model,
            ReasoningEffort = options.ReasoningEffort,
        };

        try
        {
            var includeConfig = new Dictionary<string, object?>
            {
                ["path"] = Path.GetFileName(fullPath),
            };
            if (patches is { Count: > 0 })
                includeConfig["patches"] = patches.ToList();
            var fiber = ctx.Plugin(loader.Builtins["include"]!, includeConfig);
            await fiber.Await();
            await loader.Await();
            await ThrowOnActivationFailures(ctx);
            var providerRegistration = RegisterProviderAdapters(ctx, options);
            if (providerRegistration is not null)
                app.Track(providerRegistration);
        }
        catch
        {
            app.Dispose();
            throw;
        }
        return app;
    }

    private static IDisposable? RegisterProviderAdapters(Context ctx, HarnessOptions options)
    {
        var type = AppDomain.CurrentDomain.GetAssemblies()
            .Select(assembly => assembly.GetType("Dsh.Interaction.ProviderBootstrapper"))
            .FirstOrDefault(candidate => candidate is not null);
        if (type is null)
            return null;
        return (IDisposable)type.GetMethod("Register", [typeof(Context), typeof(HarnessOptions)])!
            .Invoke(null, [ctx, options])!;
    }

    // app-boot 的 fail-loud 语义:apply 失败的 fiber 与日志里的错误(import 失败只进日志)聚合后一次抛出。
    // include 的条目树不在 loader.Store 里,loader.Await() 覆盖不到插件 fiber,因此这里自行等 fiber 静默。
    private static async Task ThrowOnActivationFailures(Context ctx)
    {
        var failures = new List<string>();
        var seen = new HashSet<Fiber>(ReferenceEqualityComparer.Instance);
        var cleanPasses = 0;
        while (true)
        {
            IReadOnlyList<Fiber> fibers;
            try
            {
                fibers = ctx.Registry.Values().SelectMany(runtime => runtime.Fibers).ToList();
            }
            catch (InvalidOperationException)
            {
                await Task.Delay(10);
                continue;
            }
            var pending = fibers.Where(fiber => seen.Add(fiber) || fiber.Inertia is not null).ToList();
            foreach (var fiber in pending)
            {
                try
                {
                    await fiber.Await();
                }
                catch (Exception error)
                {
                    failures.Add($"  - plugin <{fiber.Name}>: {DeepestMessage(error)}");
                }
            }
            if (pending.Count > 0)
            {
                cleanPasses = 0;
                continue;
            }
            cleanPasses++;
            if (cleanPasses >= 2)
                break;
            await Task.Delay(50);
        }
        foreach (var message in ctx.Root.Logger.Buffer)
        {
            if (message.Type == LoggerType.Error)
                failures.Add($"  - log <{message.Name}>: {string.Join(' ', message.Args.Select(arg => arg?.ToString()))}");
        }
        if (failures.Count > 0)
        {
            throw new CordisException("BOOT_FAILED",
                $"config boot failed with {failures.Count} activation error(s):\n{string.Join('\n', failures)}");
        }
    }

    private static object? ConstructInclude(Context pluginCtx, object? config)
    {
        var include = new Include(pluginCtx, config);
        return include is IAsyncInit init ? init.Init() : null;
    }

    private static object? ConstructGroup(Context pluginCtx, object? config)
    {
        var group = new Group(pluginCtx, config);
        return group is IAsyncInit init ? init.Init() : null;
    }

    private static string DeepestMessage(Exception error)
    {
        while (error.InnerException is not null) error = error.InnerException;
        return error.Message;
    }

    public static IReadOnlyList<Dictionary<string, object?>> LoadPatches(string path)
    {
        var fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath))
            throw new CordisException("PATCHES_NOT_FOUND", $"patch file not found: {fullPath}");
        var parsed = YamlConfig.Load(File.ReadAllText(fullPath));
        if (parsed is not List<object?> list)
            throw new CordisException("INVALID_PATCHES", $"patch file must contain a YAML list: {fullPath}");
        return list.OfType<Dictionary<string, object?>>().ToList();
    }

    private static object? ConvertNode(object? node)
    {
        switch (node)
        {
            case Dictionary<object, object> map:
                return map.ToDictionary(pair => pair.Key.ToString() ?? "", pair => ConvertNode(pair.Value));
            case List<object> items:
                return items.Select(ConvertNode).ToList();
            default:
                return node;
        }
    }
}
