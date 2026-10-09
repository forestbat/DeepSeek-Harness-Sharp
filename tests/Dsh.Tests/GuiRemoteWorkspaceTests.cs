using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Dsh.Gui.Services;
using Dsh.Gui.ViewModels;
using Dsh.Gui.Views;

namespace Dsh.Tests;

/**
 * 远程工作区: 用 GuiHeadless 在真实渲染的 GUI 里做交互(打开工作区菜单/悬停右拉/点击/设置页)
 * 并截图, 不抢占前台。
 * - UI 结构用例不需要服务器, 常规运行。
 * - 真实 SSH 连接用例需要 <repo>/artifacts020/ssh-verify.json(本地, 不进版本库), 缺失即跳过。
 */
public sealed class GuiRemoteWorkspaceTests
{
    private static readonly string ScreenshotDirectory = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "artifacts020", "gui-screenshots"));

    private static IReadOnlyList<SshWorkspace> SampleWorkspaces() =>
    [
        new SshWorkspace("实验室 GPU", "gpu-box.internal", 22, "dev", SshAuth.Password, RemotePath: "~/dsh-deploy"),
        new SshWorkspace("构建机", "build-box.internal", 22, "builder", SshAuth.Key, KeyPath: "~/.ssh/id_ed25519"),
    ];

    /** 工作区菜单里“最近工作区”与“打开新工作区…”之间应有“远程工作区”, 悬停右拉列出已保存条目; 设置页“远程”页签可编辑保存。 */
    [Fact]
    public async Task WorkspaceMenu_AndSettings_RemoteSurface() => await HeadlessGui.RunAsync(async () =>
    {
        var environment = await GuiTestEnvironment.CreateAsync();
        using var environmentScope = environment;
        new GuiSettings(environment.App.Home).Save(new GuiSettings(environment.App.Home).Load() with
        {
            RemoteWorkspaces = SampleWorkspaces(),
        });

        var window = new MainWindow(environment.App, environment.Agent);
        var viewModel = window.ViewModel!;
        window.Show();
        Dispatcher.UIThread.RunJobs();

        // 打开工作区切换浮层。
        var hostButton = window.GetVisualDescendants().OfType<Button>()
            .First(button => button.Flyout is Flyout && Equals(ToolTip.GetTip(button), "切换新会话默认工作区"));
        var workspaceFlyout = (Flyout)hostButton.Flyout!;
        workspaceFlyout.ShowAt(hostButton);
        Dispatcher.UIThread.RunJobs();

        var menu = (StackPanel)workspaceFlyout.Content!;
        var headers = menu.GetVisualDescendants().OfType<TextBlock>().Select(text => text.Text).ToList();
        Assert.Contains(headers, text => text!.StartsWith("最近工作区", StringComparison.Ordinal));
        var remoteEntry = menu.GetVisualDescendants().OfType<Button>()
            .First(button => button.Content is "远程工作区");
        var newWorkspace = menu.GetVisualDescendants().OfType<Button>()
            .First(button => button.Content is "打开新工作区…");
        // 顺序: “远程工作区”夹在“最近工作区”列表与“打开新工作区…”之间。
        var visuals = menu.GetVisualDescendants().ToList();
        Assert.True(visuals.IndexOf(remoteEntry) < visuals.IndexOf(newWorkspace));
        Capture(window, "headless-workspace-menu");

        // 悬停“远程工作区”即向右拉出子菜单, 列出已保存工作区。
        Hover(window, remoteEntry);
        Dispatcher.UIThread.RunJobs();
        var remoteFlyout = (Flyout)remoteEntry.Flyout!;
        Assert.True(remoteFlyout.IsOpen, "悬停后“远程工作区”右拉菜单应打开");

        var items = ((Control)remoteFlyout.Content!).GetVisualDescendants().OfType<Button>()
            .Where(button => button.DataContext is RemoteWorkspaceChoiceViewModel)
            .ToList();
        Assert.Equal(SampleWorkspaces().Count, items.Count);
        Assert.Contains(items, item => item.GetVisualDescendants().OfType<TextBlock>()
            .Any(text => text.Text == "实验室 GPU"));
        Capture(window, "headless-remote-workspace-flyout");

        remoteFlyout.Hide();
        workspaceFlyout.Hide();
        Dispatcher.UIThread.RunJobs();

        // 设置页“远程”页签: 列出连接, 编辑后保存落盘。
        viewModel.ShowSettingsCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();
        var remoteSection = viewModel.Preferences.Sections.First(section => section.Section == SettingsSection.Remote);
        remoteSection.SelectCommand!.Execute(remoteSection);
        Dispatcher.UIThread.RunJobs();
        Assert.True(viewModel.Preferences.IsRemoteSection);
        Assert.Equal(SampleWorkspaces().Count, viewModel.Preferences.RemoteWorkspaces.Count);
        Assert.NotNull(viewModel.Preferences.SelectedRemoteWorkspace);
        Capture(window, "headless-settings-remote");

        viewModel.Preferences.SelectedRemoteWorkspace!.Name = "改名后的工作区";
        viewModel.Preferences.SaveRemoteWorkspacesCommand.Execute(null);
        Assert.Contains(new GuiSettings(environment.App.Home).Load().RemoteWorkspaces,
            workspace => workspace.Name == "改名后的工作区");

        window.Close();
        Dispatcher.UIThread.RunJobs();
    });

    /** 真实 SSH: 从工作区菜单点击已保存的远程工作区, 经 ssh 起远端宿主并接入。 */
    [Fact]
    public async Task RemoteWorkspace_ConnectsOverSsh()
    {
        var spec = LoadSpec();
        if (spec is null)
        {
            Assert.Skip("未找到 artifacts020/ssh-verify.json, 跳过真实 SSH 验证");
            return;
        }

        await HeadlessGui.RunAsync(async () =>
        {
            var environment = await GuiTestEnvironment.CreateAsync();
            using var environmentScope = environment;
            var settings = new GuiSettings(environment.App.Home);
            settings.Save(settings.Load() with
            {
                RemoteWorkspaces =
                [
                    new SshWorkspace(spec.Name, spec.Host, spec.Port, spec.User, spec.Auth,
                        RemotePath: spec.RemotePath, Password: spec.Password),
                ],
            });

            var window = new MainWindow(environment.App, environment.Agent);
            var viewModel = window.ViewModel!;
            window.Show();
            Dispatcher.UIThread.RunJobs();

            var hostButton = window.GetVisualDescendants().OfType<Button>()
                .First(button => button.Flyout is Flyout && Equals(ToolTip.GetTip(button), "切换新会话默认工作区"));
            var workspaceFlyout = (Flyout)hostButton.Flyout!;
            workspaceFlyout.ShowAt(hostButton);
            Dispatcher.UIThread.RunJobs();
            var menu = (StackPanel)workspaceFlyout.Content!;
            var remoteEntry = menu.GetVisualDescendants().OfType<Button>()
                .First(button => button.Content is "远程工作区");
            Hover(window, remoteEntry);
            Dispatcher.UIThread.RunJobs();
            var item = ((Control)((Flyout)remoteEntry.Flyout!).Content!).GetVisualDescendants().OfType<Button>()
                .First(button => button.DataContext is RemoteWorkspaceChoiceViewModel);
            Click(window, item);

            for (var attempt = 0; attempt < 450; attempt++)
            {
                var status = viewModel.StatusText;
                if (status.Contains("已连接", StringComparison.Ordinal) || status.Contains("失败", StringComparison.Ordinal))
                    break;
                await Task.Delay(100, TestContext.Current.CancellationToken);
                Dispatcher.UIThread.RunJobs();
            }

            Assert.True(viewModel.StatusText.Contains("已连接", StringComparison.Ordinal),
                $"{spec.Name} 连接失败: {viewModel.StatusText}");
            Assert.NotNull(viewModel.RemoteHost);
            Capture(window, "headless-remote-workspace-connected");

            window.Close();
            Dispatcher.UIThread.RunJobs();
        });
    }

    private static Spec? LoadSpec()
    {
        var file = Path.Combine(Path.GetDirectoryName(ScreenshotDirectory)!, "ssh-verify.json");
        if (!File.Exists(file))
            return null;
        var specs = JsonSerializer.Deserialize<List<Spec>>(
            File.ReadAllText(file),
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
        return specs?.FirstOrDefault();
    }

    /** 用真实的无头鼠标移动触发控件的 PointerEntered(悬停)。 */
    private static void Hover(Window window, Control control)
    {
        var center = control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), window);
        if (center is { } point)
            window.MouseMove(point);
    }

    /** 用真实的无头鼠标点击控件(会走 Button.OnClick, 从而执行 Command)。 */
    private static void Click(Window window, Control control)
    {
        var center = control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), window);
        if (center is { } point)
        {
            window.MouseMove(point);
            window.MouseDown(point, MouseButton.Left);
            window.MouseUp(point, MouseButton.Left);
        }
    }

    private static void Capture(Window window, string name)
    {
        try
        {
            Directory.CreateDirectory(ScreenshotDirectory);
            using var frame = window.CaptureRenderedFrame();
            frame?.Save(Path.Combine(ScreenshotDirectory, $"{name}.png"), PngBitmapEncoderOptions.Default);
        }
        catch (Exception error)
        {
            Console.Error.WriteLine($"screenshot {name} failed: {error.Message}");
        }
    }

    public sealed record Spec(
        string Name,
        string Host,
        int Port,
        string User,
        string Auth,
        string? Password = null,
        string? KeyPath = null,
        string? Proxy = null,
        string? RemotePath = null);
}
