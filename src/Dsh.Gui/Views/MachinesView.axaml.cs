using Avalonia;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Dsh.Gui.ViewModels;
using Avalonia.VisualTree;

namespace Dsh.Gui.Views;

/**
 * 机器管理页; DataContext 为 MachinesViewModel(由集成方构造并调用 Load, 有常驻 FleetClient 时调用 AttachFleet, 订阅 MachinesChanged 重建连接)。
 * 诊断结果的修复命令可以选中复制, 也可以一键复制到剪贴板。
 */
public sealed partial class MachinesView : UserControl
{
    private const double SideBySideWidth = 760;
    private const double StackedListHeight = 220;
    private bool? _sideBySide;
    private MachinesViewModel? _networkViewModel;
    private readonly List<Control> _visibilityAncestors = [];

    public MachinesView()
    {
        InitializeComponent();
        AttachedToVisualTree += OnNetworkAttached;
        DetachedFromVisualTree += OnNetworkDetached;
        DataContextChanged += OnNetworkDataContextChanged;
        SizeChanged += OnSizeChanged;
    }

    private void OnSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        var sideBySide = e.NewSize.Width >= SideBySideWidth;
        if (_sideBySide == sideBySide)
            return;
        _sideBySide = sideBySide;
        MachineLayout.ColumnDefinitions = new ColumnDefinitions(sideBySide ? "360,6,*" : "*");
        MachineLayout.RowDefinitions = new RowDefinitions(sideBySide ? "*" : "Auto,*");
        MachineColumnSeparator.IsVisible = sideBySide;
        Grid.SetColumn(MachineColumnSeparator, sideBySide ? 1 : 0);
        Grid.SetColumn(MachineDetailPane, sideBySide ? 2 : 0);
        Grid.SetRow(MachineDetailPane, sideBySide ? 0 : 1);
        MachineListPane.MaxHeight = sideBySide ? double.PositiveInfinity : StackedListHeight;
        MachineDetailPane.Margin = sideBySide ? new Thickness(6, 0, 0, 0) : new Thickness(0, 12, 0, 0);
    }

    private void OnNetworkAttached(object? sender, VisualTreeAttachmentEventArgs e)
    {
        foreach (var ancestor in this.GetVisualAncestors().OfType<Control>().Prepend(this))
        {
            _visibilityAncestors.Add(ancestor);
            ancestor.PropertyChanged += OnNetworkVisibilityChanged;
        }
        _networkViewModel = DataContext as MachinesViewModel;
        UpdateNetworkVisibility();
    }

    private async void OnNetworkDetached(object? sender, VisualTreeAttachmentEventArgs e)
    {
        foreach (var ancestor in _visibilityAncestors)
            ancestor.PropertyChanged -= OnNetworkVisibilityChanged;
        _visibilityAncestors.Clear();
        var previous = _networkViewModel;
        _networkViewModel = null;
        if (previous is null)
            return;
        if (previous.DeactivateNetwork(this))
            await previous.WaitForNetworkOperationsAsync();
    }

    private async void OnNetworkDataContextChanged(object? sender, EventArgs e)
    {
        var previous = _networkViewModel;
        _networkViewModel = this.IsAttachedToVisualTree() ? DataContext as MachinesViewModel : null;
        if (ReferenceEquals(previous, _networkViewModel))
            return;
        var deactivated = previous?.DeactivateNetwork(this) == true;
        UpdateNetworkVisibility();
        if (deactivated && previous is not null)
            await previous.WaitForNetworkOperationsAsync();
    }

    private void OnNetworkVisibilityChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == IsVisibleProperty)
            UpdateNetworkVisibility();
    }

    private async void UpdateNetworkVisibility()
    {
        if (_networkViewModel is not { } viewModel)
            return;
        if (IsEffectivelyVisible)
            viewModel.ActivateNetwork(this);
        else if (viewModel.DeactivateNetwork(this))
        {
            await viewModel.WaitForNetworkOperationsAsync();
        }
    }

    private async void OnCopyFixClick(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is DoctorCheckRow { Fix: { } fix } && TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard)
            await clipboard.SetTextAsync(fix);
    }
}
