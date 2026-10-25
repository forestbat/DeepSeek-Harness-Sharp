using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Dsh.Gui.ViewModels;

namespace Dsh.Gui.Views;

/** The start page: every machine's sessions grouped by machine and project, with summary, filters, search and sort. */
public sealed partial class DashboardView : UserControl
{
    private MenuFlyout? _sessionMenu;
    private MenuItem? _openItem;
    private MenuItem? _renameItem;
    private MenuItem? _exportItem;
    private MenuItem? _deleteItem;
    private MenuItem? _stopItem;
    private MenuItem? _resumeItem;
    private MenuItem? _windowItem;

    public DashboardView() => InitializeComponent();

    private void OnSessionMenuClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: SessionCardViewModel card } button
            || DataContext is not DashboardViewModel viewModel)
            return;
        EnsureSessionMenu(viewModel);
        _openItem!.CommandParameter = card;
        _renameItem!.CommandParameter = card;
        _exportItem!.CommandParameter = card;
        _deleteItem!.CommandParameter = card;
        _stopItem!.CommandParameter = card;
        _resumeItem!.CommandParameter = card;
        _windowItem!.CommandParameter = card;
        _sessionMenu!.ShowAt(button);
    }

    private void EnsureSessionMenu(DashboardViewModel viewModel)
    {
        if (_sessionMenu is not null)
            return;
        _openItem = new MenuItem { Header = "打开", Command = viewModel.OpenCommand };
        _renameItem = new MenuItem { Header = "重命名", Command = viewModel.BeginRenameCommand };
        _exportItem = new MenuItem { Header = "导出日志", Command = viewModel.ExportLogCommand };
        _deleteItem = new MenuItem { Header = "删除", Command = viewModel.DeleteCommand };
        _stopItem = new MenuItem { Header = "停止", Command = viewModel.StopCommand };
        _resumeItem = new MenuItem { Header = "恢复", Command = viewModel.ResumeCommand };
        _windowItem = new MenuItem { Header = "在新窗口中打开", Command = viewModel.OpenInWindowCommand };
        _sessionMenu = new MenuFlyout { Placement = PlacementMode.BottomEdgeAlignedRight };
        _sessionMenu.Items.Add(_openItem);
        _sessionMenu.Items.Add(_renameItem);
        _sessionMenu.Items.Add(_exportItem);
        _sessionMenu.Items.Add(_deleteItem);
        _sessionMenu.Items.Add(new Separator());
        _sessionMenu.Items.Add(_stopItem);
        _sessionMenu.Items.Add(_resumeItem);
        _sessionMenu.Items.Add(_windowItem);
    }

    private void OnRenameAttached(object? sender, VisualTreeAttachmentEventArgs e)
    {
        if (sender is not TextBox box)
            return;
        Dispatcher.UIThread.Post(() =>
        {
            box.Focus();
            box.SelectAll();
        }, DispatcherPriority.Input);
    }

    private void OnRenameKeyDown(object? sender, KeyEventArgs e)
    {
        if (sender is not TextBox { DataContext: SessionCardViewModel card } || DataContext is not DashboardViewModel viewModel)
            return;
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            viewModel.CommitRenameCommand.Execute(card);
        }
        else if (e.Key == Key.Escape)
        {
            e.Handled = true;
            viewModel.CancelRenameCommand.Execute(card);
        }
    }

    private void OnRenameLostFocus(object? sender, RoutedEventArgs e)
    {
        if (sender is not TextBox { DataContext: SessionCardViewModel card } || DataContext is not DashboardViewModel viewModel)
            return;
        if (card.IsRenaming)
            viewModel.CommitRenameCommand.Execute(card);
    }
}
