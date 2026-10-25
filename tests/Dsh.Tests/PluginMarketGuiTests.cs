using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Input.Raw;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Dsh.Boot;
using Dsh.Gui.ViewModels;
using Dsh.Gui.Views;
using Dsh.Interaction;

namespace Dsh.Tests;

/** Plugin market page rendered headlessly: library picker button, permission toggles shared with /plugins, and the remove button. */
[Collection(GuiSerialCollection.CollectionName)]
public sealed class PluginMarketGuiTests
{
    private const string Todo = "@deepseek-ai/dsh-tool-todo";
    private const string BrowseTip = "浏览并选择插件文件";

    private static readonly string ScreenshotDirectory = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "artifacts020", "gui-screenshots"));

    [Fact]
    public async Task Market_PermissionToggles_And_TuiCommand_ShareSettings() => await HeadlessGui.RunAsync(async () =>
    {
        var environment = await GuiTestEnvironment.CreateAsync();
        await using var environmentScope = environment;
        var window = await OpenMarketAsync(environment);
        try
        {
            var preferences = window.ViewModel!.Preferences;
            Assert.True(Row(preferences, Todo).CanWrite);

            await RunCommandAsync(environment, $"/plugins ban_write {Todo}");
            await preferences.RefreshPluginsCommand.ExecuteAsync(null);
            await GuiTestEnvironment.WaitForAsync(preferences.Plugins,
                () => preferences.Plugins.Any(row => row.Package == Todo && !row.CanWrite));
            Dispatcher.UIThread.RunJobs();
            Assert.False(Row(preferences, Todo).CanWrite);

            var network = Toggle(window, Todo, "permissions.network");
            Assert.True(network.IsChecked);
            PressSpace(window, network);
            Assert.False(Row(preferences, Todo).CanNetwork);
            try
            {
                await GuiTestEnvironment.WaitForAsync(preferences,
                    () => HarnessSettings.Load(environment.App.Home).Plugins[Todo].Permissions?.Network == false);
            }
            catch (TimeoutException error)
            {
                var status = preferences.Status.Replace(environment.App.Home.Root, "<test-home>", StringComparison.OrdinalIgnoreCase);
                throw new TimeoutException($"Network permission did not persist; status: {status}", error);
            }
            Dispatcher.UIThread.RunJobs();
            var permissions = HarnessSettings.Load(environment.App.Home).Plugins[Todo].Permissions;
            Assert.Equal(new PluginPermissions { Write = false, Network = false }, permissions);
            Capture(window, "headless-market-permissions");

            var write = Toggle(window, Todo, "permissions.write");
            Assert.False(write.IsChecked);
            PressSpace(window, write);
            Assert.True(Row(preferences, Todo).CanWrite);
            await GuiTestEnvironment.WaitForAsync(preferences,
                () => HarnessSettings.Load(environment.App.Home).Plugins[Todo].Permissions?.Write != false);
            Dispatcher.UIThread.RunJobs();
            await RunCommandAsync(environment, $"/plugins enable_write {Todo}");
            Assert.Equal(new PluginPermissions { Network = false }, HarnessSettings.Load(environment.App.Home).Plugins[Todo].Permissions);
        }
        finally
        {
            window.Close();
            Dispatcher.UIThread.RunJobs();
        }
    });

    [Fact]
    public async Task Market_RemoveButton_UnloadsPlugin_AndBrowseFillsPath() => await HeadlessGui.RunAsync(async () =>
    {
        var environment = await GuiTestEnvironment.CreateAsync();
        await using var environmentScope = environment;
        var window = await OpenMarketAsync(environment);
        try
        {
            var preferences = window.ViewModel!.Preferences;
            var library = Path.Combine(AppContext.BaseDirectory, "Dsh.ManagedPluginSample.dll");
            // Test-only stand-in for the OS file dialog: the headless platform has no storage provider.
            preferences.PluginFilePicker = () => Task.FromResult<string?>(library);
            ClickAndWait(window.GetVisualDescendants().OfType<Button>().Single(button => Equals(ToolTip.GetTip(button), BrowseTip)));
            await preferences.BrowsePluginPathCommand.ExecutionTask!;
            Assert.Equal(library, preferences.PluginInput);

            var remove = RowButtons(window, Todo).Single(button => Equals(button.Content, "移除"));
            ClickAndWait(remove);
            await preferences.PluginActionCommand.ExecutionTask!;
            await GuiTestEnvironment.WaitForAsync(preferences.Plugins,
                () => preferences.Plugins.Any(row => row.Package == Todo && !row.Enabled));

            Assert.Null(environment.App.Composition!.Find(Todo));
            Assert.False(HarnessSettings.Load(environment.App.Home).Plugins[Todo].Enabled);
            Assert.Contains("removed", preferences.Status, StringComparison.Ordinal);
            Assert.False(Row(preferences, Todo).Enabled);
        }
        finally
        {
            window.Close();
            Dispatcher.UIThread.RunJobs();
        }
    });

    private static async Task<MainWindow> OpenMarketAsync(GuiTestEnvironment environment)
    {
        var window = new MainWindow(environment.App, await environment.OpenChatAsync());
        window.Show();
        window.ViewModel!.ShowMarketCommand.Execute(null);
        await GuiTestEnvironment.WaitForAsync(window.ViewModel.Preferences.Plugins,
            () => window.ViewModel.Preferences.Plugins.Any(row => row.Package == Todo));
        Dispatcher.UIThread.RunJobs();
        return window;
    }

    private static PluginRowViewModel Row(SettingsViewModel preferences, string package)
        => preferences.Plugins.Single(row => row.Package == package);

    private static IReadOnlyList<Control> RowControls(Window window, string package)
        => [.. window.GetVisualDescendants().OfType<MarketPageView>().Single().GetVisualDescendants()
            .OfType<Control>()
            .Where(control => control.DataContext is PluginRowViewModel row && row.Package == package)];

    private static IReadOnlyList<Button> RowButtons(Window window, string package)
        => [.. RowControls(window, package).OfType<Button>()];

    private static CheckBox Toggle(MainWindow window, string package, string tip)
    {
        // A collection refresh completes before ItemsControl's layout/materialization pass.
        window.CaptureRenderedFrame()?.Dispose();
        var toggle = RowControls(window, package).OfType<CheckBox>().Single(box => Equals(ToolTip.GetTip(box), tip));
        Assert.Same(Row(window.ViewModel!.Preferences, package), toggle.DataContext);
        return toggle;
    }

    /** Use CheckBox.OnClick/Toggle, which preserves its TwoWay binding, rather than a local property assignment. */
    private static void PressSpace(Window window, CheckBox toggle)
    {
        Assert.True(toggle.IsEffectivelyEnabled);
        Assert.True(toggle.Focus());
        window.KeyPress(Key.Space, RawInputModifiers.None, PhysicalKey.Space, null);
        window.KeyRelease(Key.Space, RawInputModifiers.None, PhysicalKey.Space, null);
    }

    /** Runs the button's bound command with its bound parameter, as a click does. */
    private static void ClickAndWait(Button button)
    {
        Assert.True(button.IsEffectivelyEnabled);
        button.Command!.Execute(button.CommandParameter);
        Dispatcher.UIThread.RunJobs();
    }

    private static async Task RunCommandAsync(GuiTestEnvironment environment, string line)
    {
        var commands = environment.App.Ctx.Get<CommandsService>(CommandsService.ServiceName)!;
        var execution = await commands.Execute(environment.Agent, line, TestContext.Current.CancellationToken);
        Assert.IsType<CommandResult.Success>(execution!.Result);
    }

    private static void Capture(Window window, string name)
    {
        Directory.CreateDirectory(ScreenshotDirectory);
        using var frame = window.CaptureRenderedFrame();
        frame?.Save(Path.Combine(ScreenshotDirectory, $"{name}.png"), PngBitmapEncoderOptions.Default);
    }
}
