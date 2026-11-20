using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace Dsh.Gui.Views;

/**
 * 启动阶段连不上本机 Host 时的可见出口。
 * GUI 没有控制台, 之前这种失败的表现是"进程活着但永远没有窗口"; 这里把原因摆出来并给出重试。
 */
public sealed class StartupFailureWindow : Window
{
    private readonly TaskCompletionSource<bool> _retry = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public StartupFailureWindow(string reason)
    {
        Title = "dsh-gui 启动失败";
        Width = 640;
        Height = 300;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Content = Build(reason);
        Closed += (_, _) => _retry.TrySetResult(false);
    }

    /** 关掉窗口(含标题栏的关闭按钮)按"不重试"处理; 只有重试按钮才回到连接流程。 */
    public Task<bool> ShowAndWaitAsync()
    {
        Show();
        return _retry.Task;
    }

    private StackPanel Build(string reason)
    {
        var retry = new Button { Content = "重试连接", IsDefault = true };
        retry.Click += (_, _) =>
        {
            _retry.TrySetResult(true);
            Close();
        };
        var quit = new Button { Content = "退出" };
        quit.Click += (_, _) => Close();
        return new StackPanel
        {
            Margin = new Thickness(24),
            Spacing = 14,
            Children =
            {
                new TextBlock
                {
                    Text = "连不上本机 Host，主窗口没有打开。",
                    FontSize = 18,
                    FontWeight = FontWeight.SemiBold,
                    TextWrapping = TextWrapping.Wrap
                },
                new TextBlock { Text = reason, TextWrapping = TextWrapping.Wrap },
                new TextBlock
                {
                    Text = "如果 run 目录里的 host.port 指向的是旧版本遗留的宿主进程，结束它之后再重试。",
                    TextWrapping = TextWrapping.Wrap,
                    Opacity = 0.7
                },
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 12,
                    Children = { retry, quit }
                }
            }
        };
    }
}