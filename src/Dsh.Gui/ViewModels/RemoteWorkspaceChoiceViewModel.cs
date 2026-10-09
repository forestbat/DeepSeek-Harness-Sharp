using System.Windows.Input;

namespace Dsh.Gui.ViewModels;

/** 工作区下拉里“远程工作区”子菜单的一项: 显示名 + user@host:port, 点击打开该远程工作区。 */
public sealed record RemoteWorkspaceChoiceViewModel(string Name, string Summary, ICommand OpenCommand)
{
    public override string ToString() => Name;
}
