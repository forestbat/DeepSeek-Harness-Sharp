using System.ComponentModel;
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
    private const double AnchorTolerance = 0.5;
    private const int MaxAnchorPasses = 4;

    /** 一格滚轮在内层滚动区域里滚动的像素数(与常见 3 行文本高度相当)。 */
    private const double NestedWheelStepPixels = 60;

    private const RoutingStrategies ScrollInputRoutes = RoutingStrategies.Tunnel | RoutingStrategies.Bubble;

    private MainViewModel? _viewModel;
    private ComposerViewModel? _composer;
    private bool _syncingCaret;
    private bool _sticky = true;
    private bool _scrollEndPosted;
    private long _scrollGeneration;
    private ListBox? _anchorList;
    private ScrollViewer? _anchorScroll;
    private object? _anchorData;
    private double _anchorTop;
    private int _anchorPasses;
    private bool _anchorRealizePosted;
    private bool _draggingThumb;

    /** 上一次滚动事件时的位置快照, 用来判断"这次布局变化之前视口是否在底部"。 */
    private ScrollSnapshot _lastScroll;
    private Flyout? _modelFlyout;
    private Flyout? _reasoningFlyout;
    private Flyout? _subagentFlyout;

    public ChatView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        InputBox.PropertyChanged += OnInputBoxPropertyChanged;
        MessageList.AddHandler(ScrollViewer.ScrollChangedEvent, OnScrollChanged);
        // 卡片/结果里的内层滚动优先吃掉滚轮: 否则同一手势会同时滚动内层和消息列表, 表现为内容乱跳。
        MessageList.AddHandler(
            InputElement.PointerWheelChangedEvent,
            OnNestedWheel,
            RoutingStrategies.Tunnel,
            handledEventsToo: true);
        MessageList.AddHandler(InputElement.PointerWheelChangedEvent, OnScrollInput, ScrollInputRoutes, handledEventsToo: true);
        MessageList.AddHandler(InputElement.KeyDownEvent, OnScrollInput, ScrollInputRoutes, handledEventsToo: true);
        MessageList.AddHandler(InputElement.PointerPressedEvent, OnScrollInput, ScrollInputRoutes, handledEventsToo: true);
        MessageList.AddHandler(InputElement.PointerMovedEvent, OnScrollInput, ScrollInputRoutes, handledEventsToo: true);
        MessageList.AddHandler(InputElement.PointerReleasedEvent, OnScrollInput, ScrollInputRoutes, handledEventsToo: true);
        MessageList.AddHandler(
            Thumb.DragStartedEvent,
            (_, _) => _draggingThumb = true,
            RoutingStrategies.Bubble,
            handledEventsToo: true);
        MessageList.AddHandler(
            Thumb.DragCompletedEvent,
            (_, _) => _draggingThumb = false,
            RoutingStrategies.Bubble,
            handledEventsToo: true);
        MessageList.AddHandler(DiffView.CardToggleRequestedEvent, OnCardToggleRequested, RoutingStrategies.Bubble);
        AddHandler(InputElement.PointerPressedEvent, OnSelectablePointerPressed, RoutingStrategies.Tunnel, handledEventsToo: true);
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
        _viewModel.MessagesChanged += QueueScrollToEnd;
        _composer = _viewModel.Composer;
        _composer.PropertyChanged += OnComposerPropertyChanged;
        SyncCaretFromView();
    }

    private void Detach()
    {
        ClearAnchor();
        if (_composer is not null)
            _composer.PropertyChanged -= OnComposerPropertyChanged;
        _composer = null;
        if (_viewModel is null)
            return;
        _viewModel.ScrollRequested -= ScrollToMessage;
        _viewModel.MessagesChanged -= QueueScrollToEnd;
        _viewModel = null;
    }

    /** 输入框文本或光标变化都回填给视图模型: 补全菜单要按光标所在 token 决定是否弹出。 */
    private void OnInputBoxPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs args)
    {
        if (args.Property != TextBox.TextProperty && args.Property != TextBox.CaretIndexProperty)
            return;
        SyncCaretFromView();
    }

    private void SyncCaretFromView()
    {
        if (_syncingCaret || _composer is null)
            return;
        _syncingCaret = true;
        try
        {
            _composer.CaretIndex = Math.Clamp(InputBox.CaretIndex, 0, (InputBox.Text ?? "").Length);
        }
        finally
        {
            _syncingCaret = false;
        }
    }

    /** 视图模型改写文本后(如选中引用候选)由它决定光标落点, 这里把光标搬过去。 */
    private void OnComposerPropertyChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (_syncingCaret || _composer is null || args.PropertyName != nameof(ComposerViewModel.CaretIndex))
            return;
        _syncingCaret = true;
        try
        {
            InputBox.CaretIndex = Math.Clamp(_composer.CaretIndex, 0, (InputBox.Text ?? "").Length);
        }
        finally
        {
            _syncingCaret = false;
        }
    }

    private RoutedEventArgs? _scrollInput;

    /** 滚轮落在消息里的内层滚动区域(工具长结果、diff 卡片)时, 由内层消费, 不再带动整个消息列表。 */
    private void OnNestedWheel(object? sender, PointerWheelEventArgs e)
    {
        if (e.Handled || MessageList.Scroll is not ScrollViewer outer)
            return;
        var inner = InnermostScrollViewer(e.Source as Visual, outer);
        if (inner is null)
            return;
        var maximum = Math.Max(0, inner.Extent.Height - inner.Viewport.Height);
        if (maximum <= 0)
            return;
        var step = e.Delta.Y * NestedWheelStepPixels;
        var target = Math.Clamp(inner.Offset.Y - step, 0, maximum);
        if (Math.Abs(target - inner.Offset.Y) < 1)
            return;
        inner.Offset = new Vector(inner.Offset.X, target);
        e.Handled = true;
    }

    /** 从事件源向上找第一个滚动视口; 命中消息列表本身说明没有内层, 交给默认行为。 */
    private static ScrollViewer? InnermostScrollViewer(Visual? source, ScrollViewer outer)
    {
        for (var visual = source; visual is not null; visual = visual.GetVisualParent())
        {
            if (visual is not ScrollViewer scroll)
                continue;
            return ReferenceEquals(scroll, outer) ? null : scroll;
        }
        return null;
    }

    /** 用户输入产生的位置变化才改判跟随意图; 程序/布局引起的位置变化不动它。 */
    private void OnScrollInput(object? sender, RoutedEventArgs e) =>
        _scrollInput = e.Route == RoutingStrategies.Tunnel ? e : null;

    /** 滚轮/键盘/拖动滚动条都属于用户自己在移动视口; 只读文本里的指针事件不算。 */
    private bool IsUserScrollInput(RoutedEventArgs e)
        => e is PointerWheelEventArgs
            || e is KeyEventArgs { Key: Key.Up or Key.Down or Key.PageUp or Key.PageDown or Key.Home or Key.End }
            || e is PointerEventArgs pointer && IsScrollBarInput(pointer.Source as Visual);

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
        if (_scrollInput is not { } input || !IsUserScrollInput(input))
            return;
        // 用户重新滚动时放弃折叠对齐, 不与其滚动意图抢位置。
        ClearAnchor();
        _sticky = scroll.Offset.Y + scroll.Viewport.Height >= scroll.Extent.Height - StickyTolerance;
    }

    /**
     * 跟随中随内容增长贴合末尾; 用户停在中途时一帧都不动, 阅读位置因此保持稳定。
     *
     * 硬规则: 视口本来就不在底部时, 任何情况下都不许自动贴底。判据是"这次布局变化之前的位置",
     * 因为内容一增长, 当前位置立刻就不算底部了 —— 用增长前的 extent(e.Extent.Height - ExtentDelta.Y)判断。
     * 在这一条之上, 跟随还要同时满足: 意图仍为跟随、没有折叠对齐在飞、没在拖滚动条、用户刚滚动过的静默窗口已过。
     *
     * 意图只在"视口自己动了"时重新判定(用户滚动、程序性跳转、我们自己的贴底)。
     * 内容高度变化(流式输出、虚拟化重估、折叠)一律不改判: 否则每一帧的 extent 抖动都会把跟随
     * 自己打开又关掉, 表现为滚动条高频小幅乱跳、拖到底又弹回上面、点折叠没反应。
     *
     * 用户刚用滚轮/滚动条动过视口时, 即使此刻判定为"贴底"也要让路一帧以上的静默窗口:
     * "消息往下刷 + 滚轮往上走"落在同一帧时, 输入与布局的先后顺序不保证, 不让路就会被拽回底部。
     */
    private void OnScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        if (e.Source is not ScrollViewer scroll || !ReferenceEquals(scroll, MessageList.Scroll))
            return;
        var layoutChanged = e.ExtentDelta != default || e.ViewportDelta != default;
        if (!layoutChanged)
        {
            // 位置变了就作废排队中的贴底请求, 并按落点重判是否还贴底。
            // 程序性跳转(ScrollIntoView / 跳到某条消息)也走这里: 跳到中途就必须停止跟随,
            // 否则一次布局增长就会把人拽回底部。
            _scrollGeneration++;
            _sticky = IsAtBottom(scroll);
        }
        // 拖动滚动条期间一帧都不许自动滚, 否则会和用户的拖动抢位置。
        // wasAtBottom 取上涨之前的快照: 内容一长高, 当前位置立刻就不算底部了。
        var wasAtBottom = _lastScroll.IsAtBottom(StickyTolerance);
        if (layoutChanged && _sticky && wasAtBottom && _anchorList is null && !_draggingThumb)
            scroll.ScrollToEnd();
        _lastScroll = new ScrollSnapshot(scroll.Offset.Y, scroll.Viewport.Height, scroll.Extent.Height);
    }

    /** 视口是否在底部; 自动贴底只允许发生在这里为真的情形。 */
    private static bool IsAtBottom(ScrollViewer scroll)
        => scroll.Offset.Y + scroll.Viewport.Height >= scroll.Extent.Height - StickyTolerance;

    /** SelectableTextBlock 点击后不会自动获得焦点, 而 Ctrl+C 只有在其拥有焦点时才生效; 选中文字即意味着在阅读, 同时停止触底跟随。 */
    private void OnSelectablePointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.Source is not Visual source
            || source.FindAncestorOfType<SelectableTextBlock>(includeSelf: true) is not { } block)
            return;
        if (MessageList.IsEffectivelyVisible)
            _sticky = false;
        block.Focus();
    }

    /** 点击折叠按钮时先记下消息位置; Click 在 Button 执行命令前触发, 记录的是折叠前的布局。 */
    private void OnMessageFoldClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Control control)
            BeginAnchor(control, MessageList, MessageList.Scroll as ScrollViewer);
    }

    private void OnTraceExpandClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Control control)
            BeginAnchor(control, TraceList, TraceList.Scroll as ScrollViewer);
    }

    private void OnCardToggleRequested(object? sender, RoutedEventArgs e)
    {
        if (e.Source is Control control)
            BeginAnchor(control, MessageList, MessageList.Scroll as ScrollViewer);
    }

    /** 记下被折叠消息在视口中的位置; 列表虚拟化后容器会被回收, 因此按数据项而不仅是容器来跟踪。 */
    private void BeginAnchor(Control control, ListBox list, ScrollViewer? scroll)
    {
        var item = control.FindAncestorOfType<ListBoxItem>() ?? control;
        if (scroll is null || item.TranslatePoint(default, scroll) is not { } top)
            return;
        if (_anchorList is null)
            LayoutUpdated += OnLayoutUpdated;
        _anchorList = list;
        _anchorScroll = scroll;
        _anchorData = item.DataContext;
        _anchorTop = top.Y;
        _anchorPasses = 0;
        _anchorRealizePosted = false;
    }

    private void OnLayoutUpdated(object? sender, EventArgs e)
    {
        if (_anchorList is null || _anchorScroll is null || _anchorData is null)
            return;
        var list = _anchorList;
        var scroll = _anchorScroll;
        var data = _anchorData;
        if (FindRealizedItem(list, data) is { } item)
        {
            if (AlignAnchor(item, scroll))
                FinishAnchor(scroll);
            return;
        }
        if (_anchorRealizePosted)
            return;
        if (++_anchorPasses > MaxAnchorPasses)
        {
            FinishAnchor(scroll);
            return;
        }
        _anchorRealizePosted = true;
        // 列表自带的滚动锚定会把项移出视口并回收容器; 先让目标项重新实体化, 下一轮布局再按原位置对齐。
        Dispatcher.UIThread.Post(() =>
        {
            _anchorRealizePosted = false;
            if (ReferenceEquals(_anchorList, list) && ReferenceEquals(_anchorData, data))
                list.ScrollIntoView(data);
        }, DispatcherPriority.Loaded);
    }

    /** 返回 true 表示已经对齐或已到滚动边界, 无需再等下一轮布局。 */
    private bool AlignAnchor(Control item, ScrollViewer scroll)
    {
        if (item.TranslatePoint(default, scroll) is not { } top)
            return true;
        var delta = top.Y - _anchorTop;
        if (Math.Abs(delta) <= AnchorTolerance)
            return true;
        if (++_anchorPasses > MaxAnchorPasses)
            return true;
        var maximum = Math.Max(0, scroll.Extent.Height - scroll.Viewport.Height);
        var target = Math.Clamp(scroll.Offset.Y + delta, 0, maximum);
        // 到达滚动边界时无法继续对齐, 立即结束而不是反复重试。
        if (Math.Abs(target - scroll.Offset.Y) <= AnchorTolerance)
            return true;
        scroll.Offset = new Vector(scroll.Offset.X, target);
        return false;
    }

    private static ListBoxItem? FindRealizedItem(ListBox list, object data)
        => list.GetVisualDescendants().OfType<ListBoxItem>()
            .FirstOrDefault(item => ReferenceEquals(item.DataContext, data));

    private void FinishAnchor(ScrollViewer scroll)
    {
        var messageList = ReferenceEquals(_anchorList, MessageList);
        ClearAnchor();
        if (messageList)
            _sticky = scroll.Offset.Y + scroll.Viewport.Height >= scroll.Extent.Height - StickyTolerance;
    }

    private void ClearAnchor()
    {
        if (_anchorList is not null)
            LayoutUpdated -= OnLayoutUpdated;
        _anchorList = null;
        _anchorScroll = null;
        _anchorData = null;
        _anchorPasses = 0;
        _anchorRealizePosted = false;
    }

    private void ScrollToMessage(MessageViewModel message)
    {
        if (_viewModel is null)
            return;
        if (!_viewModel.Messages.Contains(message))
            return;
        ClearAnchor();
        _sticky = true;
        Dispatcher.UIThread.Post(() => MessageList.ScrollIntoView(message));
    }

    private void QueueScrollToEnd()
    {
        if (_scrollEndPosted || !_sticky || _viewModel?.Messages.Count is not > 0)
            return;
        _scrollEndPosted = true;
        var generation = _scrollGeneration;
        Dispatcher.UIThread.Post(() =>
        {
            _scrollEndPosted = false;
            // 位置判断放在真正执行时: 视口不在底部就绝不贴底, 不管是谁排的队。
            if (generation == _scrollGeneration && _sticky && _viewModel?.Messages.Count is > 0
                && MessageList.Scroll is ScrollViewer live && IsAtBottom(live))
                live.ScrollToEnd();
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


    private void OnSubagentFlyoutOpened(object? sender, EventArgs e)
        => _subagentFlyout = sender as Flyout;

    // Execute before hiding the flyout because detached buttons lose their command bindings.
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

    /** 一次滚动事件时的视口状态; 初始为"在底部"(空列表没有可滚内容)。 */
    private readonly record struct ScrollSnapshot(double Offset, double Viewport, double Extent)
    {
        public bool IsAtBottom(double tolerance) => Offset + Viewport >= Extent - tolerance;
    }
}
