using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using CommunityToolkit.Mvvm.Input;
using Dsh.Gui.ViewModels;

namespace Dsh.Gui.Views;

public sealed partial class ChatView : UserControl
{
    private const double StickyTolerance = 24;
    private const RoutingStrategies ScrollInputRoutes = RoutingStrategies.Tunnel | RoutingStrategies.Bubble;

    private MainViewModel? _viewModel;
    private bool _sticky = true;
    private RoutedEventArgs? _scrollInput;
    private Flyout? _modelFlyout;
    private Flyout? _reasoningFlyout;
    private Flyout? _presetFlyout;
    private Flyout? _subagentFlyout;

    public ChatView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        MessageList.AddHandler(ScrollViewer.ScrollChangedEvent, OnScrollChanged);
        MessageList.AddHandler(InputElement.PointerWheelChangedEvent, OnScrollInput, ScrollInputRoutes, handledEventsToo: true);
        MessageList.AddHandler(InputElement.KeyDownEvent, OnScrollInput, ScrollInputRoutes, handledEventsToo: true);
        MessageList.AddHandler(InputElement.PointerPressedEvent, OnScrollInput, ScrollInputRoutes, handledEventsToo: true);
        MessageList.AddHandler(InputElement.PointerMovedEvent, OnScrollInput, ScrollInputRoutes, handledEventsToo: true);
        MessageList.AddHandler(InputElement.PointerReleasedEvent, OnScrollInput, ScrollInputRoutes, handledEventsToo: true);
        MessageList.PropertyChanged += (_, change) =>
        {
            if (change.Property != ListBox.ScrollProperty)
                return;
            if (change.OldValue is ScrollViewer previous)
                previous.PropertyChanged -= OnScrollPropertyChanged;
            if (change.NewValue is ScrollViewer current)
                current.PropertyChanged += OnScrollPropertyChanged;
        };
        PropertyChanged += (_, change) =>
        {
            if (change.Property == IsVisibleProperty && IsVisible)
                FocusComposer();
        };
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        Detach();
        _viewModel = DataContext as MainViewModel;
        if (_viewModel is null)
            return;
        _viewModel.ScrollRequested += ScrollToMessage;
        _viewModel.MessagesChanged += ScrollToEnd;
    }

    private void Detach()
    {
        if (_viewModel is null)
            return;
        _viewModel.ScrollRequested -= ScrollToMessage;
        _viewModel.MessagesChanged -= ScrollToEnd;
        _viewModel = null;
    }

    private void OnScrollInput(object? sender, RoutedEventArgs e) =>
        _scrollInput = e.Route == RoutingStrategies.Tunnel ? e : null;

    private bool IsScrollBarInput(Visual? source)
    {
        var inScrollBar = false;
        for (var visual = source; visual is not null; visual = visual.GetVisualParent())
        {
            if (visual is ScrollBar)
                inScrollBar = true;
            if (visual is ScrollViewer scroll)
                return inScrollBar && ReferenceEquals(scroll, MessageList.Scroll);
        }
        return false;
    }

    // Observe manual offset changes before ScrollChanged combines them with layout changes.
    private void OnScrollPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs change)
    {
        if (change.Property != ScrollViewer.OffsetProperty || sender is not ScrollViewer scroll)
            return;
        var manual = _scrollInput is PointerWheelEventArgs
            || _scrollInput is KeyEventArgs { Key: Key.Up or Key.Down or Key.PageUp or Key.PageDown or Key.Home or Key.End }
            || _scrollInput is PointerEventArgs pointer && IsScrollBarInput(pointer.Source as Visual);
        if (!manual)
            return;
        var previous = change.GetOldValue<Vector>().Y;
        var maximum = Math.Max(0, scroll.Extent.Height - scroll.Viewport.Height);
        // A smaller scroll range can clamp the offset without the reader scrolling.
        if (scroll.Offset.Y < Math.Min(previous, maximum) || !_sticky)
            _sticky = scroll.Offset.Y >= maximum - StickyTolerance;
    }

    /** Follow layout growth only while pinned; scrolling can leave or rejoin the tail. */
    private void OnScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        if (e.Source is not ScrollViewer scroll || !ReferenceEquals(scroll, MessageList.Scroll))
            return;
        var layoutChanged = e.ExtentDelta != default || e.ViewportDelta != default;
        // Preserve pinned intent through growth after immediate offset intent was observed.
        if (!layoutChanged || !_sticky)
            _sticky = scroll.Offset.Y + scroll.Viewport.Height >= scroll.Extent.Height - StickyTolerance;
        if (layoutChanged && _sticky)
            scroll.ScrollToEnd();
    }

    private void ScrollToMessage(MessageViewModel message)
    {
        if (_viewModel is null)
            return;
        if (!_viewModel.Messages.Contains(message))
            return;
        _sticky = true;
        Dispatcher.UIThread.Post(() => MessageList.ScrollIntoView(message));
    }

    private void ScrollToEnd()
    {
        if (!_sticky || _viewModel?.Messages.Count is not > 0)
            return;
        Dispatcher.UIThread.Post(() =>
        {
            if (_sticky && _viewModel?.Messages.Count is > 0)
                (MessageList.Scroll as ScrollViewer)?.ScrollToEnd();
        });
    }

    private void OnModelFlyoutOpened(object? sender, EventArgs e)
    {
        if (sender is not Flyout flyout)
            return;
        _modelFlyout = flyout;
        if (_viewModel is not null)
            _viewModel.ModelSearchText = "";
        if (flyout.Content is Panel panel && panel.Children.OfType<TextBox>().FirstOrDefault() is { } search)
            Dispatcher.UIThread.Post(() => search.Focus());
    }

    /** 命令先同步执行再关浮层: 关闭会让按钮脱离逻辑树, 依赖 Button.Command 绑定会在 OnClick 后段拿到 null; 命令本身已做轻量化(不重载整份设置)。 */
    private async void OnModelItemClick(object? sender, RoutedEventArgs e)
    {
        var execution = sender is Button { DataContext: ModelListItem item } && _viewModel is { } viewModel
            ? ExecuteViewCommandAsync(viewModel, viewModel.SwitchModelCommand, item.Name)
            : Task.CompletedTask;
        _modelFlyout?.Hide();
        await execution;
    }

    private void OnPresetFlyoutOpened(object? sender, EventArgs e)
    {
        _presetFlyout = sender as Flyout;
    }

    /** 命令先同步执行再关浮层, 同 OnModelItemClick。 */
    private async void OnPresetItemClick(object? sender, RoutedEventArgs e)
    {
        var execution = sender is Button { DataContext: PresetListItem item } && _viewModel is { } viewModel
            ? ExecuteViewCommandAsync(viewModel, viewModel.SetPresetCommand, item.Id)
            : Task.CompletedTask;
        _presetFlyout?.Hide();
        await execution;
    }

    private void OnSubagentFlyoutOpened(object? sender, EventArgs e)
        => _subagentFlyout = sender as Flyout;

    /** 命令先同步执行再关浮层, 同 OnPresetItemClick: 浮层关闭后按钮脱离逻辑树, Command 绑定会求值为 null。 */
    private async void OnSubagentItemClick(object? sender, RoutedEventArgs e)
    {
        var execution = sender is Button { DataContext: SubagentNodeViewModel node } && _viewModel is { } viewModel
            ? ExecuteViewCommandAsync(viewModel, viewModel.Subagents.OpenCommand, node)
            : Task.CompletedTask;
        _subagentFlyout?.Hide();
        await execution;
    }

    /** 打开浮层时按当前模型重算强度候选: 在线元数据是后台预热的, 打开时刻通常已就绪。 */
    private void OnReasoningFlyoutOpened(object? sender, EventArgs e)
    {
        _reasoningFlyout = sender as Flyout;
        _viewModel?.Preferences.RefreshReasoningEfforts();
    }

    private async void OnReasoningItemClick(object? sender, RoutedEventArgs e)
    {
        var execution = sender is Button { DataContext: string effort } && _viewModel is { } viewModel
            ? ExecuteViewCommandAsync(viewModel, viewModel.SwitchReasoningCommand, effort)
            : Task.CompletedTask;
        _reasoningFlyout?.Hide();
        await execution;
    }

    /** A detached view has no UI result to apply; active-view failures remain visible rather than escaping async void. */
    private static async Task ExecuteViewCommandAsync(MainViewModel viewModel, IAsyncRelayCommand command, object? parameter)
    {
        try
        {
            await command.ExecuteAsync(parameter);
        }
        catch (OperationCanceledException) when (viewModel.IsDisposed)
        {
            // Closing the GUI cancels its projection, not work already accepted by Host.
        }
        catch (Exception error) when (!viewModel.IsDisposed)
        {
            viewModel.SetStatus($"操作失败: {error.Message}");
            viewModel.ShowToast("操作失败", error.Message, true);
        }
    }

    private void FocusComposer() => Dispatcher.UIThread.Post(() =>
    {
        if (InputBox.IsEffectivelyVisible)
            InputBox.Focus();
    }, DispatcherPriority.Input);

    private async void OnInputKeyDown(object? sender, KeyEventArgs e)
    {
        if (_viewModel is null)
            return;
        if (e.Key == Key.V && (e.KeyModifiers & KeyModifiers.Control) != 0)
        {
            // 自己接管粘贴: 剪贴板有图片就转附件, 没有则退回文本粘贴。
            e.Handled = true;
            await PasteAsync();
            return;
        }
        if (_viewModel.IsSuggestionOpen)
        {
            switch (e.Key)
            {
                case Key.Down:
                    e.Handled = true;
                    _viewModel.MoveSuggestionCommand.Execute(1);
                    return;
                case Key.Up:
                    e.Handled = true;
                    _viewModel.MoveSuggestionCommand.Execute(-1);
                    return;
                case Key.Tab:
                case Key.Enter when e.KeyModifiers == KeyModifiers.None:
                    e.Handled = true;
                    _viewModel.ConfirmSuggestionCommand.Execute(null);
                    return;
                case Key.Escape:
                    e.Handled = true;
                    _viewModel.CloseSuggestionsCommand.Execute(null);
                    return;
            }
        }
        if (e.Key != Key.Enter || e.KeyModifiers != KeyModifiers.None)
            return;
        e.Handled = true;
        if (_viewModel.SubmitCommand.CanExecute(null))
            await ExecuteViewCommandAsync(_viewModel, _viewModel.SubmitCommand, null);
    }

    /** 剪贴板有图片 -> 转附件; 否则退回把剪贴板文本插到光标处(替代 TextBox 默认粘贴, 以便优先识别图片)。 */
    private async Task PasteAsync()
    {
        if (_viewModel is null)
            return;
        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard is null)
            return;
        try
        {
            var bitmap = await clipboard.TryGetBitmapAsync();
            if (bitmap is not null)
            {
                using var stream = new MemoryStream();
                bitmap.Save(stream, new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
                _viewModel.AttachImage("clipboard.png", stream.ToArray(), bitmap.PixelSize.Width, bitmap.PixelSize.Height);
                return;
            }
        }
        catch (Exception)
        {
        }
        try
        {
            var text = await clipboard.TryGetTextAsync();
            if (string.IsNullOrEmpty(text))
                return;
            var current = InputBox.Text ?? "";
            var index = Math.Clamp(InputBox.CaretIndex, 0, current.Length);
            InputBox.Text = current[..index] + text + current[index..];
            InputBox.CaretIndex = index + text.Length;
        }
        catch (Exception)
        {
        }
    }

    private void OnAttachmentDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (sender is Control { DataContext: AttachmentViewModel attachment })
            _viewModel?.OpenPreviewCommand.Execute(attachment);
    }

    private void OnPreviewOverlayPressed(object? sender, PointerPressedEventArgs e)
        => _viewModel?.ClosePreviewCommand.Execute(null);

    protected override void OnLoaded(RoutedEventArgs e)
    {
        base.OnLoaded(e);
        if (_viewModel is null)
            OnDataContextChanged(this, EventArgs.Empty);
        // TextBox 会先消费 Enter(AcceptsReturn), 必须在隧道阶段拦截。
        InputBox.AddHandler(InputElement.KeyDownEvent, OnInputKeyDown, RoutingStrategies.Tunnel);
        FocusComposer();
    }

    protected override void OnUnloaded(RoutedEventArgs e)
    {
        InputBox.RemoveHandler(InputElement.KeyDownEvent, OnInputKeyDown);
        Detach();
        base.OnUnloaded(e);
    }
}
