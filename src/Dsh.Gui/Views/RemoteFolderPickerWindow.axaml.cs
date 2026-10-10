using Avalonia.Controls;
using Dsh.Gui.ViewModels;
using Dsh.RemoteHost;

namespace Dsh.Gui.Views;

/** “选择远端目录”对话框: 传入已连接的远端宿主, 返回用户选中的目录(取消返回 null)。 */
public sealed partial class RemoteFolderPickerWindow : Window
{
    private readonly string? _initial;

    /** XAML 编译器要求存在无参构造, 实际启动路径走下面的带参构造。 */
    public RemoteFolderPickerWindow() => InitializeComponent();

    public RemoteFolderPickerWindow(IRemoteHost host, string? initial) : this()
    {
        _initial = initial;
        ViewModel = new RemoteFolderPickerViewModel(host);
        DataContext = ViewModel;
        ViewModel.Accepted += () => Close(ViewModel.Result);
        ViewModel.Cancelled += () => Close(null);
    }

    public RemoteFolderPickerViewModel? ViewModel { get; }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        if (ViewModel is { } viewModel)
            _ = viewModel.NavigateAsync(_initial);
    }
}
