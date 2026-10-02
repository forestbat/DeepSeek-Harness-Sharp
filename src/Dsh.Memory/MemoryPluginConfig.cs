namespace Dsh.Memory;

/** 记忆插件的参数面:全部来自 plugins."@deepseek-ai/dsh-memory" 段。
 *  启用/禁用即插件的 enabled(由 /memory on|off 驱动);capture 控制回合末自动捕获,可独立于插件启用状态关闭。 */
public sealed record MemoryPluginConfig
{
    public const string PackageName = "@deepseek-ai/dsh-memory";

    public string? Backend { get; init; }

    public string? File { get; init; }

    public bool Capture { get; init; } = true;

    public MemoryMongoConfig Mongo { get; init; } = new();

    public static MemoryPluginConfig Resolve(object? config)
    {
        if (config is not IReadOnlyDictionary<string, object?> map)
            return new MemoryPluginConfig();
        return new MemoryPluginConfig
        {
            Backend = StringOf(map, "backend"),
            File = StringOf(map, "file"),
            Capture = !map.TryGetValue("capture", out var capture) || capture is not bool flag || flag,
            Mongo = MemoryMongoConfig.Resolve(map.TryGetValue("mongo", out var mongo) ? mongo : null),
        };
    }

    /** 回合末捕获的开关随 settings 即时重读(/memory on|off 之外的独立维度)。 */
    public static bool CaptureEnabled(Boot.HarnessSettings settings)
    {
        if (!settings.Plugins.TryGetValue(PackageName, out var setting))
            return true;
        return !setting.Parameters.TryGetValue("capture", out var capture) || capture is not bool flag || flag;
    }

    internal static string? StringOf(IReadOnlyDictionary<string, object?> map, string key)
        => map.TryGetValue(key, out var value) ? value as string : null;
}

public sealed record MemoryMongoConfig
{
    public string ConnectionString { get; init; } = "mongodb://localhost:27017";

    public string Database { get; init; } = "dsh_memory";

    public string Collection { get; init; } = "memory";

    public string? Key { get; init; }

    public static MemoryMongoConfig Resolve(object? raw)
    {
        if (raw is not IReadOnlyDictionary<string, object?> map)
            return new MemoryMongoConfig();
        return new MemoryMongoConfig
        {
            ConnectionString = MemoryPluginConfig.StringOf(map, "connectionString") ?? "mongodb://localhost:27017",
            Database = MemoryPluginConfig.StringOf(map, "database") ?? "dsh_memory",
            Collection = MemoryPluginConfig.StringOf(map, "collection") ?? "memory",
            Key = MemoryPluginConfig.StringOf(map, "key"),
        };
    }
}
