using System.Collections.Specialized;
using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Dsh.Gui.ViewModels;
using Dsh.Llm;

namespace Dsh.Gui.Views;

public sealed partial class SidebarView : UserControl
{
    private MainViewModel? _viewModel;
    private bool _locatePending;
    private MenuFlyout? _recentMenu;
    private MenuItem? _recentOpenItem;
    private MenuItem? _recentRenameItem;
    private MenuItem? _recentExportItem;
    private MenuItem? _recentDeleteItem;
    private MenuItem? _recentStopItem;
    private MenuItem? _recentResumeItem;
    private MenuItem? _recentWindowItem;

    public SidebarView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (_viewModel is not null)
        {
            _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
            _viewModel.FileSystemNodes.CollectionChanged -= OnFileSystemNodesChanged;
        }
        _viewModel = DataContext as MainViewModel;
        _recentMenu = null;
        _recentOpenItem = null;
        _recentRenameItem = null;
        _recentExportItem = null;
        _recentDeleteItem = null;
        _recentStopItem = null;
        _recentResumeItem = null;
        _recentWindowItem = null;
        _locatePending = false;
        if (_viewModel is null)
            return;
        _viewModel.PropertyChanged += OnViewModelPropertyChanged;
        _viewModel.FileSystemNodes.CollectionChanged += OnFileSystemNodesChanged;
        LocateCurrentSession();
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MainViewModel.IsFileSystemView) or nameof(MainViewModel.CurrentSessionId))
            LocateCurrentSession();
    }

    private void OnFileSystemNodesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (_locatePending)
            LocateCurrentSession();
    }

    /** 文件系统视图下把树定位到当前会话所在目录: 展开祖先链, 选中并滚入视野; 会话尚未落盘时保持原样。 */
    private void LocateCurrentSession()
    {
        if (_viewModel is not { IsFileSystemView: true } viewModel)
            return;
        if (viewModel.FileSystemNodes.Count == 0)
        {
            _locatePending = true;
            return;
        }
        _locatePending = false;
        var path = FindSessionPath(viewModel.FileSystemNodes, viewModel.CurrentSessionId);
        if (path is null)
            return;
        foreach (var ancestor in path.SkipLast(1))
            ancestor.IsExpanded = true;
        var node = path[^1];
        foreach (var session in node.Sessions)
            session.IsSelected = session.SessionId == viewModel.CurrentSessionId;
        Dispatcher.UIThread.Post(() =>
        {
            FileTree.SelectedItem = node;
            FileTree.ScrollIntoView(node);
        }, DispatcherPriority.Background);
    }

    private static List<WorkspaceNodeViewModel>? FindSessionPath(IEnumerable<WorkspaceNodeViewModel> nodes, SessionId id)
    {
        foreach (var node in nodes)
        {
            if (node.Sessions.Any(session => session.SessionId == id))
                return [node];
            if (FindSessionPath(node.Children, id) is { } childPath)
                return [node, .. childPath];
        }
        return null;
    }

    private void OnRecentSessionMenuClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: SessionCardViewModel card } button || _viewModel is not { } viewModel)
            return;
        var dashboard = viewModel.Workspace.Dashboard;
        EnsureRecentMenu(dashboard);
        _recentOpenItem!.CommandParameter = card;
        _recentRenameItem!.CommandParameter = card;
        _recentExportItem!.CommandParameter = card;
        _recentDeleteItem!.CommandParameter = card;
        _recentStopItem!.CommandParameter = card;
        _recentResumeItem!.CommandParameter = card;
        _recentWindowItem!.CommandParameter = card;
        _recentMenu!.ShowAt(button);
    }

    private void EnsureRecentMenu(DashboardViewModel dashboard)
    {
        if (_recentMenu is not null)
            return;
        _recentOpenItem = new MenuItem { Header = "打开", Command = dashboard.OpenCommand };
        _recentRenameItem = new MenuItem { Header = "重命名", Command = dashboard.BeginRenameCommand };
        _recentExportItem = new MenuItem { Header = "导出日志", Command = dashboard.ExportLogCommand };
        _recentDeleteItem = new MenuItem { Header = "删除", Command = dashboard.DeleteCommand };
        _recentStopItem = new MenuItem { Header = "停止", Command = dashboard.StopCommand };
        _recentResumeItem = new MenuItem { Header = "恢复", Command = dashboard.ResumeCommand };
        _recentWindowItem = new MenuItem { Header = "在新窗口中打开", Command = dashboard.OpenInWindowCommand };
        _recentMenu = new MenuFlyout { Placement = PlacementMode.BottomEdgeAlignedRight };
        _recentMenu.Items.Add(_recentOpenItem);
        _recentMenu.Items.Add(_recentRenameItem);
        _recentMenu.Items.Add(_recentExportItem);
        _recentMenu.Items.Add(_recentDeleteItem);
        _recentMenu.Items.Add(new Separator());
        _recentMenu.Items.Add(_recentStopItem);
        _recentMenu.Items.Add(_recentResumeItem);
        _recentMenu.Items.Add(_recentWindowItem);
    }

    private void OnCardRenameAttached(object? sender, VisualTreeAttachmentEventArgs e)
    {
        if (sender is not TextBox box)
            return;
        Dispatcher.UIThread.Post(() =>
        {
            box.Focus();
            box.SelectAll();
        }, DispatcherPriority.Input);
    }

    private void OnCardRenameKeyDown(object? sender, KeyEventArgs e)
    {
        if (sender is not TextBox { DataContext: SessionCardViewModel card } || _viewModel is not { } viewModel)
            return;
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            viewModel.Workspace.Dashboard.CommitRenameCommand.Execute(card);
        }
        else if (e.Key == Key.Escape)
        {
            e.Handled = true;
            viewModel.Workspace.Dashboard.CancelRenameCommand.Execute(card);
        }
    }

    private void OnCardRenameLostFocus(object? sender, RoutedEventArgs e)
    {
        if (sender is not TextBox { DataContext: SessionCardViewModel card } || _viewModel is not { } viewModel)
            return;
        if (card.IsRenaming)
            viewModel.Workspace.Dashboard.CommitRenameCommand.Execute(card);
    }
}
