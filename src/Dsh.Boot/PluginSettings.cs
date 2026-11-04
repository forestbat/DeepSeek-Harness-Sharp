using Dsh.Plugins;
using Dsh.Runtime;
using Dsh.Runtime.Composition;

namespace Dsh.Boot;

/** settings.yaml 的 plugins 条目:布尔开关或 {enabled, ...参数} 形式。 */
public sealed record PluginSetting
{
    public const string EnabledKey = "enabled";
    public const string PermissionsKey = "permissions";

    public bool Enabled { get; init; } = true;
    public Dictionary<string, object?> Parameters { get; init; } = [];

    /** Reserved key beside enabled with read/write/network booleans; null (key omitted) allows everything. */
    public PluginPermissions? Permissions { get; init; }

    public static PluginSetting FromRaw(object? raw) => raw switch
    {
        null => new PluginSetting(),
        bool enabled => new PluginSetting { Enabled = enabled },
        IDictionary<string, object?> map => FromMap(map),
        _ => throw new RuntimeException("INVALID_PLUGIN_SETTING",
            $"plugin setting must be a boolean or a mapping, got {raw.GetType().Name}"),
    };

    private static PluginSetting FromMap(IDictionary<string, object?> map)
    {
        var enabled = true;
        if (map.TryGetValue(EnabledKey, out var value) && value is not null)
        {
            enabled = value is bool flag
                ? flag
                : throw new RuntimeException("INVALID_PLUGIN_SETTING", $"plugins.<name>.{EnabledKey} must be a boolean");
        }
        var parameters = new Dictionary<string, object?>(map, StringComparer.Ordinal);
        parameters.Remove(EnabledKey);
        parameters.Remove(PermissionsKey, out var permissions);
        return new PluginSetting
        {
            Enabled = enabled,
            Parameters = parameters,
            Permissions = permissions is null ? null : PluginPermissions.FromRaw(permissions),
        };
    }
}

/**
 * The plugins."<package>".permissions mapping: read, write and network flags, each allowed unless set to false.
 */
public sealed record PluginPermissions
{
    public static readonly PluginPermissions AllAllowed = new();

    public bool Read { get; init; } = true;
    public bool Write { get; init; } = true;
    public bool Network { get; init; } = true;

    public bool Allows(PluginPermission permission) => permission switch
    {
        PluginPermission.Read => Read,
        PluginPermission.Write => Write,
        _ => Network,
    };

    public PluginPermissions With(PluginPermission permission, bool allowed) => permission switch
    {
        PluginPermission.Read => this with { Read = allowed },
        PluginPermission.Write => this with { Write = allowed },
        _ => this with { Network = allowed },
    };

    /** The settings.yaml key of a permission, e.g. "write". */
    public static string KeyOf(PluginPermission permission) => permission switch
    {
        PluginPermission.Read => "read",
        PluginPermission.Write => "write",
        _ => "network",
    };

    private static readonly Dictionary<string, PluginPermission> ByKey =
        Enum.GetValues<PluginPermission>().ToDictionary(KeyOf, StringComparer.Ordinal);

    /** Strict parse: a typo such as "wirte: false" must fail loudly instead of leaving the permission allowed. */
    public static PluginPermissions FromRaw(object raw)
    {
        if (raw is not IDictionary<string, object?> map)
            throw Invalid($"plugins.<name>.{PluginSetting.PermissionsKey} must be a mapping of read/write/network booleans");
        var permissions = AllAllowed;
        foreach (var (key, value) in map)
        {
            if (!ByKey.TryGetValue(key, out var permission))
                throw Invalid($"unknown plugin permission \"{key}\" (expected read, write or network)");
            if (value is not bool allowed)
                throw Invalid($"plugins.<name>.{PluginSetting.PermissionsKey}.{key} must be a boolean");
            permissions = permissions.With(permission, allowed);
        }
        return permissions;
    }

    private static RuntimeException Invalid(string message) => new("INVALID_PLUGIN_SETTING", message);
}

/** settings.yaml 的 plugins 段:用户在此禁用插件或保存参数;未列出的插件发现即启用。 */
public static class PluginSettingsSection
{
    public const string PluginsKey = "plugins";
    private static readonly Lazy<Dictionary<string, PluginSetting>> TemplateSettings = new(() => ParseDocument(HarnessSettings.LoadTemplate()));

    public static Dictionary<string, object?> DefaultParameters(string name)
        => TemplateSettings.Value.GetValueOrDefault(name)?.Parameters.ToDictionary(pair => pair.Key, pair => CloneValue(pair.Value), StringComparer.Ordinal) ?? [];

    public static object? DefaultParameter(string name, string key)
        => CloneValue(TemplateSettings.Value.GetValueOrDefault(name)?.Parameters.GetValueOrDefault(key));

    private static object? CloneValue(object? value) => value switch
    {
        IReadOnlyDictionary<string, object?> map => map.ToDictionary(pair => pair.Key, pair => CloneValue(pair.Value), StringComparer.Ordinal),
        IEnumerable<object?> items => items.Select(CloneValue).ToArray(),
        _ => value,
    };

    public static Dictionary<string, PluginSetting> ParseDocument(string yaml)
    {
        var root = YamlConfig.LoadMapping(yaml);
        return root.TryGetValue(PluginsKey, out var raw) ? Parse(raw) : new Dictionary<string, PluginSetting>(StringComparer.Ordinal);
    }

    private static Dictionary<string, PluginSetting> Parse(object? raw)
    {
        if (raw is not IDictionary<string, object?> map)
            return new Dictionary<string, PluginSetting>(StringComparer.Ordinal);
        var plugins = new Dictionary<string, PluginSetting>(StringComparer.Ordinal);
        foreach (var (name, value) in map)
            plugins[name] = PluginSetting.FromRaw(value);
        return plugins;
    }
}
