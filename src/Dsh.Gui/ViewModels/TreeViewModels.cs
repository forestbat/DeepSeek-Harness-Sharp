using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Dsh.Gui.ViewModels;

/** 文件系统视图的一个目录节点: 子目录 + 该目录下打开的会话。 */
public sealed partial class WorkspaceNodeViewModel : ObservableObject
{
    public required string Name { get; init; }

    public required string FullPath { get; init; }

    public ObservableCollection<WorkspaceNodeViewModel> Children { get; } = [];

    public ObservableCollection<SessionNodeViewModel> Sessions { get; } = [];

    public string Header => Sessions.Count > 0 ? $"{Name} ({Sessions.Count})" : Name;

    [ObservableProperty]
    private bool _isExpanded = true;

    [RelayCommand]
    private void Toggle() => IsExpanded = !IsExpanded;
}

/** 侧栏排序方式。 */
public sealed partial class TraceFilterViewModel(TraceKind? kind, string label) : ObservableObject
{
    public TraceKind? Kind { get; } = kind;

    public string Label { get; } = label;

    [ObservableProperty]
    private bool _isSelected;
}

/** 命令/@ 候选浮层里的一条候选。 */
public sealed record SuggestionViewModel(string Kind, string Label, string Description, string InsertText);

/** 模型浮层的一项: Name 为 provider/model, IsCurrent 标记当前会话使用的模型。 */
public sealed record ModelListItem(string Name, bool IsCurrent);
