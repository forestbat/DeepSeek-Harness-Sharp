using System.ComponentModel;
using Dsh.Runtime;

namespace Dsh.Plugins;

/** Host execution grants full in-process authority; sandbox execution uses the capability SDK. */
public enum PluginExecution
{
    Sandbox,
    Host,
}

/** settings.yaml 的 plugins 条目:布尔开关或 {enabled, ...参数} 形式。 */
public sealed record PluginSetting
{
    public const string EnabledKey = "enabled";
    public const string PermissionsKey = "permissions";
    public const string ExecutionKey = "execution";

    [DefaultValue(true)]
    public bool Enabled { get; init; } = true;
    public Dictionary<string, object?> Parameters { get; init; } = [];

    /** Reserved key beside enabled with read/write/network booleans; null (key omitted) allows everything. */
    public PluginPermissions? Permissions { get; init; }

    public PluginExecution? Execution { get; init; }

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
        parameters.Remove(ExecutionKey, out var execution);
        var role = execution switch
        {
            null => (PluginExecution?)null,
            "sandbox" => PluginExecution.Sandbox,
            "host" => PluginExecution.Host,
            _ => throw new RuntimeException("INVALID_PLUGIN_SETTING", $"plugins.<name>.{ExecutionKey} must be sandbox or host"),
        };
        return new PluginSetting
        {
            Enabled = enabled,
            Parameters = parameters,
            Permissions = permissions is null ? null : PluginPermissions.FromRaw(permissions),
            Execution = role,
        };
    }
}

/**
 * The plugins."<package>".permissions mapping: read, write and network flags, each allowed unless set to false.
 */
public sealed record PluginPermissions
{
    public static readonly PluginPermissions AllAllowed = new();

    [DefaultValue(true)]
    public bool Read { get; init; } = true;
    [DefaultValue(true)]
    public bool Write { get; init; } = true;
    [DefaultValue(true)]
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
