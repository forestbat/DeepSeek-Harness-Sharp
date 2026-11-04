using Dsh.Plugins;
using Dsh.Runtime;
using Dsh.Runtime.Composition;

namespace Dsh.Boot;

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
