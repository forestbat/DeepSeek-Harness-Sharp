using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.Input;
using Dsh.Boot;
using Dsh.Gui.Services;
using Dsh.Gui.Terminal;
using Dsh.Gui.ViewModels;

namespace Dsh.Gui.Views;

public sealed partial class MainWindow : Window
{
    private const double ScreenMargin = 60;
    private const double TitleBarHeight = 34;

    /** What /plugins add accepts from a path: managed assemblies and native shared libraries. */
    private static readonly FilePickerFileType PluginLibraries = new("插件库") { Patterns = ["*.dll", "*.so", "*.dylib"] };

    private readonly GuiSettings? _guiSettings;
    private CloseActionKind _closeAction = CloseActionKind.Tray;
    private TrayIcon? _tray;
    private bool _forceQuit;
    private bool _shutdownRequested;
    private bool _preparingClose;
    private bool _closePrepared;
    private ClosePromptDialog? _closePrompt;

    /** XAML 编译器要求存在无参构造, 实际启动路径走下面的带参构造。 */
    public MainWindow() => InitializeComponent();

    public MainWindow(HarnessApp app, HostChatSession chat) : this()
    {
        _guiSettings = new GuiSettings(app.Home);
        _closeAction = ClosePolicy.Effective(ClosePolicy.Parse(_guiSettings.Load().CloseAction));
        ViewModel = new MainViewModel(app, chat);
        DataContext = ViewModel;
        ConnectWorkspace(ViewModel.Workspace);
        ViewModel.DecisionRequested += ShowDecisionAsync;
        Opened += (_, _) => ViewModel.RefreshPendingDialogs();
        ViewModel.CopyRequested += CopyToClipboard;
        ViewModel.TerminalRequested += ShowTerminal;
        ViewModel.FilePicker = PickAsync;
        ViewModel.Preferences.PluginFilePicker = PickPluginLibraryAsync;
        ViewModel.Workspace.Machines.IrohFilePicker = PickIrohExecutableAsync;
        ViewModel.Workspace.FolderPicker = async () => (await PickAsync(false, true)).FirstOrDefault();
        ViewModel.Preferences.Applied += ApplyAppearance;
        ViewModel.Preferences.Preview += ApplyPreview;
        AddHandler(KeyDownEvent, OnWorkspaceKeyDown, RoutingStrategies.Bubble);
        Dispatcher.UIThread.UnhandledException += OnDispatcherUnhandledException;
        SetUpTitleBar();
        SetUpTray();
    }

    public MainViewModel? ViewModel { get; }

    /** Session windows by view model, so the workspace can close or focus them. */
    private readonly Dictionary<FleetSessionViewModel, SessionWindow> _sessionWindows = [];

    private void ConnectWorkspace(WorkspaceViewModel workspace)
    {
        workspace.SessionWindowRequested += ShowSessionWindow;
        workspace.SessionWindowFocusRequested += session => _sessionWindows.GetValueOrDefault(session)?.Activate();
        workspace.SessionWindowCloseRequested += session => _sessionWindows.GetValueOrDefault(session)?.Close();
        workspace.NewSessionDialogRequested += form => _ = new NewSessionWindow(form).ShowDialog(this);
        // Opening a session from a notification navigates behind the panel; the panel closes so the session is visible.
        workspace.NavigationRequested += _ => BellButton.Flyout?.Hide();
        workspace.Notifications.IsForeground = () => IsActive;
        workspace.Notifications.DesktopNotify = DesktopNotifier.Create();
        workspace.RegisterHostPages(IconGeometry("Icon.Folder"), IconGeometry("Icon.Git"), IconGeometry("Icon.Machine"));
        workspace.Start();
    }

    private Geometry IconGeometry(string key) => (Geometry)this.FindResource(key)!;

    private void ShowSessionWindow(FleetSessionViewModel session)
    {
        var window = new SessionWindow(session);
        _sessionWindows[session] = window;
        window.Closed += (_, _) =>
        {
            _sessionWindows.Remove(session);
            if (ViewModel is { } viewModel)
                // The workspace retains this retirement and joins it before replacing or disposing the fleet.
                _ = viewModel.Workspace.SessionWindowClosedAsync(session);
        };
        window.Show(this);
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        RestoreBounds();
        ClampToScreen();
    }

    protected override async void OnClosing(WindowClosingEventArgs e)
    {
        if (!_forceQuit && _closeAction != CloseActionKind.Quit)
        {
            e.Cancel = true;
            try
            {
                if (_closeAction == CloseActionKind.Ask)
                {
                    await AskCloseActionAsync();
                    return;
                }
                if (!ClosePolicy.TrayAvailable())
                {
                    // 桌面托盘宿主已经退出(或从未就绪), 藏起来就找不回来了。
                    ViewModel?.SetStatus("托盘不可用，改为直接退出");
                    Quit();
                    return;
                }
            }
            catch (Exception error)
            {
                // 询问窗口自身出错时不能让 async void 把进程带走: 退回最小化到托盘。
                ViewModel?.SetStatus($"关闭询问失败, 已最小化到托盘: {error.Message}");
            }
            HideToTray();
            return;
        }
        if (!_closePrepared)
        {
            e.Cancel = true;
            if (_preparingClose)
                return;
            _preparingClose = true;
            IsEnabled = false;
            try
            {
                if (ViewModel is { } viewModel)
                    await viewModel.DisposeAsync();
                _closePrepared = true;
                Dispatcher.UIThread.Post(Close);
            }
            catch (Exception error)
            {
                ViewModel?.SetStatus($"退出清理失败: {Dsh.Runtime.OutboundProxy.Redact(error.Message)}");
                IsEnabled = true;
            }
            finally
            {
                _preparingClose = false;
            }
            return;
        }
        ViewModel?.PersistWindowBounds(Width, Height, Position.X, Position.Y, WindowState == WindowState.Maximized);
        _forceQuit = true;
        base.OnClosing(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        if (ViewModel is { } viewModel)
        {
            viewModel.DecisionRequested -= ShowDecisionAsync;
            viewModel.CopyRequested -= CopyToClipboard;
            viewModel.TerminalRequested -= ShowTerminal;
            viewModel.Preferences.Applied -= ApplyAppearance;
            viewModel.Preferences.Preview -= ApplyPreview;
        }
        // 已登记的图标由 Avalonia 在 dispatcher 关闭时统一 Dispose; 先摘除可避免 12.1.2 那种非幂等 Dispose 被调两次。
        if (_tray is { } tray && Application.Current is { } trayApp && TrayIcon.GetIcons(trayApp) is { } icons)
            icons.Remove(tray);
        else
            _tray?.Dispose();
        base.OnClosed(e);
        if (_forceQuit)
            ShutdownApplication();
    }

    public void ShowFromTray()
    {
        Show();
        if (WindowState == WindowState.Minimized)
            WindowState = WindowState.Normal;
        Activate();
    }

    private void SetUpTitleBar()
    {
        if (!OperatingSystem.IsWindows())
            return;
        ExtendClientAreaToDecorationsHint = true;
        ExtendClientAreaTitleBarHeightHint = TitleBarHeight;
        WindowDecorations = WindowDecorations.BorderOnly;
        TitleBar.IsVisible = true;
    }

    private void SetUpTray()
    {
        if (!ClosePolicy.TraySupported)
            return;
        try
        {
            var menu = new NativeMenu();
            menu.Add(new NativeMenuItem("显示主窗口") { Command = new RelayCommand(ShowFromTray) });
            menu.Add(new NativeMenuItem("新会话") { Command = new RelayCommand(() => ViewModel?.NewSessionCommand.Execute(null)) });
            menu.Add(new NativeMenuItem("取消当前任务") { Command = new RelayCommand(() => ViewModel?.CancelTaskCommand.Execute(null)) });
            menu.Add(new NativeMenuItemSeparator());
            menu.Add(new NativeMenuItem("设置") { Command = new RelayCommand(() => ViewModel?.ShowSettingsCommand.Execute(null)) });
            menu.Add(new NativeMenuItem("退出") { Command = new RelayCommand(Quit) });
            _tray = new TrayIcon
            {
                Icon = LoadTrayIcon(),
                ToolTipText = "DeepSeek Harness",
                Menu = menu,
                IsVisible = true,
            };
            _tray.Clicked += (_, _) => ShowFromTray();
            RegisterTrayIcon();
        }
        catch (Exception)
        {
            // 托盘属于锦上添花: 没有通知区域时静默降级为普通窗口。
            _tray = null;
        }
    }

    /**
     * 图标必须登记到挂在 Application 上的 TrayIcons 集合里: Avalonia 12.1.3 起原生图标只在 Attach() 中创建,
     * 而 Attach() 只由该集合触发(12.1.2 及以前是构造即创建)。漏登记不会抛异常, 只是静默没有图标。
     */
    private void RegisterTrayIcon()
    {
        if (_tray is null || Application.Current is not { } app)
            return;
        var icons = TrayIcon.GetIcons(app) ?? new TrayIcons();
        icons.Add(_tray);
        TrayIcon.SetIcons(app, icons);
    }

    /** Windows 用多尺寸 ico; Linux/macOS 的托盘(SNI/NSStatusItem)要 png。 */
    private static WindowIcon? LoadTrayIcon()
    {
        var asset = OperatingSystem.IsWindows() ? "avares://Dsh.Gui/Assets/icon.ico" : "avares://Dsh.Gui/Assets/icon.png";
        try
        {
            using var stream = AssetLoader.Open(new Uri(asset));
            return new WindowIcon(stream);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private async Task AskCloseActionAsync()
    {
        if (_closePrompt is not null)
            return;
        _closePrompt = new ClosePromptDialog(ClosePolicy.TraySupported);
        var result = await _closePrompt.ShowDialog<(bool Quit, bool Remember)?>(this);
        _closePrompt = null;
        if (result is not { } choice)
            return;
        if (choice.Remember && _guiSettings is not null)
        {
            _closeAction = choice.Quit ? CloseActionKind.Quit : CloseActionKind.Tray;
            _guiSettings.Save(_guiSettings.Load() with { CloseAction = ClosePolicy.Wire(_closeAction) });
        }
        if (choice.Quit || !ClosePolicy.TraySupported)
            Quit();
        else
            HideToTray();
    }

    private void HideToTray()
    {
        Hide();
        ViewModel?.SetStatus("已最小化到托盘，托盘菜单可退出");
    }

    private void Quit()
    {
        _forceQuit = true;
        Close();
    }

    /**
     * 生命周期是 OnExplicitShutdown: 关闭窗口本身不会退出进程, 必须显式 Shutdown。
     * 只能在窗口关闭之后调用(见 OnClosed), 在 OnClosing 里调会重入关闭流程导致死循环。
     */
    private void ShutdownApplication()
    {
        if (_shutdownRequested)
            return;
        _shutdownRequested = true;
        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.Shutdown();
            return;
        }
        Close();
    }

    private void RestoreBounds()
    {
        if (_guiSettings?.Load() is not { RememberBounds: true } snapshot)
            return;
        if (snapshot.WindowWidth > 0)
            Width = snapshot.WindowWidth;
        if (snapshot.WindowHeight > 0)
            Height = snapshot.WindowHeight;
        if (snapshot.WindowX is { } x && snapshot.WindowY is { } y)
            Position = new PixelPoint((int)x, (int)y);
        if (snapshot.WindowMaximized)
            WindowState = WindowState.Maximized;
    }

    /** XAML 里的尺寸是逻辑单位, 高 DPI 下可能超过屏幕工作区; 超出部分会被系统裁掉且不可见。 */
    private void ClampToScreen()
    {
        if (Screens.Primary is not { } screen)
            return;
        var availableWidth = screen.WorkingArea.Width / screen.Scaling;
        var availableHeight = screen.WorkingArea.Height / screen.Scaling;
        Width = Math.Min(Width, Math.Max(MinWidth, availableWidth - ScreenMargin));
        Height = Math.Min(Height, Math.Max(MinHeight, availableHeight - ScreenMargin));
    }

    private void ApplyAppearance()
    {
        if (Application.Current is { } app && _guiSettings is not null)
            ThemeService.Apply(app, _guiSettings.Load());
    }

    /** 实时预览用内存里的字号, 不读盘; 落盘由设置页"保存外观"负责。 */
    private void ApplyPreview()
    {
        if (Application.Current is { } app && _guiSettings is not null)
            ThemeService.Apply(app, _guiSettings.Load() with { FontSize = ViewModel!.Preferences.FontSize });
    }

    /**
     * 托盘只是锦上添花: Avalonia 的 SNI 托盘在 DBus 调用失败时把异常抛到 dispatcher 上,
     * 不拦住会直接带走整个 GUI(实测 KDE 上 watcher 尚未就绪时进程收到 SIGABRT)。
     */
    private void OnDispatcherUnhandledException(object? sender, DispatcherUnhandledExceptionEventArgs e)
    {
        if (!IsTrayFailure(e.Exception))
            return;
        _ = Console.Error.WriteLineAsync($"dsharp: tray icon unavailable: {e.Exception.Message}");
        e.Handled = true;
    }

    private static bool IsTrayFailure(Exception error)
    {
        var trace = error.StackTrace ?? string.Empty;
        return trace.Contains("DBusTrayIcon", StringComparison.Ordinal)
            || trace.Contains("Avalonia.FreeDesktop", StringComparison.Ordinal)
            || trace.Contains("TrayIcon", StringComparison.Ordinal)
            || error.Source?.Contains("Tmds.DBus", StringComparison.Ordinal) == true;
    }

    /** Workspace shortcuts run only after focused controls have had their input; PTY keys never become GUI commands. */
    private void OnWorkspaceKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Handled || e.Source is TerminalControl || ViewModel is not { } viewModel)
            return;
        ICommand? command = (e.Key, e.KeyModifiers) switch
        {
            (Key.N, KeyModifiers.Control) => viewModel.NewSessionCommand,
            (Key.OemComma, KeyModifiers.Control) => viewModel.ShowSettingsCommand,
            (Key.Escape, KeyModifiers.None) => viewModel.GoBackCommand,
            (Key.K, KeyModifiers.Control) => viewModel.Workspace.Palette.ToggleCommand,
            (Key.D1, KeyModifiers.Control) => viewModel.ShowConversationCommand,
            (Key.D2, KeyModifiers.Control) => viewModel.ShowTraceCommand,
            (Key.B, KeyModifiers.Control) => viewModel.ToggleSidebarCommand,
            _ => null,
        };
        if (command is not null)
        {
            if (command.CanExecute(null))
            {
                e.Handled = true;
                command.Execute(null);
            }
            return;
        }
        if (!e.KeyModifiers.HasFlag(KeyModifiers.Control))
            return;
        if (e.Key == Key.W)
        {
            e.Handled = true;
            Close();
        }
        else if (e.Key == Key.Q)
        {
            e.Handled = true;
            Quit();
        }
    }

    private void OnMinimizeClick(object? sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void OnMaximizeClick(object? sender, RoutedEventArgs e) => ToggleMaximize();

    private void OnCloseClick(object? sender, RoutedEventArgs e) => Close();

    private void ToggleMaximize()
        => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private async Task<object?> ShowDecisionAsync(DecisionViewModel decision)
    {
        var dialog = new DecisionDialog(decision);
        return await dialog.ShowDialog<object?>(this);
    }

    private void CopyToClipboard(string text) => _ = CopyAsync(text);

    private void ShowTerminal(TerminalViewModel terminal)
    {
        var window = new TerminalWindow(terminal);
        window.Closed += async (_, _) => await terminal.DisposeAsync();
        window.Show(this);
    }

    private async Task CopyAsync(string text)
    {
        try
        {
            if (Clipboard is { } clipboard)
                await clipboard.SetTextAsync(text);
            ViewModel?.SetStatus("已复制到剪贴板");
        }
        catch (Exception error)
        {
            ViewModel?.SetStatus($"复制失败: {error.Message}");
        }
    }

    private async Task<IReadOnlyList<string>> PickAsync(bool allowMultiple, bool folders)
    {
        if (StorageProvider is not { } storage)
            return [];
        if (folders)
        {
            var picked = await storage.OpenFolderPickerAsync(new FolderPickerOpenOptions { AllowMultiple = false, Title = "选择文件夹" });
            return [.. picked.Select(folder => folder.TryGetLocalPath()).Where(path => path is not null).Select(path => path!)];
        }
        var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions { AllowMultiple = allowMultiple, Title = "选择文件" });
        return [.. files.Select(file => file.TryGetLocalPath()).Where(path => path is not null).Select(path => path!)];
    }

    private async Task<string?> PickPluginLibraryAsync()
    {
        if (StorageProvider is not { } storage)
            return null;
        var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            AllowMultiple = false,
            Title = "选择插件",
            FileTypeFilter = [PluginLibraries, FilePickerFileTypes.All],
        });
        return files.Select(file => file.TryGetLocalPath()).FirstOrDefault(path => path is not null);
    }

    private async Task<string?> PickIrohExecutableAsync()
    {
        if (StorageProvider is not { } storage)
            return null;
        var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            AllowMultiple = false,
            Title = "选择 iroh-ssh",
            FileTypeFilter = [FilePickerFileTypes.All],
        });
        return files.Select(file => file.TryGetLocalPath()).FirstOrDefault(path => path is not null);
    }

}
