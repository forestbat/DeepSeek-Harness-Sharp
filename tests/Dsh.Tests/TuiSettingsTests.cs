using Dsh.Boot;
using Dsh.Pty;
using Dsh.Tui.Services;

namespace Dsh.Tests;

/** TUI 插件参数段(settings.yaml -> plugins."@deepseek-ai/dsh-tui"): 侧栏宽度读写、夹取与"不覆盖别人"的写回。 */
public sealed class TuiSettingsTests : IDisposable
{
    private readonly string _homeDir = Path.Combine(
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../artifacts020/test-homes")),
        Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_homeDir))
            Directory.Delete(_homeDir, true);
    }

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
}
