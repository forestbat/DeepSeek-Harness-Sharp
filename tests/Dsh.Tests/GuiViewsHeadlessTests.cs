using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using Avalonia.VisualTree;
using AvaloniaEdit;
using Dsh.Boot;
using Dsh.Fleet;
using Dsh.Gui.ViewModels;
using Dsh.Gui.Views;
using Dsh.RemoteHost;

namespace Dsh.Tests;

/** 三个独立视图在真实渲染的 headless 窗口里接真实 host: 编辑器与视图模型的文本同步、diff 卡片渲染、机器列表。 */
public sealed class GuiViewsHeadlessTests
{
    private const double WindowWidth = 1280;
    private const double WindowHeight = 800;

    [Fact]
    public async Task FilesView_ShowsTheFileWithLineNumbersAndHighlighting_AndSavesWhatWasTyped() => await HeadlessGui.RunAsync(async () =>
    {
        await using var host = await HarnessHostFixture.CreateAsync("gvf", TestContext.Current.CancellationToken);
        var root = Directory.CreateDirectory(Path.Combine(host.Environment.App.Home.Root, "project")).FullName;
        var file = Path.Combine(root, "Program.cs");
        await File.WriteAllTextAsync(file, "class Program\n{\n}\n", TestContext.Current.CancellationToken);
        var client = await host.ConnectAsync("files-view", TestContext.Current.CancellationToken);
        var viewModel = new FilesViewModel(client.Files, root, RemotePaths.StyleFor((await client.InfoAsync(TestContext.Current.CancellationToken)).Platform));
        Assert.True(await viewModel.LoadAsync(), viewModel.Status);
        var window = Show(new FilesView { DataContext = viewModel });

        viewModel.Selected = viewModel.Roots[0].Children.Single();
        await viewModel.OpenCommand.ExecuteAsync(null);
        Dispatcher.UIThread.RunJobs();
        var editor = window.GetVisualDescendants().OfType<TextEditor>().Single();
        var shown = (editor.Text, editor.ShowLineNumbers, editor.SyntaxHighlighting?.Name, editor.IsEffectivelyVisible);
        editor.Document.Insert(0, "// edited\n");
        var dirty = (viewModel.Editor!.IsDirty, viewModel.Editor.Text);
        await viewModel.Editor.SaveCommand.ExecuteAsync(null);

        Assert.Equal(("class Program\n{\n}\n", true, "C#", true), shown);
        Assert.Equal((true, "// edited\nclass Program\n{\n}\n"), dirty);
        Assert.Equal("// edited\nclass Program\n{\n}\n", await File.ReadAllTextAsync(file, TestContext.Current.CancellationToken));
        window.Close();
    });

    [Fact]
    public async Task GitView_RendersOneDiffCardPerFileOfTheSelectedCommit() => await HeadlessGui.RunAsync(async () =>
    {
        using var repo = new GitTestRepository();
        repo.Commit(("a.txt", "one\n"), ("b.txt", "two\n"));
        await using var host = await HarnessHostFixture.CreateAsync("gvg", TestContext.Current.CancellationToken);
        var client = await host.ConnectAsync("git-view", TestContext.Current.CancellationToken);
        await HarnessHostFixture.OpenProjectAsync(client, repo.Sandbox, TestContext.Current.CancellationToken);
        var viewModel = new GitViewModel(client.Git, client.Files, repo.Root, RemotePaths.StyleFor((await client.InfoAsync(TestContext.Current.CancellationToken)).Platform));
        Assert.True(await viewModel.RefreshAsync(), viewModel.Status);
        var window = Show(new GitView { DataContext = viewModel });

        viewModel.SelectedCommit = viewModel.Commits.Single();
        await FleetConnectionTests.WaitForAsync(() => viewModel.DiffCards.Count == 2);
        Dispatcher.UIThread.RunJobs();
        window.CaptureRenderedFrame()?.Dispose();

        Assert.Equal(["a.txt", "b.txt"], window.GetVisualDescendants().OfType<DiffView>().Select(view => view.Card!.Title));
        window.Close();
    });

    [Fact]
    public async Task MachinesView_ListsTheImplicitLocalMachineAndTheSavedOnes() => await HeadlessGui.RunAsync(async () =>
    {
        var home = TempTree.CreateDirectory("machines-view");
        try
        {
            var store = new MachineStore(new HarnessHome(home));
            store.Upsert(MachineSpec.Ssh("build", "build.internal", MachineSpec.DefaultSshPort, "dev", SshAuth.Agent));
            store.Upsert(MachineSpec.Ssh("iroh-build", "bootstrap.internal", MachineSpec.DefaultSshPort, "dev", SshAuth.Agent) with
            {
                Kind = MachineKinds.Iroh,
                EndpointId = new string('a', MachineSpec.IrohEndpointIdLength),
            });
            var viewModel = new MachinesViewModel(store, new FleetOptions());
            viewModel.Load("build");
            var window = Show(new MachinesView { DataContext = viewModel });
            await Task.Yield();

            var rows = window.GetVisualDescendants().OfType<ListBoxItem>().Select(item => ((MachineRowViewModel)item.DataContext!).Name).ToList();
            var hostBox = window.GetVisualDescendants().OfType<TextBox>().First(box => box.Text == "build.internal");

            Assert.Equal(["local", "build", "iroh-build"], rows);
            Assert.True(hostBox.IsEffectivelyVisible);

            viewModel.Selected = viewModel.Machines.Single(machine => machine.Name == "iroh-build");
            Dispatcher.UIThread.RunJobs();
            var endpointBox = window.GetVisualDescendants().OfType<TextBox>().Single(box => box.Text == new string('a', 64));
            Assert.False(hostBox.IsEffectivelyVisible);
            Assert.True(endpointBox.IsEffectivelyVisible);
            Assert.True(window.GetVisualDescendants().OfType<TextBox>().Single(box => box.Text == "dev").IsEffectivelyVisible);
            Assert.True(window.GetVisualDescendants().OfType<ComboBox>().Single(box => Equals(box.SelectedItem, SshAuth.Agent)).IsEffectivelyVisible);
            endpointBox.Text = new string('b', 64);
            await viewModel.SaveCommand.ExecuteAsync(null);
            Assert.False(viewModel.StatusIsError, viewModel.Status);
            var saved = Assert.Single(store.Load().Machines, machine => machine.Name == "iroh-build");
            Assert.Equal((MachineKinds.Iroh, "bootstrap.internal", new string('b', MachineSpec.IrohEndpointIdLength)), (saved.Kind, saved.Host, saved.EndpointId));
            window.Close();
        }
        finally
        {
            TempTree.Delete(home);
        }
    });

    private static Window Show(Control view)
    {
        var window = new Window { Width = WindowWidth, Height = WindowHeight, Content = view };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return window;
    }
}
