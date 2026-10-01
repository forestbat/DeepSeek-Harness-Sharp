using Dsh.Boot;
using Dsh.Tui.Services;
using Xunit;

namespace Dsh.Tests;

/** TUI 插件参数段(settings.yaml -> plugins."@deepseek-ai/dsh-tui"): 侧栏宽度读写、夹取与"不覆盖别人"的写回。 */
public sealed class TuiSettingsTests : IDisposable
{
    private readonly string _homeDir = Path.Combine(
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../artifacts/test-homes")),
        Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_homeDir))
            Directory.Delete(_homeDir, true);
    }

    [Fact]
    public void Load_Without_Section_Keeps_Default_And_Clamps_Absurd_Values()
    {
        var home = HarnessHome.Resolve(_homeDir);

        Assert.Null(new TuiSettings(home).Load().SidebarWidth);

        var settings = HarnessSettings.Load(home);
        settings.Plugins[TuiSettings.Package] = new PluginSetting
        {
            Enabled = true,
            Parameters = new Dictionary<string, object?>(StringComparer.Ordinal) { ["sidebarWidth"] = 9999L },
        };
        settings.SavePlugins(home);

        Assert.Equal(TuiSettings.MaximumSidebarWidth, new TuiSettings(home).Load().SidebarWidth);
    }

    [Fact]
    public void Save_Round_Trips_And_Keeps_Other_Plugins()
    {
        var home = HarnessHome.Resolve(_homeDir);
        var settings = HarnessSettings.Load(home);
        settings.Plugins["@deepseek-ai/dsh-gui"] = new PluginSetting
        {
            Enabled = false,
            Parameters = new Dictionary<string, object?>(StringComparer.Ordinal) { ["theme"] = "light" },
        };
        settings.SavePlugins(home);

        var tui = new TuiSettings(home);
        tui.Save(tui.Load() with { SidebarWidth = 30 });

        Assert.Equal(30, tui.Load().SidebarWidth);
        var reloaded = HarnessSettings.Load(home);
        Assert.Equal("light", reloaded.Plugins["@deepseek-ai/dsh-gui"].Parameters["theme"]);
        Assert.False(reloaded.Plugins["@deepseek-ai/dsh-gui"].Enabled);
    }
}
