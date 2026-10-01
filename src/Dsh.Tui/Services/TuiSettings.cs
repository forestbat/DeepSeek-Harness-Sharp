using Dsh.Boot;

namespace Dsh.Tui.Services;

/**
 * TUI 插件自己的参数段: settings.yaml -> plugins."@deepseek-ai/dsh-tui"。
 * 与 GuiSettings 同构: 参数缺失一律退回默认值(不因手改配置把界面卡住), 写回走 HarnessSettings.SavePlugins, 保留文件其余内容。
 */
public sealed class TuiSettings(HarnessHome home)
{
    public const string Package = "@deepseek-ai/dsh-tui";

    /** 侧栏宽度的绝对上限(字符列): 再宽也没有意义, 用来兜住手改配置里的离谱值; 实际还会按控制台宽度再夹一次。 */
    public const int MaximumSidebarWidth = 120;

    public TuiSettingsSnapshot Load()
        => TuiSettingsSnapshot.From(Parameters());

    public void Save(TuiSettingsSnapshot snapshot)
    {
        var settings = HarnessSettings.Load(home);
        var existing = settings.Plugins.GetValueOrDefault(Package);
        settings.Plugins[Package] = new PluginSetting
        {
            Enabled = existing?.Enabled ?? true,
            Parameters = snapshot.ToParameters(),
        };
        settings.SavePlugins(home);
    }

    private Dictionary<string, object?> Parameters()
        => HarnessSettings.Load(home).Plugins.GetValueOrDefault(Package)?.Parameters ?? [];
}

/** 参数快照: 与 GuiSettingsSnapshot 一样, "零配置可用"即默认值。 */
public sealed record TuiSettingsSnapshot
{
    /** 用户拖出来的侧栏(单窗格右栏)宽度; null 表示没拖过, 用布局默认比例。 */
    public int? SidebarWidth { get; init; }

    public static TuiSettingsSnapshot From(IReadOnlyDictionary<string, object?> parameters)
    {
        var width = parameters.GetValueOrDefault("sidebarWidth") switch
        {
            double value => (int)value,
            long value => (int)value,
            int value => value,
            _ => (int?)null,
        };
        return new TuiSettingsSnapshot
        {
            SidebarWidth = width is { } parsed ? Math.Clamp(parsed, 0, TuiSettings.MaximumSidebarWidth) : null,
        };
    }

    public Dictionary<string, object?> ToParameters() => new(StringComparer.Ordinal)
    {
        ["sidebarWidth"] = SidebarWidth,
    };
}
