using System.Windows.Input;

namespace Dsh.Gui.ViewModels;

/** 侧栏工作区下拉里的一项: 显示末段名 + 完整路径, 选中只改新会话默认目录(不切换当前会话)。 */
public sealed record WorkspaceChoiceViewModel(string Name, string Path, ICommand SelectCommand)
{
    public override string ToString() => Name;
}
