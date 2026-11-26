using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Dsh.Gui.Services;
using Dsh.Gui.ViewModels;
using Dsh.Gui.Views;

namespace Dsh.Tests;

[Collection(GuiSerialCollection.CollectionName)]
public sealed class Ma2aNavigationTests
{
    [Fact]
    public async Task MachinesPage_KeepsOneNativeConsoleAcrossTabChangesAndReopening()
        => await HeadlessGui.RunAsync(async () =>
        {
            await using var environment = await GuiTestEnvironment.CreateAsync();
            var preferences = new GuiSettings(environment.App.Home);
            preferences.Save(preferences.Load() with { CloseAction = GuiSettings.CloseQuit });
            var window = new MainWindow(environment.App, await environment.OpenChatAsync());
            var viewModel = window.ViewModel!;
            var workspace = viewModel.Workspace;
            var originalClient = workspace.Fleet.Client;
            try
            {
                window.Show();
                Render(window);
                var navigation = window.GetVisualDescendants().OfType<SidebarView>().Single()
                    .GetVisualDescendants().OfType<Button>()
                    .Where(button => button.CommandParameter is WorkspacePageEntry).ToArray();
                Assert.Equal([WorkspacePages.Files, WorkspacePages.Git, WorkspacePages.Machines],
                    navigation.Select(button => Assert.IsType<WorkspacePageEntry>(button.CommandParameter).Id));

                var machinesEntry = navigation.Single(button => button.CommandParameter is WorkspacePageEntry { Id: WorkspacePages.Machines });
                var openMachines = machinesEntry.Command;
                Assert.NotNull(openMachines);
                openMachines.Execute(machinesEntry.CommandParameter);
                Render(window);
                Assert.Same(machinesEntry.CommandParameter, workspace.ActivePage);
                var tabs = MachinesTabs(window);
                Assert.Equal(0, tabs.SelectedIndex);
                Assert.Same(workspace.Machines, VisibleView<MachinesView>(tabs).DataContext);

                tabs.SelectedIndex = 1;
                Render(window);
                var nativeView = VisibleView<Ma2aView>(tabs);
                var console = Assert.IsType<Ma2aViewModel>(nativeView.DataContext);
                var connect = nativeView.GetVisualDescendants().OfType<Button>().Single(button => Equals(button.Content, "连接"));
                Assert.Same(console.ConnectCommand, connect.Command);
                var directory = Path.Combine(environment.App.Home.Root, "native-runtime");
                StateDirectoryBox(nativeView).SetCurrentValue(TextBox.TextProperty, directory);
                Assert.Equal(directory, console.StateDirectory);

                tabs.SelectedIndex = 0;
                Render(window);
                Assert.False(nativeView.IsAttachedToVisualTree() && nativeView.IsEffectivelyVisible);
                Assert.Same(workspace.Machines, VisibleView<MachinesView>(tabs).DataContext);
                tabs.SelectedIndex = 1;
                Render(window);
                Assert.Same(console, VisibleView<Ma2aView>(tabs).DataContext);
                Assert.Same(originalClient, workspace.Fleet.Client);

                var shownConsole = VisibleView<Ma2aView>(tabs);
                viewModel.ShowChatCommand.Execute(null);
                Render(window);
                Assert.False(shownConsole.IsAttachedToVisualTree() && shownConsole.IsEffectivelyVisible);
                openMachines.Execute(machinesEntry.CommandParameter);
                Render(window);
                Assert.Same(console, VisibleView<Ma2aView>(MachinesTabs(window)).DataContext);

                viewModel.ShowDashboardCommand.Execute(null);
                Render(window);
                Assert.False(shownConsole.IsAttachedToVisualTree());
                openMachines.Execute(machinesEntry.CommandParameter);
                Render(window);
                var reopened = MachinesTabs(window);
                Assert.Equal(1, reopened.SelectedIndex);
                var consoleView = VisibleView<Ma2aView>(reopened);
                Assert.Same(console, consoleView.DataContext);
                Assert.Equal(directory, StateDirectoryBox(consoleView).Text);
                Assert.Same(originalClient, workspace.Fleet.Client);
            }
            finally
            {
                window.Close();
                await viewModel.DisposeAsync();
                Dispatcher.UIThread.RunJobs();
            }

            Assert.Null(workspace.PageContent);
            Assert.Empty(window.GetVisualDescendants().OfType<Ma2aView>().Where(view => view.IsAttachedToVisualTree()));
        });

    private static TabControl MachinesTabs(MainWindow window) => window.GetVisualDescendants()
        .OfType<TabControl>().Single(tabs => tabs.DataContext is MachinesPageViewModel);

    private static T VisibleView<T>(Control parent) where T : Control => parent.GetVisualDescendants()
        .OfType<T>().Single(view => view.IsEffectivelyVisible);

    private static TextBox StateDirectoryBox(Ma2aView view) => view.GetVisualDescendants().OfType<TextBox>()
        .Single(box => AutomationProperties.GetName(box) == "MA2A 状态目录");

    private static void Render(MainWindow window)
    {
        Dispatcher.UIThread.RunJobs();
        window.CaptureRenderedFrame()?.Dispose();
    }
}
