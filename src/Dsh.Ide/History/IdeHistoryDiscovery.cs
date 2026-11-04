using Dsh.Ide.Catalog;

namespace Dsh.Ide.History;

public sealed record IdeHistoryDiscovery
{
    public IReadOnlyDictionary<string, string[]> Roots { get; init; } = new Dictionary<string, string[]>();
    [global::Dsh.Plugins.DshPluginSettings(Name = "store_paths")]
    public IReadOnlyList<string> StorePaths { get; init; } = [];
    [global::Dsh.Plugins.DshPluginSettings(Name = "settings_file")]
    public string? SettingsFile { get; init; }

    internal static IdeHistoryDiscovery From(object? value)
    {
        var map = ConfigMap.Read(value);
        return new()
        {
            Roots = EditorDiscoveryPaths.Read(map.GetValueOrDefault("roots")),
            StorePaths = ConfigMap.Strings(map.GetValueOrDefault("store_paths")),
            SettingsFile = ConfigMap.Text(map, "settings_file"),
        };
    }
}
