using Avalonia.Controls;
using Avalonia.Threading;
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

    protected override void OnAttachedToVisualTree(Avalonia.VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        Attach();
        Dispatcher.UIThread.Post(ScrollToEnd, DispatcherPriority.Loaded);
    }

    protected override void OnDetachedFromVisualTree(Avalonia.VisualTreeAttachmentEventArgs e)
    {
        Detach();
        base.OnDetachedFromVisualTree(e);
    }

    private void Attach()
    {
        if (_viewModel is not null)
            _viewModel.TranscriptChanged -= ScrollToEnd;
        _viewModel = DataContext as MobileShellViewModel;
        if (_viewModel is not null)
            _viewModel.TranscriptChanged += ScrollToEnd;
    }

    private void Detach()
    {
        if (_viewModel is null)
            return;
        _viewModel.TranscriptChanged -= ScrollToEnd;
        _viewModel = null;
    }

    private void ScrollToEnd()
    {
        if (_viewModel is null || _viewModel.Messages.Count == 0)
            return;
        MessageScroll.ScrollToEnd();
    }
}
