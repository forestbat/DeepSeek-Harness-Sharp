using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using AvaloniaEdit;
using Dsh.Boot;
using Dsh.Core;
using Dsh.Fleet;
using Dsh.Fleet.Iroh;
using Dsh.Gui.Services;
using Dsh.Gui.ViewModels;
using Dsh.Gui.Views;
using Dsh.RemoteHost;
using Dsh.Llm;

namespace Dsh.Tests;

/** 三个独立视图在真实渲染的 headless 窗口里接真实 host: 编辑器与视图模型的文本同步、diff 卡片渲染、机器列表。 */
[Collection(GuiSerialCollection.CollectionName)]
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
    public async Task GitView_RendersOneDiffCardPerFileOfTheSelectedCommit()
    {
        using var repo = new GitTestRepository();
        repo.Commit(("a.txt", "one\n"), ("b.txt", "two\n"));
        await using var host = await HarnessHostFixture.CreateAsync("gvg", TestContext.Current.CancellationToken);
        var client = await host.ConnectAsync("git-view", TestContext.Current.CancellationToken);
        await HarnessHostFixture.OpenProjectAsync(client, repo.Sandbox, TestContext.Current.CancellationToken);
        var viewModel = new GitViewModel(client.Git, client.Files, repo.Root, RemotePaths.StyleFor((await client.InfoAsync(TestContext.Current.CancellationToken)).Platform));
        Assert.True(await viewModel.RefreshAsync(), viewModel.Status);
        await HeadlessGui.RunAsync(async () =>
        {
            var window = Show(new GitView { DataContext = viewModel });
            viewModel.SelectedCommit = viewModel.Commits.Single();
            await FleetConnectionTests.WaitForAsync(() => viewModel.DiffCards.Count == 2);
            Dispatcher.UIThread.RunJobs();
            window.CaptureRenderedFrame()?.Dispose();

            Assert.Equal(["a.txt", "b.txt"], window.GetVisualDescendants().OfType<DiffView>().Select(view => view.Card!.Title));
            window.Close();
        });
    }

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
            var viewModel = new MachinesViewModel(store, new FleetOptions { SshTransportAdapter = new IrohSshTransport() });
            viewModel.Load("build");
            var window = Show(new MachinesView { DataContext = viewModel });
            await Task.Yield();

            var rows = window.GetVisualDescendants().OfType<ListBoxItem>().Select(item => ((MachineRowViewModel)item.DataContext!).Name).ToList();
            var hostBox = window.GetVisualDescendants().OfType<TextBox>().First(box => box.Text == "build.internal");

            Assert.Equal(["local", "build", "iroh-build"], rows);
            Assert.True(hostBox.IsEffectivelyVisible);
            Assert.False(window.GetVisualDescendants().OfType<SelectableTextBlock>().Single(block => block.Name == "RemoteOwner").IsVisible);

            viewModel.NewLocalCommand.Execute(null);
            Dispatcher.UIThread.RunJobs();
            var newLocal = window.GetVisualDescendants().OfType<Button>().Single(button => Equals(button.Content, "新增本地 Harness Host"));
            var newSsh = window.GetVisualDescendants().OfType<Button>().Single(button => Equals(button.Content, "新增 SSH 机器"));
            Assert.Contains("Primary", newLocal.Classes);
            Assert.DoesNotContain("Primary", newSsh.Classes);

            viewModel.Selected = viewModel.Machines.Single(machine => machine.Name == "iroh-build");
            Dispatcher.UIThread.RunJobs();
            var endpointBox = window.GetVisualDescendants().OfType<TextBox>().Single(box => box.Text == $"dev@{new string('a', 64)}");
            Assert.False(hostBox.IsEffectivelyVisible);
            Assert.True(endpointBox.IsEffectivelyVisible);
            Assert.True(window.GetVisualDescendants().OfType<ComboBox>().Single(box => Equals(box.SelectedItem, SshAuth.Agent) && box.IsEffectivelyVisible).IsEffectivelyVisible);
            endpointBox.Text = $"dev@{new string('b', 64)}";
            await viewModel.SaveCommand.ExecuteAsync(null);
            Assert.False(viewModel.StatusIsError, viewModel.Status);
            var saved = Assert.Single(store.Load().Machines, machine => machine.Name == "iroh-build");
            Assert.Equal((MachineKinds.Iroh, "bootstrap.internal", new string('b', MachineSpec.IrohEndpointIdLength)), (saved.Kind, saved.Host, saved.EndpointId));
            var irohHelp = window.GetVisualDescendants().OfType<Expander>().Single(expander => Equals(expander.Header, "目标端本地设置 Iroh"));
            Assert.False(irohHelp.IsVisible);
            viewModel.NewIrohCommand.Execute(null);
            Dispatcher.UIThread.RunJobs();
            Assert.True(irohHelp.IsVisible);
            window.Close();
        }
        finally
        {
            TempTree.Delete(home);
        }
    });

    [Theory]
    [InlineData(1280, 900)]
    [InlineData(960, 720)]
    public async Task MachinesPage_KeepsMachineSelectionAndDetailInsideTheViewport(int width, int height) => await HeadlessGui.RunAsync(async () =>
    {
        await using var environment = await GuiTestEnvironment.CreateAsync();
        var preferences = new GuiSettings(environment.App.Home);
        preferences.Save(preferences.Load() with { CloseAction = GuiSettings.CloseQuit });
        var window = new MainWindow(environment.App, await environment.OpenChatAsync()) { Width = width, Height = height };
        try
        {
            window.Show();
            var viewModel = window.ViewModel!;
            viewModel.Workspace.OpenPage(viewModel.Workspace.Pages.Available.Single(page => page.Id == WorkspacePages.Machines));
            Dispatcher.UIThread.RunJobs();
            window.CaptureRenderedFrame()?.Dispose();
            var machines = window.GetVisualDescendants().OfType<MachinesView>().Single();
            Assert.True(machines.IsEffectivelyVisible);
            AssertInsideViewport(machines, window);
            AssertInsideViewport(machines.FindControl<ListBox>("MachineList")!, window);
            AssertInsideViewport(machines.FindControl<ScrollViewer>("MachineDetailPane")!, window);
        }
        finally
        {
            window.Close();
            await window.ViewModel!.DisposeAsync();
            Dispatcher.UIThread.RunJobs();
        }
    });

    [Fact]
    public async Task ChatView_FollowsAppendsAndStreamingLayout_ButPreservesUserScrollUntilReturningToTail() => await HeadlessGui.RunAsync(async () =>
    {
        await using var environment = await GuiTestEnvironment.CreateAsync();
        var session = environment.Agent.Session;
        for (var index = 0; index < 14; index++)
            session.Append(new UserMessagePayload(MessageFactory.CreateUserText($"History {index}\nSecond line\nThird line")), new SurfaceOp.Append());
        await using var viewModel = new MainViewModel(environment.App, await environment.OpenChatAsync());
        var chat = new ChatView { DataContext = viewModel };
        var window = Show(chat);
        try
        {
            Render();
            var list = chat.FindControl<ListBox>("MessageList")!;
            var scroll = Assert.IsType<ScrollViewer>(list.Scroll);
            Assert.True(scroll.Extent.Height > scroll.Viewport.Height);
            var wheelPoint = scroll.TranslatePoint(new Point(40, 40), window)!.Value;
            window.MouseWheel(wheelPoint, new Vector(0, -1000));
            Render();

            var assistant = await StreamAsync(Paragraphs(0, 28) + "initial-tail", "initial-tail");
            AssertVisible("initial-tail");
            Assert.True(scroll.Extent.Height > 2 * scroll.Viewport.Height);

            await StreamAsync("\n\n" + Paragraphs(28, 12) + "grown-tail", "grown-tail");
            AssertVisible("grown-tail");

            window.Height -= 120;
            Render();
            AssertVisible("grown-tail");

            var layoutPosition = scroll.Offset.Y;
            assistant.SetText($"{assistant.Text}\n\n{Paragraphs(40, 12)}layout-positioned-tail");
            scroll.PageUp();
            Assert.True(scroll.Offset.Y < layoutPosition);
            window.UpdateLayout();
            Render();
            AssertVisible("layout-positioned-tail");

            var previousOffset = scroll.Offset.Y;
            var readingOffset = previousOffset;
            assistant.SetText($"{assistant.Text}\n\n{Paragraphs(40, 12)}unfollowed-tail");
            scroll.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.PageUp });
            readingOffset = scroll.Offset.Y;
            window.UpdateLayout();
            Render();
            Assert.True(readingOffset < previousOffset, "The actual keyboard scroll command must move away from the tail.");
            Assert.Equal(readingOffset, scroll.Offset.Y, 3);

            window.MouseWheel(wheelPoint, new Vector(0, -1000));
            Render();
            AssertVisible("unfollowed-tail");
            await StreamAsync("\n\n" + Paragraphs(52, 12) + "rearmed-tail", "rearmed-tail");
            AssertVisible("rearmed-tail");

            window.MouseWheel(wheelPoint, new Vector(0, 8));
            Render();
            readingOffset = scroll.Offset.Y;
            await StreamAsync("\n\n" + Paragraphs(64, 12) + "wheel-unfollowed-tail", "wheel-unfollowed-tail");
            Assert.Equal(readingOffset, scroll.Offset.Y, 3);

            window.MouseWheel(wheelPoint, new Vector(0, -1000));
            Render();
            AssertVisible("wheel-unfollowed-tail");
            await StreamAsync("\n\n" + Paragraphs(76, 12) + "wheel-rearmed-tail", "wheel-rearmed-tail");
            AssertVisible("wheel-rearmed-tail");

            session.Append(new AssistantMessagePayload(1, 1,
                MessageFactory.CreateAssistantMessage([new Dsh.Llm.TextBlock(assistant.Text)], "test", "test-model")), new SurfaceOp.Append());
            session.Append(new TurnEndPayload(1, new TurnEndReason.Completed()));
            await GuiTestEnvironment.WaitForAsync(assistant, () => !assistant.IsStreaming);
            session.Append(new AssistantMessagePayload(2, 1,
                MessageFactory.CreateAssistantMessage([new Dsh.Llm.TextBlock(Paragraphs(88, 8) + "appended-tail")], "test", "test-model")), new SurfaceOp.Append());
            await GuiTestEnvironment.WaitForAsync(viewModel.Messages,
                () => viewModel.Messages.Any(message => message.Text.EndsWith("appended-tail", StringComparison.Ordinal)));
            Render();
            AssertVisible("appended-tail");

            async Task<MessageViewModel> StreamAsync(string delta, string marker)
            {
                session.Append(new AssistantChunkPayload(1, 1, new StreamChunk.TextDelta(0, delta)));
                await GuiTestEnvironment.WaitForAsync(viewModel.Messages,
                    () => viewModel.Messages.Any(message => message.IsAssistant));
                var message = viewModel.Messages.Last(message => message.IsAssistant);
                await GuiTestEnvironment.WaitForAsync(message,
                    () => message.Text.EndsWith(marker, StringComparison.Ordinal));
                Render();
                return message;
            }

            void AssertVisible(string marker)
            {
                var block = Assert.Single(list.GetVisualDescendants().OfType<Avalonia.Controls.TextBlock>(),
                    block => block.Text == marker || string.Join("", block.Inlines?
                        .OfType<Avalonia.Controls.Documents.Run>().Select(run => run.Text) ?? []) == marker);
                Assert.True(block.IsEffectivelyVisible);
                AssertInsideViewport(block, scroll);
            }
        }
        finally
        {
            window.Close();
            Dispatcher.UIThread.RunJobs();
        }

        void Render()
        {
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            window.CaptureRenderedFrame()?.Dispose();
            Dispatcher.UIThread.RunJobs();
        }

        static string Paragraphs(int start, int count)
        {
            var text = new System.Text.StringBuilder();
            for (var index = start; index < start + count; index++)
                text.AppendLine($"Streaming paragraph {index}.").AppendLine();
            return text.ToString();
        }
    });

    private static void AssertInsideViewport(Control control, Control viewport)
    {
        var origin = control.TranslatePoint(default, viewport)!.Value;
        var bounds = new Avalonia.Rect(origin, control.Bounds.Size);
        Assert.True(bounds.Width > 0 && bounds.Height > 0 && new Avalonia.Rect(viewport.Bounds.Size).Contains(bounds),
            $"{control.Name} must remain inside the visible settings viewport: {bounds} in {viewport.Bounds.Size}.");
    }

    private static Window Show(Control view)
    {
        var window = new Window { Width = WindowWidth, Height = WindowHeight, Content = view };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return window;
    }
}
