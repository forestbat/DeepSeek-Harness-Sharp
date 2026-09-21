using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Platform;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.Input;
using Dsh.Boot;
using Dsh.Core;
using Dsh.Gui.Services;
using Dsh.Gui.ViewModels;

namespace Dsh.Gui.Views;

public sealed partial class MainWindow : Window
{
    private const double ScreenMargin = 60;
    private const double TitleBarHeight = 34;

    private readonly GuiSettings? _guiSettings;
    private CloseActionKind _closeAction = CloseActionKind.Tray;
    private TrayIcon? _tray;
    private bool _forceQuit;
    private bool _shutdownRequested;
    private ClosePromptDialog? _closePrompt;

    /** XAML 编译器要求存在无参构造, 实际启动路径走下面的带参构造。 */
    public MainWindow() => InitializeComponent();

    public MainWindow(HarnessApp app, AgentLoopAgent agent) : this()
    {
        _guiSettings = new GuiSettings(app.Home);
        _closeAction = ClosePolicy.Effective(ClosePolicy.Parse(_guiSettings.Load().CloseAction));
        ViewModel = new MainViewModel(app, agent);
        DataContext = ViewModel;
        ViewModel.DecisionRequested += ShowDecisionAsync;
        ViewModel.CopyRequested += CopyToClipboard;
        ViewModel.FilePicker = PickAsync;
        ViewModel.Preferences.Applied += ApplyAppearance;
        AddHandler(KeyDownEvent, OnPreviewKeyDown, RoutingStrategies.Tunnel);
        Dispatcher.UIThread.UnhandledException += OnDispatcherUnhandledException;
        SetUpTitleBar();
        SetUpTray();
    }

    public MainViewModel? ViewModel { get; }

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
            viewModel.Preferences.Applied -= ApplyAppearance;
            viewModel.Dispose();
        }
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
        // ExtendClientAreaChromeHints = ExtendClientAreaChromeHints.NoChrome;
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
        }
        catch (Exception)
        {
            // 托盘属于锦上添花: 没有通知区域时静默降级为普通窗口。
            _tray = null;
        }
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
        ShutdownApplication();
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

    /**
     * 托盘只是锦上添花: Avalonia 的 SNI 托盘在 DBus 调用失败时把异常抛到 dispatcher 上,
     * 不拦住会直接带走整个 GUI(实测 KDE 上 watcher 尚未就绪时进程收到 SIGABRT)。
     */
    private void OnDispatcherUnhandledException(object? sender, DispatcherUnhandledExceptionEventArgs e)
    {
        if (!IsTrayFailure(e.Exception))
            return;
        _ = Console.Error.WriteLineAsync($"dsh: tray icon unavailable: {e.Exception.Message}");
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

    private void OnPreviewKeyDown(object? sender, KeyEventArgs e)
    {
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

    private void OnTitleBarPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            ToggleMaximize();
            return;
        }
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            BeginMoveDrag(e);
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
}
