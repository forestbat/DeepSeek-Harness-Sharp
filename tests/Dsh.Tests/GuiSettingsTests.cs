using Dsh.Boot;
using Dsh.Gui.Services;

namespace Dsh.Tests;

public sealed class GuiSettingsTests : IDisposable
{
    private readonly string _home = Path.Combine(Path.GetTempPath(), "dsh-gui-settings", Guid.NewGuid().ToString("N"));

    public GuiSettingsTests() => Directory.CreateDirectory(_home);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_home, true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public void MissingSection_Falls_Back_To_Defaults()
    {
        var snapshot = new GuiSettings(new HarnessHome(_home)).Load();

        Assert.Equal(GuiSettings.ThemeDark, snapshot.Theme);
        Assert.Equal(13.5, snapshot.FontSize);
        Assert.Equal(GuiSettings.ViewSolution, snapshot.WorkspaceView);
        Assert.True(snapshot.GpuEnabled);
        Assert.Equal(GuiSettings.CloseTray, snapshot.CloseAction);
        Assert.True(snapshot.RememberBounds);
    }

    [Fact]
    public void Save_Then_Load_Roundtrips_Nested_Parameters()
    {
        var settings = new GuiSettings(new HarnessHome(_home));
        settings.Save(settings.Load() with
        {
            Theme = GuiSettings.ThemeLight,
            FontSize = 15,
            WorkspaceView = GuiSettings.ViewFilesystem,
            CloseAction = GuiSettings.CloseQuit,
            GpuEnabled = false,
            GpuBackend = "software",
            GpuAdapter = "NVIDIA GeForce RTX 4060 Laptop GPU",
            WindowWidth = 1180,
            WindowHeight = 760,
            WindowX = -12,
            WindowMaximized = true,
            SidebarVisible = false,
        });

        var reloaded = settings.Load();

        Assert.Equal(GuiSettings.ThemeLight, reloaded.Theme);
        Assert.Equal(15, reloaded.FontSize);
        Assert.Equal(GuiSettings.ViewFilesystem, reloaded.WorkspaceView);
        Assert.Equal(GuiSettings.CloseQuit, reloaded.CloseAction);
        Assert.False(reloaded.GpuEnabled);
        Assert.Equal("software", reloaded.GpuBackend);
        Assert.Equal(1180, reloaded.WindowWidth);
        Assert.Equal(760, reloaded.WindowHeight);
        Assert.Equal(-12, reloaded.WindowX);
        Assert.True(reloaded.WindowMaximized);
        Assert.False(reloaded.SidebarVisible);
    }

    [Fact]
    public void Save_Keeps_Other_Plugins_And_File_Comments()
    {
        var path = Path.Combine(_home, "settings.yaml");
        File.WriteAllText(path, """
            # 顶部注释
            global_default_model: custom/model

            plugins:
              "@scope/other":
                enabled: false
                mode: quiet
            """);
        var settings = new GuiSettings(new HarnessHome(_home));

        settings.Save(settings.Load() with { Theme = GuiSettings.ThemeLight });

        var text = File.ReadAllText(path);
        Assert.Contains("# 顶部注释", text);
        Assert.Contains("global_default_model: custom/model", text);
        Assert.Contains("\"@scope/other\":", text);
        Assert.Contains("mode: quiet", text);
        Assert.Contains("\"@deepseek-ai/dsh-gui\":", text);
        Assert.Contains("theme: light", text);

        var reloaded = HarnessSettings.Load(new HarnessHome(_home));
        Assert.False(reloaded.Plugins["@scope/other"].Enabled);
        Assert.Equal("light", reloaded.Plugins[GuiSettings.Package].Parameters["theme"]);
        Assert.True(reloaded.Plugins[GuiSettings.Package].Enabled);
    }

    [Fact]
    public void Out_Of_Range_FontSize_Is_Clamped()
    {
        var settings = new GuiSettings(new HarnessHome(_home));

        settings.Save(settings.Load() with { FontSize = 40 });

        Assert.Equal(GuiSettings.MaxFontSize, settings.Load().FontSize);
    }

    [Fact]
    public void DefaultWorkspace_RoundTrips_And_Clears()
    {
        var settings = new GuiSettings(new HarnessHome(_home));
        Assert.Null(settings.Load().DefaultWorkspace);

        settings.Save(settings.Load() with { DefaultWorkspace = _home });
        Assert.Equal(_home, settings.Load().DefaultWorkspace);

        settings.Save(settings.Load() with { DefaultWorkspace = null });
        Assert.Null(settings.Load().DefaultWorkspace);
    }
}
