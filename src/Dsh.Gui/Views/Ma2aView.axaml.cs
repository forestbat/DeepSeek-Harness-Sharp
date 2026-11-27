using Avalonia;
using Avalonia.Controls;
using Avalonia.VisualTree;
using Dsh.Gui.ViewModels;

namespace Dsh.Gui.Views;

public sealed partial class Ma2aView : UserControl
{
    private Ma2aViewModel? _observedViewModel;
    private readonly List<Visual> _visibilityAncestors = [];

    public Ma2aView()
    {
        InitializeComponent();
        AttachedToVisualTree += OnAttached;
        DetachedFromVisualTree += OnDetached;
        DataContextChanged += (_, _) => UpdateObservation();
    }

    private void OnAttached(object? sender, VisualTreeAttachmentEventArgs args)
    {
        foreach (var visual in this.GetVisualAncestors().Prepend(this))
        {
            _visibilityAncestors.Add(visual);
            visual.PropertyChanged += OnVisualPropertyChanged;
        }
        UpdateObservation();
    }

    private void OnDetached(object? sender, VisualTreeAttachmentEventArgs args)
    {
        foreach (var visual in _visibilityAncestors)
            visual.PropertyChanged -= OnVisualPropertyChanged;
        _visibilityAncestors.Clear();
        Observe(null);
    }

    private void OnVisualPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs args)
    {
        if (args.Property == IsVisibleProperty) UpdateObservation();
    }

    private void UpdateObservation() =>
        Observe(IsEffectivelyVisible && this.IsAttachedToVisualTree() ? DataContext as Ma2aViewModel : null);

    private void Observe(Ma2aViewModel? viewModel)
    {
        if (ReferenceEquals(_observedViewModel, viewModel))
            return;
        _observedViewModel?.SetObservationActive(false);
        _observedViewModel = viewModel;
        viewModel?.SetObservationActive(true);
    }
}
