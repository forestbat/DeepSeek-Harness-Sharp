using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Dsh.Gui.ViewModels;

/** 侧栏里的一个工作区分组: 标题 + 该工作区下的会话。 */
public sealed partial class WorkspaceGroupViewModel : ObservableObject
{
    public required string Name { get; init; }

    /** 分组的完整工作区路径(与 cwd 一致), 与最近工作区下拉共用同一键。 */
    public required string Path { get; init; }

    public ObservableCollection<SessionNodeViewModel> Sessions { get; } = [];

    [ObservableProperty]
    private bool _isExpanded = true;

    [RelayCommand]
    private void Toggle() => IsExpanded = !IsExpanded;
}
