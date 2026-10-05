using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Dsh.Gui.ViewModels;

namespace Dsh.Gui.Views;

public sealed partial class ChatView : UserControl
{
    private const double StickyTolerance = 24;

    private MainViewModel? _viewModel;
    private bool _sticky = true;
    private Flyout? _modelFlyout;
    private Flyout? _reasoningFlyout;
    private Flyout? _presetFlyout;
    private Flyout? _subagentFlyout;

    public ChatView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        MessageList.AddHandler(ScrollViewer.ScrollChangedEvent, OnScrollChanged);
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

    /** 只在贴底时跟随流式输出; 用户向上翻阅时保持位置。 */
    private void OnScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        if (e.Source is not ScrollViewer scroll)
            return;
        _sticky = scroll.Offset.Y + scroll.Viewport.Height >= scroll.Extent.Height - StickyTolerance;
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
        Dispatcher.UIThread.Post(() => MessageList.ScrollIntoView(_viewModel.Messages[^1]));
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
    private void OnModelItemClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: ModelListItem item })
            _viewModel?.SwitchModelCommand.Execute(item.Name);
        _modelFlyout?.Hide();
    }

    private void OnPresetFlyoutOpened(object? sender, EventArgs e)
    {
        _presetFlyout = sender as Flyout;
    }

    /** 命令先同步执行再关浮层, 同 OnModelItemClick。 */
    private void OnPresetItemClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: PresetListItem item })
            _viewModel?.SetPresetCommand.Execute(item.Id);
        _presetFlyout?.Hide();
    }

    private void OnSubagentFlyoutOpened(object? sender, EventArgs e)
        => _subagentFlyout = sender as Flyout;

    /** 命令先同步执行再关浮层, 同 OnPresetItemClick: 浮层关闭后按钮脱离逻辑树, Command 绑定会求值为 null。 */
    private void OnSubagentItemClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: SubagentNodeViewModel node })
            _viewModel?.Subagents.OpenCommand.Execute(node);
        _subagentFlyout?.Hide();
    }

    /** 打开浮层时按当前模型重算强度候选: 在线元数据是后台预热的, 打开时刻通常已就绪。 */
    private void OnReasoningFlyoutOpened(object? sender, EventArgs e)
    {
        _reasoningFlyout = sender as Flyout;
        _viewModel?.Preferences.RefreshReasoningEfforts();
    }

    private void OnReasoningItemClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: string effort })
            _viewModel?.SwitchReasoningCommand.Execute(effort);
        _reasoningFlyout?.Hide();
    }

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
            _viewModel.SubmitCommand.Execute(null);
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
        // TextBox 会先消费 Enter(AcceptsReturn), 必须在隧道阶段拦截。
        InputBox.AddHandler(InputElement.KeyDownEvent, OnInputKeyDown, RoutingStrategies.Tunnel);
        InputBox.Focus();
    }

    protected override void OnUnloaded(RoutedEventArgs e)
    {
        InputBox.RemoveHandler(InputElement.KeyDownEvent, OnInputKeyDown);
        Detach();
        base.OnUnloaded(e);
    }
}
