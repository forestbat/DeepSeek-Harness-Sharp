using Cordis;
using Dsh.Interaction;
using Dsh.Plugins;

[assembly: DshPlugin(Dsh.Skills.Plugin.Skill)]
[assembly: DshPlugin(Dsh.Skills.Plugin.SkillFilesystem)]

namespace Dsh.Skills;

public sealed class Plugin(string packageName) : IDshPlugin
{
    internal const string Skill = "@deepseek-ai/dsh-skill";
    internal const string SkillFilesystem = "@deepseek-ai/dsh-skill-filesystem";

    public string[] Inject => packageName switch
    {
        Skill => [CommandsService.ServiceName],
        SkillFilesystem => [SkillRegistry.ServiceName],
        _ => throw new InvalidOperationException($"Unknown DSH package '{packageName}'."),
    };

    public IDisposable Apply(Context ctx, object? config) => packageName switch
    {
        Skill => RegisterSkillRegistry(ctx, config),
        SkillFilesystem => global::Dsh.Skills.SkillFilesystem.Apply(ctx, SkillFilesystemConfigFrom(config)),
        _ => throw new InvalidOperationException($"Unknown DSH package '{packageName}'."),
    };

    private static IDisposable RegisterSkillRegistry(Context ctx, object? config)
    {
        _ = new SkillRegistry(ctx, SkillRegistryConfigFrom(config));
        return new DisposableBundle(new NoopDisposable(), SkillCommand.Register(ctx));
    }

    private static IReadOnlyDictionary<string, object?>? ConfigOf(object? config)
        => config as IReadOnlyDictionary<string, object?>;

    private static SkillRegistryConfig SkillRegistryConfigFrom(object? config)
    {
        var dict = ConfigOf(config);
        return new SkillRegistryConfig
        {
            CollectCacheMaxEntries = IntOf(dict, "collectCacheMaxEntries") ?? new SkillRegistryConfig().CollectCacheMaxEntries,
        };
    }

    private static SkillFilesystemConfig SkillFilesystemConfigFrom(object? config)
    {
        var dict = ConfigOf(config);
        return new SkillFilesystemConfig
        {
            ProviderName = dict?.GetValueOrDefault("providerName") as string ?? new SkillFilesystemConfig().ProviderName,
            IncludeDefaultRoots = dict?.GetValueOrDefault("includeDefaultRoots") as bool? ?? new SkillFilesystemConfig().IncludeDefaultRoots,
            DshHome = dict?.GetValueOrDefault("dshHome") as string,
            AgentsHome = dict?.GetValueOrDefault("agentsHome") as string,
            CustomSkillDirs = StringListOf(dict?.GetValueOrDefault("customSkillDirs")),
            Watch = dict?.GetValueOrDefault("watch") as bool? ?? new SkillFilesystemConfig().Watch,
            WatchUsePolling = dict?.GetValueOrDefault("watchUsePolling") as bool? ?? new SkillFilesystemConfig().WatchUsePolling,
            WatchStabilityThresholdMs = IntOf(dict, "watchStabilityThresholdMs") ?? new SkillFilesystemConfig().WatchStabilityThresholdMs,
            WatchPollIntervalMs = IntOf(dict, "watchPollIntervalMs") ?? new SkillFilesystemConfig().WatchPollIntervalMs,
            WatchMaxProjects = IntOf(dict, "watchMaxProjects") ?? new SkillFilesystemConfig().WatchMaxProjects,
            WatchFollowSymlinks = dict?.GetValueOrDefault("watchFollowSymlinks") as bool? ?? new SkillFilesystemConfig().WatchFollowSymlinks,
            BundledSkillDir = dict?.GetValueOrDefault("bundledSkillDir") as string,
        };
    }

    private static int? IntOf(IReadOnlyDictionary<string, object?>? dict, string key)
        => dict?.GetValueOrDefault(key) switch
        {
            long value => (int)value,
            int value => value,
            _ => null,
        };

    private static IReadOnlyList<string>? StringListOf(object? value)
        => value is IEnumerable<object?> items
            ? items.OfType<string>().ToList()
            : null;

    private sealed class NoopDisposable : IDisposable
    {
        public void Dispose()
        {
        }
    }

    private sealed class DisposableBundle(params IDisposable[] disposables) : IDisposable
    {
        public void Dispose()
        {
            foreach (var disposable in disposables)
                disposable.Dispose();
        }
    }
}