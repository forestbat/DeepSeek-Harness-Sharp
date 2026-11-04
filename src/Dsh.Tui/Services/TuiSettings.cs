using System.Globalization;
using Dsh.Boot;
using Dsh.Plugins;

namespace Dsh.Tui.Services;

/**
 * TUI 插件自己的参数段: settings.yaml -> plugins."@deepseek-ai/dsh-tui"。
 * 与 GuiSettings 同构: 缺参数一律退回默认值(不因手改配置把界面卡住), 写回走 HarnessSettings.SavePlugins 并保留文件其余内容。
 */
public sealed class TuiSettings(HarnessHome home)
{
    public const string Package = "@deepseek-ai/dsh-tui";

    /** 侧栏宽度的绝对上限(字符列): 再宽也没意义, 用来兜住手改配置里的离谱值; 实际渲染还会按控制台宽度再夹一次。 */
    public const int MaximumSidebarWidth = 120;

    /** 输入栏高度的绝对上限(行): 同理兜住离谱值; 实际渲染还会按控制台高度再夹一次。 */
    public const int MaximumInputHeight = 40;

    /** 前缀等待超时的上限(毫秒): 兜住手改配置里的离谱值。 */
    public const int MaximumKeySequenceTimeoutMs = 60_000;

    /** 侧栏(单窗格右栏)宽度; null 表示没拖过, 用布局默认比例。 */
    public int? SidebarWidth
    {
        get => Read("sidebarWidth", MaximumSidebarWidth);
        set => Write("sidebarWidth", value, MaximumSidebarWidth);
    }

    /** 输入栏高度(行, 含信息行); null 表示没拖过, 用默认 2 行。 */
    public int? InputHeight
    {
        get => Read("inputHeight", MaximumInputHeight);
        set => Write("inputHeight", value, MaximumInputHeight);
    }

    /** 高亮色(聚焦窗格标题/补全选中): "#rrggbb" 真彩或 16 色板名(如 BrightCyan); 缺省 BrightCyan。 */
    public CellColor Highlight => ReadColor("highlightColor", CellColor.FromPalette(AnsiColor.BrightCyan));

    /** 聚焦窗格行首箭头色; 缺省跟随 highlightColor。 */
    public CellColor Arrow => ReadColor("arrowColor", Highlight);

    /** 分隔线色; 缺省 Default。 */
    public CellColor Divider => ReadColor("dividerColor", CellColor.Default);

    /** 状态栏文本色; 缺省 Default。 */
    public CellColor Status => ReadColor("statusColor", CellColor.Default);

    /** 错误文本色; 缺省 Red; 设成 Default 即不着色。 */
    public CellColor Error => ReadColor("errorColor", CellColor.FromPalette(AnsiColor.Red));

    /** GPU 后端: auto|vk|gl|cpu; 缺省 auto(auto 优先 Vk, 不可用回退 GL, 再回退 CPU)。 */
    public string GpuBackend => ReadString("gpuBackend", "auto");

    /** 快捷键覆盖: 动作名 → 按键串(如 overview: "Ctrl+X W"); 未列出的动作用默认键位。 */
    public IReadOnlyDictionary<string, object?>? Keys => Parameters().GetValueOrDefault("keys") as IReadOnlyDictionary<string, object?>;

    /** 多键序列的前缀等待上限(毫秒); 缺省 0 = 一直等到下一个键。 */
    public int KeySequenceTimeoutMs => Read("keySequenceTimeoutMs", MaximumKeySequenceTimeoutMs) ?? 0;

    private string ReadString(string key, string fallback)
        => Parameters().GetValueOrDefault(key) is string text && text.Length > 0 ? text : fallback;

    private CellColor ReadColor(string key, CellColor fallback)
    {
        if (Parameters().GetValueOrDefault(key) is not string text || text.Length == 0)
            return fallback;
        if (text.Length == 7 && text[0] == '#'
            && byte.TryParse(text.AsSpan(1, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var r)
            && byte.TryParse(text.AsSpan(3, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var g)
            && byte.TryParse(text.AsSpan(5, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var b))
            return CellColor.FromRgb(r, g, b);
        return Enum.TryParse<AnsiColor>(text, ignoreCase: true, out var palette)
            ? CellColor.FromPalette(palette)
            : fallback;
    }

    private int? Read(string key, int maximum)
        => Parameters().GetValueOrDefault(key) switch
        {
            double value => (int)value,
            long value => (int)value,
            int value => value,
            _ => (int?)null,
        } is { } parsed ? Math.Clamp(parsed, 0, maximum) : null;

    private void Write(string key, int? value, int maximum)
    {
        var parameters = Parameters();
        parameters[key] = value is { } parsed ? Math.Clamp(parsed, 0, maximum) : null;
        Save(parameters);
    }

    private Dictionary<string, object?> Parameters()
        => HarnessSettings.Load(home).Plugins.GetValueOrDefault(Package)?.Parameters ?? [];

    private void Save(Dictionary<string, object?> parameters)
    {
        var settings = HarnessSettings.Load(home);
        var existing = settings.Plugins.GetValueOrDefault(Package);
        settings.Plugins[Package] = (existing ?? new PluginSetting()) with { Parameters = parameters };
        settings.SavePlugins(home);
    }
}
