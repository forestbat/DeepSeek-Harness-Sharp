using Avalonia.Media;
using Dsh.Boot;
using Dsh.Fleet;
using Dsh.Gui.Services;
using Dsh.Gui.ViewModels;

namespace Dsh.Tests;

[Collection(GuiSerialCollection.CollectionName)]
public sealed class Ma2aNavigationTests
{
    [Fact]
    public async Task RegisteredMa2aPage_OpensThroughWorkspaceNavigation_AndCoexistsWithFleetPages()
    {
        var root = TempTree.CreateSocketDirectory("ma2an");
        try
        {
            await HeadlessGui.RunAsync(async () =>
            {
                var home = new HarnessHome(root);
                var options = new FleetOptions { LocalHome = home, ClientName = "ma2a-navigation-test", AutoReconnect = false };
                var fleet = new FleetWorkspace(new FleetWorkspaceServices(new MachineStore(home), options, () => new FleetClient([], options)));
                var workspace = new WorkspaceViewModel(fleet, (_, _, _) => { }, home: home);
                var destinations = new List<WorkspaceDestination>();
                workspace.NavigationRequested += destinations.Add;
                var icon = Geometry.Parse("M0,0 L1,1");
                try
                {
                    workspace.RegisterHostPages(icon, icon, icon);
                    workspace.RegisterHostPages(icon, icon, icon);
                    var originalClient = fleet.Client;
                    var expected = new[] { WorkspacePages.Files, WorkspacePages.Git, WorkspacePages.Machines, WorkspacePages.Ma2a };
                    Assert.Equal(expected, workspace.Pages.Available.Select(page => page.Id));

                    var native = workspace.Pages.Available.Single(page => page.Id == WorkspacePages.Ma2a);
                    workspace.OpenPageCommand.Execute(native);
                    var console = Assert.IsType<Ma2aViewModel>(workspace.PageContent);
                    Assert.Same(native, workspace.ActivePage);
                    Assert.Equal(WorkspaceDestination.Page, destinations[^1]);
                    Assert.False(console.Connected);
                    Assert.Same(originalClient, fleet.Client);

                    workspace.OpenPageCommand.Execute(workspace.Pages.Available.Single(page => page.Id == WorkspacePages.Machines));
                    Assert.Same(workspace.Machines, workspace.PageContent);
                    workspace.OpenPageCommand.Execute(workspace.Pages.Available.Single(page => page.Id == WorkspacePages.Files));
                    Assert.IsType<HostPageViewModel>(workspace.PageContent);
                    workspace.OpenPageCommand.Execute(workspace.Pages.Available.Single(page => page.Id == WorkspacePages.Git));
                    Assert.IsType<HostPageViewModel>(workspace.PageContent);
                    workspace.OpenPageCommand.Execute(native);
                    Assert.NotSame(console, Assert.IsType<Ma2aViewModel>(workspace.PageContent));
                    Assert.Equal(expected, workspace.Pages.Available.Select(page => page.Id));
                    Assert.Same(originalClient, fleet.Client);
                }
                finally { await workspace.DisposeAsync(); }
            });
        }
        finally { TempTree.Delete(root); }
    }
}
