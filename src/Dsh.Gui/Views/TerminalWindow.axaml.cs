using Avalonia.Controls;
using Dsh.Gui.ViewModels;

namespace Dsh.Gui.Views;

/** GUI 终端窗口: 本地 PTY 与远端 PTY 共用同一个 TerminalViewModel 与终端控件。 */
public sealed partial class TerminalWindow : Window
{
    /** XAML 编译器要求存在无参构造, 实际启动路径走下面的带参构造。 */
    public TerminalWindow() => InitializeComponent();

    public TerminalWindow(TerminalViewModel viewModel) : this()
    {
        DataContext = viewModel;
        Terminal.TitleChanged += (_, title) => viewModel.ScreenTitle = title;
        Terminal.GridResized += (_, size) => viewModel.OnGridResized(size);
        ShowActivated = viewModel.ActivateOnOpen;
        Opened += (_, _) => { if (viewModel.ActivateOnOpen) Terminal.Focus(); };
    }
}
