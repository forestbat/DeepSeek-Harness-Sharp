using System.Collections.ObjectModel;
using System.Collections.Specialized;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Dsh.Gui.ViewModels;

/** 侧栏里的一个工作区分组: 标题 + 该工作区下的会话。 */
public sealed partial class WorkspaceGroupViewModel : ObservableObject
{
    private const int VisibleLimit = 20;

    public WorkspaceGroupViewModel() => Sessions.CollectionChanged += OnSessionsChanged;

    public required string Name { get; init; }

    /** 分组的完整工作区路径(与 cwd 一致), 与最近工作区下拉共用同一键。 */
    public required string Path { get; init; }

    public ObservableCollection<SessionNodeViewModel> Sessions { get; } = [];

    /** 默认只实例化前 20 行; 248 个会话全量套模板会生成几千个侧栏控件和样式对象。 */
    public ObservableCollection<SessionNodeViewModel> VisibleSessions { get; } = [];

    [ObservableProperty]
    private bool _isExpanded = true;

    [ObservableProperty]
    private bool _showAll;

    public bool HasMore => Sessions.Count > VisibleLimit;

    public bool ShowToggle => IsExpanded && HasMore;

    public string ShowAllLabel => ShowAll ? "收起" : $"显示全部（{Sessions.Count}）";

    partial void OnIsExpandedChanged(bool value)
    {
        _ = value;
        RefreshVisible();
    }

    partial void OnShowAllChanged(bool value)
    {
        _ = value;
        RefreshVisible();
    }

    [RelayCommand]
    private void Toggle() => IsExpanded = !IsExpanded;

    [RelayCommand]
    private void ToggleShowAll() => ShowAll = !ShowAll;

    private void OnSessionsChanged(object? sender, NotifyCollectionChangedEventArgs e) => RefreshVisible();

    private void RefreshVisible()
    {
        VisibleSessions.Clear();
        if (IsExpanded)
        {
            foreach (var session in ShowAll ? Sessions : Sessions.Take(VisibleLimit))
                VisibleSessions.Add(session);
        }
        OnPropertyChanged(nameof(HasMore));
        OnPropertyChanged(nameof(ShowToggle));
        OnPropertyChanged(nameof(ShowAllLabel));
    }
}
