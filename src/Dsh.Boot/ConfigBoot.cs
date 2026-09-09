using Cordis;
using Cordis.Loader;
using Cordis.Logging;
using Cordis.Plugins;
using Dsh.Core;
using Dsh.Interaction;
using Dsh.Persistence;
using Dsh.Plugins;
using Dsh.Tools;
using Dsh.Boot.Profiles;

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
        return File.Exists(path) ? File.ReadAllText(path) : "[]\n";
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
        ctx.Provide("dshHomePath", options.Home.Root);
        ctx.Provide("credentials", credentials);

        var persistence = new JsonlSessionPersistence(options.Home.SessionsPath);
        _ = new SessionStore(ctx);
        _ = new SystemPrompt(ctx, new SystemPromptConfig());
        _ = new ToolRuntime(ctx);
        var llm = new LlmRuntime(ctx);
        _ = new AgentRegistry(ctx);
        _ = new AgentLoop(ctx, new AgentLoopConfig(), _ => persistence);
        _ = new SubprocessService(ctx);
        _ = new LocalFsService(ctx, new LocalFsConfig { Cwd = options.Cwd });
        _ = ApprovalService.Register(ctx);
        _ = UserQuestionService.Register(ctx);
        _ = CommandsService.Register(ctx);
        var modelCommand = ModelCommand.Register(ctx, options.Home);
        var reasoningCommand = ReasoningCommand.Register(ctx);
        var providerCommand = ProviderCommand.Register(ctx, options.Home);
        var goalCommand = GoalCommand.Register(ctx);
        var skillCommand = SkillCommand.Register(ctx);
        var memoryCommand = MemoryCommand.Register(ctx, options);
        var sessionCommand = SessionCommand.Register(ctx, persistence);
        var pluginHost = new PluginHost();
        pluginHost.ScanDirectory(AppContext.BaseDirectory);
        var pluginCommand = PluginCommand.Register(ctx, pluginHost.Catalog);
        var mcpCommand = McpCommand.Register(ctx, options.Home);
        var settings = HarnessSettings.Load(options.Home);
        var safetyGuard = SafetyCommandGuard.Register(ctx, settings.Safety);
        var defaultModel = settings.ResolveDefaultModel();
        var provider = options.Provider ?? defaultModel?.Provider ?? HarnessComposer.DefaultProvider;
        var model = options.Model ?? defaultModel?.Model ?? HarnessComposer.DefaultModel;
        var reasoningEffort = options.ReasoningEffort;
        var providerSettings = settings.ResolveProvider(provider);
        var registration = HarnessComposer.RegisterProviderAdapter(
            ctx,
            provider,
            providerSettings,
            options.BaseUrl ?? providerSettings?.Options?.BaseUrl ?? HarnessComposer.DefaultBaseUrl,
            options.ApiKeyEnv ?? providerSettings?.Options?.ApiKeyEnv ?? HarnessComposer.DefaultApiKeyEnv,
            options.ApiKey ?? providerSettings?.Options?.ApiKey,
            options,
            credentials,
            llm);

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

        var app = new HarnessApp
        {
            Ctx = ctx,
            Home = options.Home,
            Credentials = credentials,
            Persistence = persistence,
            Provider = provider,
            Model = model,
            ReasoningEffort = reasoningEffort,
        };
        app.Track(registration);
        app.Track(modelCommand);
        app.Track(reasoningCommand);
        app.Track(providerCommand);
        app.Track(goalCommand);
        app.Track(skillCommand);
        app.Track(memoryCommand);
        app.Track(sessionCommand);
        app.Track(pluginCommand);
        app.Track(mcpCommand);
        app.Track(safetyGuard);
        HarnessComposer.WirePersistence(ctx, persistence);

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
        }
        catch
        {
            app.Dispose();
            throw;
        }
        return app;
    }

    // app-boot 的 fail-loud 语义:apply 失败的 fiber 与日志里的错误(import 失败只进日志)聚合后一次抛出。
    // include 的条目树不在 loader.Store 里,loader.Await() 覆盖不到插件 fiber,因此这里自行等 fiber 静默。
    private static async Task ThrowOnActivationFailures(Context ctx)
    {
        var failures = new List<string>();
        var seen = new HashSet<Fiber>(ReferenceEqualityComparer.Instance);
        // JS 插件的 fiber 经 RPC 异步创建,可能晚于一次扫描;干净扫描后短暂等待再确认一次,避免注册竞争漏等。
        var cleanPasses = 0;
        while (true)
        {
            var fibers = ctx.Registry.Values().SelectMany(runtime => runtime.Fibers).ToList();
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
        var parsed = new YamlDotNet.Serialization.DeserializerBuilder()
            .WithAttemptingUnquotedStringTypeDeserialization()
            .Build()
            .Deserialize<object>(File.ReadAllText(fullPath));
        if (parsed is not List<object> list)
            throw new CordisException("INVALID_PATCHES", $"patch file must contain a YAML list: {fullPath}");
        return list.Select(ConvertNode).OfType<Dictionary<string, object?>>().ToList();
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
