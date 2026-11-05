using Dsh.Runtime;
using Dsh.Boot;
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
        // SettingsSkillPaths 让顶层 skills.paths 与 skills.urls 都能生效, 两个提供者各管一摊。
        SkillFilesystem => global::Dsh.Skills.SkillFilesystem.Apply(ctx, SkillFilesystemConfigFrom(config, ctx), RemoteConfig(ctx, config)),
        _ => throw new InvalidOperationException($"Unknown DSH package '{packageName}'."),
    };

    private static IDisposable RegisterSkillRegistry(Context ctx, object? config)
    {
        var registry = new SkillRegistry(ctx, SkillRegistryConfigFrom(config));
        return new DisposableBundle(new SkillCatalog(ctx, registry), SkillCommand.Register(ctx));
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

    private static SkillFilesystemConfig SkillFilesystemConfigFrom(object? config, Context ctx)
    {
        var dict = ConfigOf(config);
        var customDirs = (StringListOf(dict?.GetValueOrDefault("customSkillDirs")) ?? []).ToList();
        foreach (var path in SettingsSkillPaths(ctx))
        {
            if (!customDirs.Contains(path, StringComparer.OrdinalIgnoreCase))
                customDirs.Add(path);
        }
        return new SkillFilesystemConfig
        {
            ProviderName = dict?.GetValueOrDefault("providerName") as string ?? new SkillFilesystemConfig().ProviderName,
            IncludeDefaultRoots = dict?.GetValueOrDefault("includeDefaultRoots") as bool? ?? new SkillFilesystemConfig().IncludeDefaultRoots,
            DshHome = dict?.GetValueOrDefault("dshHome") as string,
            AgentsHome = dict?.GetValueOrDefault("agentsHome") as string,
            CustomSkillDirs = customDirs,
            Watch = dict?.GetValueOrDefault("watch") as bool? ?? new SkillFilesystemConfig().Watch,
            WatchUsePolling = dict?.GetValueOrDefault("watchUsePolling") as bool? ?? new SkillFilesystemConfig().WatchUsePolling,
            WatchStabilityThresholdMs = IntOf(dict, "watchStabilityThresholdMs") ?? new SkillFilesystemConfig().WatchStabilityThresholdMs,
            WatchPollIntervalMs = IntOf(dict, "watchPollIntervalMs") ?? new SkillFilesystemConfig().WatchPollIntervalMs,
            WatchMaxProjects = IntOf(dict, "watchMaxProjects") ?? new SkillFilesystemConfig().WatchMaxProjects,
            WatchFollowSymlinks = dict?.GetValueOrDefault("watchFollowSymlinks") as bool? ?? new SkillFilesystemConfig().WatchFollowSymlinks,
            BundledSkillDir = dict?.GetValueOrDefault("bundledSkillDir") as string,
        };
    }

    private static IReadOnlyList<string> SettingsSkillPaths(Context ctx)
    {
        if (ctx.GetProp("dshHomePath") is not string homePath)
            return [];
        try
        {
            return HarnessSettings.Load(new HarnessHome(homePath)).Skills?.Paths ?? [];
        }
        catch (Exception error)
        {
            ctx.LoggerFor(SkillFilesystem).Warn($"failed to read skills.paths from settings: {error.Message}");
            return [];
        }
    }

    /** skills.urls 下发的远程技能默认不启用: 只有 skills.enabled 里点名的名字才会进入技能目录。 */
    private static SkillRemoteConfig RemoteConfig(Context ctx, object? config)
    {
        var dict = ConfigOf(config);
        var urls = (StringListOf(dict?.GetValueOrDefault("remoteUrls")) ?? []).ToList();
        var enabled = (StringListOf(dict?.GetValueOrDefault("enabledRemoteSkills")) ?? []).ToList();
        if (ctx.GetProp("dshHomePath") is string homePath)
        {
            try
            {
                var skills = HarnessSettings.Load(new HarnessHome(homePath)).Skills;
                urls.AddRange(skills?.Urls ?? []);
                enabled.AddRange(skills?.Enabled ?? []);
            }
            catch (Exception error)
            {
                ctx.LoggerFor(SkillFilesystem).Warn($"failed to read skills.urls from settings: {error.Message}");
            }
        }
        return new SkillRemoteConfig
        {
            Urls = [.. urls.Distinct(StringComparer.OrdinalIgnoreCase)],
            Enabled = [.. enabled.Distinct(StringComparer.Ordinal)],
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

    private sealed class DisposableBundle(params IDisposable[] disposables) : IDisposable
    {
        public void Dispose()
        {
            foreach (var disposable in disposables)
                disposable.Dispose();
        }
    }
}