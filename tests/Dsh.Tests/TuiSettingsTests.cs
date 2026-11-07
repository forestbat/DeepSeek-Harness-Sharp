using Dsh.Boot;
using Dsh.Plugins;
using Dsh.Pty;
using Dsh.Tui;
using Dsh.Tui.Services;

namespace Dsh.Tests;

/** TUI 插件参数段(settings.yaml -> plugins."@deepseek-ai/dsh-tui"): 侧栏宽度读写、夹取与"不覆盖别人"的写回。 */
public sealed class TuiSettingsTests : IDisposable
{
    private readonly string _homeDir = Path.Combine(
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../artifacts020/test-homes")),
        Guid.NewGuid().ToString("N"));

    public void Dispose() => TempTree.Delete(_homeDir);

    [Fact]
    public void Read_Without_Section_Keeps_Default_And_Clamps_Absurd_Values()
    {
        var home = HarnessHome.Resolve(_homeDir);
        var settings = new TuiSettings(home);

        Assert.Null(settings.SidebarWidth);

        var raw = HarnessSettings.Load(home);
        raw.Plugins[TuiSettings.Package] = new PluginSetting
        {
            Enabled = true,
            Parameters = new Dictionary<string, object?>(StringComparer.Ordinal) { ["sidebarWidth"] = 9999L },
        };
        raw.SavePlugins(home);

        Assert.Equal(TuiSettings.MaximumSidebarWidth, settings.SidebarWidth);
    }

    [Fact]
    public void Write_Round_Trips_And_Keeps_Other_Plugins()
    {
        var home = HarnessHome.Resolve(_homeDir);
        var raw = HarnessSettings.Load(home);
        raw.Plugins["@deepseek-ai/dsh-gui"] = new PluginSetting
        {
            Enabled = false,
            Parameters = new Dictionary<string, object?>(StringComparer.Ordinal) { ["theme"] = "light" },
        };
        raw.SavePlugins(home);

        var settings = new TuiSettings(home);
        settings.SidebarWidth = 30;
        settings.InputHeight = 6;

        Assert.Equal(30, settings.SidebarWidth);
        Assert.Equal(6, settings.InputHeight);
        var reloaded = HarnessSettings.Load(home);
        Assert.Equal("light", reloaded.Plugins["@deepseek-ai/dsh-gui"].Parameters["theme"]);
        Assert.False(reloaded.Plugins["@deepseek-ai/dsh-gui"].Enabled);
    }

    /** 拖动侧栏会整段回写插件参数: keys 映射(含 `,`/`-`/`+` 这类标点键)必须原样保留。 */
    [Fact]
    public void Write_Keeps_Key_Bindings_Intact()
    {
        Directory.CreateDirectory(_homeDir);
        File.WriteAllText(
            Path.Combine(_homeDir, "settings.yaml"),
            $"plugins:\n  \"{TuiSettings.Package}\":\n    keys:\n      prevSession: \"Ctrl+X ,\"\n      closePane: \"Ctrl+X -\"\n      splitPane: \"Ctrl+X +\"\n");
        var home = HarnessHome.Resolve(_homeDir);

        new TuiSettings(home).SidebarWidth = 30;

        var reloaded = new TuiSettings(home);
        var (table, warnings) = KeyBindingTable.Load(reloaded.Keys);
        Assert.Equal(30, reloaded.SidebarWidth);
        Assert.Empty(warnings);
        Assert.Equal("Ctrl+X ,", KeyBindingTable.Format(table.Sequence(TuiAction.PrevSession)));
        Assert.Equal("Ctrl+X -", KeyBindingTable.Format(table.Sequence(TuiAction.ClosePane)));
        Assert.Equal("Ctrl+X +", KeyBindingTable.Format(table.Sequence(TuiAction.SplitPane)));
    }

    [Fact]
    public void Highlight_Parses_Rgb_And_Palette()
    {
        var home = new HarnessHome(_homeDir);
        Directory.CreateDirectory(_homeDir);
        var raw = HarnessSettings.Load(home);
        raw.Plugins[TuiSettings.Package] = new PluginSetting
        {
            Enabled = true,
            Parameters = new Dictionary<string, object?>(StringComparer.Ordinal) { ["highlightColor"] = "#112233" },
        };
        raw.SavePlugins(home);

        Assert.Equal(CellColor.FromRgb(0x11, 0x22, 0x33), new TuiSettings(home).Highlight);

        var palette = HarnessSettings.Load(home);
        palette.Plugins[TuiSettings.Package].Parameters["highlightColor"] = "BrightRed";
        palette.SavePlugins(home);
        Assert.Equal(CellColor.FromPalette(AnsiColor.BrightRed), new TuiSettings(home).Highlight);
    }

    [Fact]
    public void Theme_Colors_Parse_And_Arrow_Follows_Highlight()
    {
        var home = new HarnessHome(_homeDir);
        Directory.CreateDirectory(_homeDir);
        var raw = HarnessSettings.Load(home);
        raw.Plugins[TuiSettings.Package] = new PluginSetting
        {
            Enabled = true,
            Parameters = new Dictionary<string, object?>(StringComparer.Ordinal) { ["highlightColor"] = "#112233" },
        };
        raw.SavePlugins(home);

        // 只配了高亮时: 箭头跟随高亮; 分隔线/状态栏默认不着色; 错误默认 Red。
        var onlyHighlight = new TuiSettings(home);
        Assert.Equal(CellColor.FromRgb(0x11, 0x22, 0x33), onlyHighlight.Arrow);
        Assert.Equal(CellColor.Default, onlyHighlight.Divider);
        Assert.Equal(CellColor.Default, onlyHighlight.Status);
        Assert.Equal(CellColor.FromPalette(AnsiColor.Red), onlyHighlight.Error);

        var explicitColors = HarnessSettings.Load(home);
        var parameters = explicitColors.Plugins[TuiSettings.Package].Parameters;
        parameters["arrowColor"] = "#0a0b0c";
        parameters["dividerColor"] = "BrightBlack";
        parameters["statusColor"] = "#010203";
        parameters["errorColor"] = "Default";
        explicitColors.SavePlugins(home);

        var themed = new TuiSettings(home);
        Assert.Equal(CellColor.FromRgb(0x0a, 0x0b, 0x0c), themed.Arrow);
        Assert.Equal(CellColor.FromPalette(AnsiColor.BrightBlack), themed.Divider);
        Assert.Equal(CellColor.FromRgb(0x01, 0x02, 0x03), themed.Status);
        Assert.Equal(CellColor.Default, themed.Error);
    }
}
