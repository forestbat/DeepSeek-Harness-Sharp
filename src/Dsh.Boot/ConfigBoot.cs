using Cordis;
using Cordis.Loader;
using Cordis.Node;
using Dsh.Core;
using Dsh.Persistence;
using Dsh.Tools;

namespace Dsh.Boot;

public static class ConfigBoot
{
    public static async Task<HarnessApp> Compose(string configPath, HarnessOptions options, string? nodeExecutable = null, IReadOnlyList<Dictionary<string, object?>>? patches = null)
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
        _ = Dsh.Interaction.ApprovalService.Register(ctx);
        _ = Dsh.Interaction.UserQuestionService.Register(ctx);
        _ = Dsh.Interaction.CommandsService.Register(ctx);
        var registration = HarnessComposer.RegisterDeepSeekAdapter(ctx, options, credentials, llm);

        var host = NodeHost.Start(nodeExecutable);
        var loader = new Loader(ctx);
        loader.Attach(host);
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
        loader.Importer = new DshModuleImporter(new NodeImporter(host));

        var app = new HarnessApp
        {
            Ctx = ctx,
            Home = options.Home,
            Credentials = credentials,
            Persistence = persistence,
            Provider = options.Provider ?? HarnessComposer.DefaultProvider,
            Model = options.Model ?? HarnessComposer.DefaultModel,
        };
        app.Track(registration);
        app.Track(host);
        HarnessComposer.WirePersistence(ctx, persistence);

        try
        {
            var includeConfig = new Dictionary<string, object?>
            {
                ["path"] = Path.GetFileName(fullPath),
            };
            if (patches is { Count: > 0 })
                includeConfig["patches"] = patches.ToList();
            var fiber = ctx.Plugin(loader.Builtins["include"], includeConfig);
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
            if (pending.Count == 0) break;
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
