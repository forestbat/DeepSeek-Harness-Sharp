using Avalonia.Controls;
using Avalonia.Input;
using Dsh.Gui.ViewModels;

namespace Dsh.Gui.Views;

/** GUI 终端窗口: 本地 PTY 与远端 PTY 共用同一个 TerminalViewModel。 */
public sealed partial class TerminalWindow : Window
{
    /** XAML 编译器要求存在无参构造, 实际启动路径走下面的带参构造。 */
    public TerminalWindow() => InitializeComponent();

    public TerminalWindow(TerminalViewModel viewModel) : this() => DataContext = viewModel;

    private void OnInputKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || DataContext is not TerminalViewModel viewModel)
            return;
        viewModel.SendCommand.Execute(null);
        e.Handled = true;
    }
}
