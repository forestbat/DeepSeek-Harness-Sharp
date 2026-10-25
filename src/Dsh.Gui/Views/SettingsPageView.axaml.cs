using Avalonia;
using Avalonia.Controls;
using Avalonia.VisualTree;
using Dsh.Gui.ViewModels;

namespace Dsh.Gui.Views;

public sealed partial class SettingsPageView : UserControl
{
    private SettingsViewModel? _networkViewModel;
    private readonly List<Control> _visibilityOwners = [];

    public SettingsPageView()
    {
        InitializeComponent();
        AttachedToVisualTree += OnNetworkAttached;
        DetachedFromVisualTree += OnNetworkDetached;
        DataContextChanged += OnNetworkDataContextChanged;
    }

    private void OnNetworkAttached(object? sender, VisualTreeAttachmentEventArgs e)
    {
        _networkViewModel = (DataContext as MainViewModel)?.Preferences;
        foreach (var owner in this.GetVisualAncestors().OfType<Control>().Prepend(this))
        {
            _visibilityOwners.Add(owner);
            owner.PropertyChanged += OnVisibilityChanged;
        }
    }

    private async void OnVisibilityChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property != IsVisibleProperty || _visibilityOwners.All(owner => owner.IsVisible))
            return;
        var viewModel = _networkViewModel;
        if (viewModel is null)
            return;
        viewModel.DeactivateNetwork();
        await viewModel.WaitForNetworkOperationsAsync();
    }

    private async void OnNetworkDetached(object? sender, VisualTreeAttachmentEventArgs e)
    {
        foreach (var owner in _visibilityOwners)
            owner.PropertyChanged -= OnVisibilityChanged;
        _visibilityOwners.Clear();
        var previous = _networkViewModel;
        _networkViewModel = null;
        if (previous is null)
            return;
        previous.DeactivateNetwork();
        await previous.WaitForNetworkOperationsAsync();
    }

    private async void OnNetworkDataContextChanged(object? sender, EventArgs e)
    {
        var previous = _networkViewModel;
        _networkViewModel = this.IsAttachedToVisualTree() ? (DataContext as MainViewModel)?.Preferences : null;
        if (ReferenceEquals(previous, _networkViewModel) || previous is null)
            return;
        previous.DeactivateNetwork();
        await previous.WaitForNetworkOperationsAsync();
    }
}
