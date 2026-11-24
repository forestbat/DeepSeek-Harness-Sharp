using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace Dsh.Gui.Views;

/**
 * 启动失败要摆出来的内容: 标题说明"哪一步没成", 详情是原因。
 *
 * Connection 只在"连不上本机 Host"时为真: 只有那种情况才该建议去查 run 目录的 host.port;
 * 连上了但会话打不开时说同样的话, 会把用户引到完全不存在的旧宿主进程上(实测如此)。
 */
public readonly record struct StartupFailure(string Headline, string Detail, bool Connection);

/**
 * 启动阶段主窗口没打开时的可见出口。
 * GUI 没有控制台, 之前这种失败的表现是"进程活着但永远没有窗口"; 这里把原因摆出来并给出重试。
 */
public sealed class StartupFailureWindow : Window
{
    private readonly TaskCompletionSource<bool> _retry = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public StartupFailureWindow(StartupFailure failure)
    {
        Title = "dsh-gui 启动失败";
        Width = 640;
        Height = 300;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Content = Build(failure);
        Closed += (_, _) => _retry.TrySetResult(false);
    }

    /** 关掉窗口(含标题栏的关闭按钮)按"不重试"处理; 只有重试按钮才回到连接流程。 */
    public Task<bool> ShowAndWaitAsync()
    {
        Show();
        return _retry.Task;
    }

    private StackPanel Build(StartupFailure failure)
    {
        var retry = new Button { Content = "重试连接", IsDefault = true };
        retry.Click += (_, _) =>
        {
            _retry.TrySetResult(true);
            Close();
        };
        var quit = new Button { Content = "退出" };
        quit.Click += (_, _) => Close();
        var panel = new StackPanel
        {
            Margin = new Thickness(24),
            Spacing = 14,
            Children =
            {
                new TextBlock
                {
                    Text = failure.Headline,
                    FontSize = 18,
                    FontWeight = FontWeight.SemiBold,
                    TextWrapping = TextWrapping.Wrap
                },
                new TextBlock { Text = failure.Detail, TextWrapping = TextWrapping.Wrap },
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 12,
                    Children = { retry, quit }
                }
            }
        };
        if (failure.Connection)
            panel.Children.Insert(2, new TextBlock
            {
                Text = "如果 run 目录里的 host.port 指向的是旧版本遗留的宿主进程，结束它之后再重试。",
                TextWrapping = TextWrapping.Wrap,
                Opacity = 0.7
            });
        return panel;
    }
}