using System.Collections.Specialized;
using System.ComponentModel;
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
        _locatePending = false;
        if (_viewModel is null)
            return;
        _viewModel.PropertyChanged += OnViewModelPropertyChanged;
        _viewModel.FileSystemNodes.CollectionChanged += OnFileSystemNodesChanged;
        LocateCurrentSession();
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.IsFileSystemView))
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
        var path = FindSessionPath(viewModel.FileSystemNodes, viewModel.CurrentAgent.Id);
        if (path is null)
            return;
        foreach (var ancestor in path.SkipLast(1))
            ancestor.IsExpanded = true;
        var node = path[^1];
        foreach (var session in node.Sessions)
            session.IsSelected = session.SessionId == viewModel.CurrentAgent.Id;
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

    private void OnRenameKeyDown(object? sender, KeyEventArgs e)
    {
        if (sender is not TextBox { DataContext: SessionNodeViewModel node } || DataContext is not MainViewModel viewModel)
            return;
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            viewModel.CommitRenameCommand.Execute(node);
        }
        else if (e.Key == Key.Escape)
        {
            e.Handled = true;
            viewModel.CancelRenameCommand.Execute(node);
        }
    }

    private void OnRenameLostFocus(object? sender, RoutedEventArgs e)
    {
        if (sender is not TextBox { DataContext: SessionNodeViewModel node } || DataContext is not MainViewModel viewModel)
            return;
        if (node.IsRenaming)
            viewModel.CommitRenameCommand.Execute(node);
    }
}
