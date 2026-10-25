using System.Runtime.Versioning;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Controls.Primitives;
using Avalonia.Dialogs;
using Avalonia.Threading;
using Dsh.Boot;
using Dsh.Fleet;
using Dsh.Gui;
using Dsh.Gui.ViewModels;
using Dsh.Gui.Views;
using Dsh.RemoteHost;

namespace Dsh.GuiViewsHarness;

/**
 * 用法: dsh-gui-views files|git|machines [项目路径]
 * 连接本机的 host(harness home 按 DSH_HOME / ~/.dsh 解析, 与 dsharp 自身一致; 只连接, 不拉起), 把一个视图放进一个裸窗口。
 * 文件选择框用 Avalonia 自绘的对话框, 画在同一个(私有 Xvfb)显示上, 不经桌面门户。
 * files 以项目路径为根目录, git 以项目路径为项目; machines 编辑同一个 harness home 下 settings.yaml 的 fleet 段。
 */
[SupportedOSPlatform("linux")]
internal static class Program
{
    private const int WindowWidth = 1400;
    private const int WindowHeight = 900;
    private const string BackgroundKey = "Brush.Bg.Window";
    private const string Usage = "usage: dsh-gui-views files|git|machines [project path]";

    [STAThread]
    public static int Main(string[] args)
    {
        if (args is not [var view, ..] || view is not ("files" or "git" or "machines"))
        {
            Console.Error.WriteLine(Usage);
            return 2;
        }

        var project = args.Length > 1 ? Path.GetFullPath(args[1]) : Environment.CurrentDirectory;
        var lifetime = new ClassicDesktopStyleApplicationLifetime { Args = args, ShutdownMode = ShutdownMode.OnMainWindowClose };
        AppBuilder.Configure<App>().UsePlatformDetect().UseManagedSystemDialogs().SetupWithLifetime(lifetime);
        var window = new Window { Title = $"dsh-gui-views {view}", Width = WindowWidth, Height = WindowHeight };
        window.Bind(TemplatedControl.BackgroundProperty, window.GetResourceObservable(BackgroundKey));
        lifetime.MainWindow = window;
        Dispatcher.UIThread.Post(() => _ = ShowAsync(window, view, project));
        return lifetime.Start(args);
    }

    private static async Task ShowAsync(Window window, string view, string project)
    {
        var home = HarnessHome.Default();
        var options = new FleetOptions { ClientName = $"gui-views@{Environment.MachineName}", AutoReconnect = false };
        if (view == "machines")
        {
            var machines = new MachinesViewModel(new MachineStore(home), options);
            machines.Load();
            window.Content = new MachinesView { DataContext = machines };
            return;
        }

        var connection = new MachineConnection(MachineSpec.Local(home.Root, "local"), options);
        window.Closed += async (_, _) => await connection.DisposeAsync();
        connection.Start();
        var host = (await connection.WaitConnectedAsync(CancellationToken.None)).Client;
        var style = RemotePaths.StyleFor((await host.InfoAsync()).Platform);
        window.Content = view == "files" ? await FilesAsync(host.Files, project, style) : await GitAsync(host.Git, host.Files, project, style);
    }

    private static async Task<Control> FilesAsync(RemoteFilesClient filesDomain, string project, RemotePathStyle style)
    {
        var files = new FilesViewModel(filesDomain, project, style);
        await files.LoadAsync();
        return new FilesView { DataContext = files };
    }

    private static async Task<Control> GitAsync(RemoteGitClient gitDomain, RemoteFilesClient filesDomain, string project, RemotePathStyle style)
    {
        var git = new GitViewModel(gitDomain, filesDomain, project, style);
        git.NewSessionRequested += async worktree => await Console.Out.WriteLineAsync($"new session requested in {worktree.Path}");
        await git.RefreshAsync();
        return new GitView { DataContext = git };
    }
}

