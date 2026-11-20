using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;

namespace Dsh.Gui;

public sealed class App : Application
{
    /** 启动时应用主题/字号(GuiRunner 从 GUI 插件参数段读出后注入)。 */
    public static Action<Application>? StartupAppearance { get; set; }

    /**
     * 框架初始化完成后由 GuiRunner 挂上的启动流程: 它在后台连本机 Host, 连上才建主窗口。
     * 启动路径上不同步等握手 —— 那样连不上时进程会活着却永远没有窗口。
     */
    public static Action? StartupHook { get; set; }

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime)
            StartupAppearance?.Invoke(this);
        base.OnFrameworkInitializationCompleted();
        StartupHook?.Invoke();
    }
}