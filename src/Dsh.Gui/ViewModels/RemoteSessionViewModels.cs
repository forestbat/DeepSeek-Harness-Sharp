using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Dsh.Gui.ViewModels;

/** 侧栏里的一个远端工作区分组: 标题为 user@host:路径, 下挂该工作区的远端会话。 */
public sealed partial class RemoteWorkspaceGroupViewModel : ObservableObject
{
    public required string Header { get; init; }

    public ObservableCollection<RemoteSessionNodeViewModel> Sessions { get; } = [];

    [ObservableProperty]
    private bool _isExpanded = true;

    [RelayCommand]
    private void Toggle() => IsExpanded = !IsExpanded;
}

/** 侧栏里的一个远端会话节点: 只读回放, 不绑定本地 agent。 */
public sealed partial class RemoteSessionNodeViewModel : ObservableObject
{
    public required string SessionId { get; init; }

    public required string Title { get; init; }

    public required string Summary { get; init; }

    public required string RelativeTime { get; init; }

    /** 远端会话工作目录(绝对路径): 远端相对路径/图片引用按它解析。 */
    public string Cwd { get; init; } = "";

    [ObservableProperty]
    private bool _isSelected;

    [ObservableProperty]
    private bool _isRunning;
}
