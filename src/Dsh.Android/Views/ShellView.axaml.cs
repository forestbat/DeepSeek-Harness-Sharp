using Avalonia.Controls;
using Dsh.Android.ViewModels;

namespace Dsh.Android.Views;

public sealed partial class ShellView : UserControl
{
    private MobileShellViewModel? _viewModel;

    public ShellView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => Attach();
    }

    private void Attach()
    {
        if (_viewModel is not null)
            _viewModel.TranscriptChanged -= ScrollToEnd;
        _viewModel = DataContext as MobileShellViewModel;
        if (_viewModel is not null)
            _viewModel.TranscriptChanged += ScrollToEnd;
    }

    private void ScrollToEnd()
    {
        if (_viewModel is null || _viewModel.Messages.Count == 0)
            return;
        MessageScroll.ScrollToEnd();
    }
}
