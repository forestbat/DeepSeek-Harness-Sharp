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
    /** 位置上的容差: 小于它就算没动(与 SubtitleEdit 的 ScrollAnchor 一致)。 */
    private const double AnchorTolerance = 0.5;
    /** 布局变化顺带把视口推了多少像素以内算噪声(不是滚动)。 */
    private const double SubPixelDrift = 6;
    /** 连续这么多轮都在原位, 才认为还原已经落定。 */
    /** ApproachIndex 走位最多几轮(取自 SubtitleEdit 的 PrePositionScroll)。 */
    private const int ApproachPasses = 3;
    /** 一次还原里最多修几轮。 */
    private const int AnchorSettlePasses = 3;

    /** 一格滚轮在内层滚动区域里滚动的像素数(与常见 3 行文本高度相当)。 */
    private const double NestedWheelStepPixels = 60;

    private const RoutingStrategies ScrollInputRoutes = RoutingStrategies.Tunnel | RoutingStrategies.Bubble;

    private MainViewModel? _viewModel;
    private ComposerViewModel? _composer;
    private bool _syncingCaret;
    private bool _sticky = true;
    private bool _scrollEndPosted;
    private long _scrollGeneration;
    private bool _draggingThumb;
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
        AddHandler(InputElement.PointerPressedEvent, OnSelectablePointerPressed, RoutingStrategies.Tunnel, handledEventsToo: true);
        LayoutUpdated += OnLayoutUpdatedGuard;
        MessageList.AddHandler(DiffView.CardToggleRequestedEvent, OnCardToggleRequested, RoutingStrategies.Bubble);
        // 时间线入口: 右键消息就是"跳到这里 / 回退到这里 / 从这里分叉"。
        MessageList.AddHandler(ContextRequestedEvent, OnMessageContextRequested, RoutingStrategies.Tunnel);
        MessageList.PropertyChanged += (_, change) =>
        {
            if (change.Property != ListBox.ScrollProperty)
                return;
            if (change.OldValue is ScrollViewer previous)
                previous.PropertyChanged -= OnScrollPropertyChanged;
            if (change.NewValue is ScrollViewer current)
            {
                current.PropertyChanged += OnScrollPropertyChanged;
                // 点击消息会让列表项获得焦点; 默认的"聚焦即滚到可见"会把比视口还高的整条消息顶到视口顶端。
                current.BringIntoViewOnFocusChange = false;
            }
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
        _sticky = scroll.Offset.Y + scroll.Viewport.Height >= scroll.Extent.Height - StickyTolerance;
    }

    /**
     * 跟随中随内容增长贴合末尾; 用户停在中途时一帧都不动, 阅读位置因此保持稳定。
     *
     * 硬规则: 视口本来就不在底部时, 任何情况下都不许自动贴底。判据是"这次布局变化之前的位置快照"(_lastScroll),
     * 因为内容一增长, 当前位置立刻就不算底部了。
     * 在这一条之上, 跟随还要同时满足: 意图仍为跟随、没有折叠对齐在飞、没在拖滚动条。
     *
     * 意图只在"视口自己动了"时重新判定(用户滚动、程序性跳转、我们自己的贴底)。
     * 内容高度变化(流式输出、虚拟化重估、折叠)一律不改判: 否则每一帧的 extent 抖动都会把跟随
     * 自己打开又关掉, 表现为滚动条高频小幅乱跳、拖到底又弹回上面、点折叠没反应。
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
        // SubtitleEdit 的判据是"高度变了、位置没变"。这里把"位置"放宽到 SubPixelDrift: 我们自己的修正
        // 会让面板再估一次并再漂几像素, 用 0.5px 的严格判据会把这类后续漂移漏掉, 累积成可见位移。
        if (!_restoringAnchor && !_draggingThumb && !_sticky
            && Math.Abs(e.ExtentDelta.Y) > AnchorTolerance
            && Math.Abs(e.OffsetDelta.Y) <= SubPixelDrift
            && scroll.Offset.Y > AnchorTolerance)
        {
            RestoreAnchor(scroll);
        }
        // 没有折叠在还原时: 布局变化顺手把视口推了几像素是面板重估 + 布局取整的噪声, 不是滚动, 原样退回。
        // 这两条必须互斥 —— 否则上一行的锚点修正会被这一行按旧 offset 撤销, 流式期间就一直漂。
        else if (layoutChanged && !_sticky && !_draggingThumb
            && Math.Abs(e.OffsetDelta.Y) > 0 && Math.Abs(e.OffsetDelta.Y) < SubPixelDrift)
        {
            scroll.Offset = new Vector(scroll.Offset.X, _lastScroll.Offset);
        }
        RememberAnchor(scroll);
        // 面板重估偶尔会把实体化窗口整个丢到视口之外(实测: 视口里一条都没有, 已实体化项的 top 全是 -1100 之类),
        // 这时按锚点把它找回来。只在滚动事件里做一次, 不在布局回调里做, 不会递归。
        if (!_restoringAnchor && !HasVisibleItem(scroll) && LastRealizedIndex() is var blank && blank >= 0)
        {
            _restoringAnchor = true;
            try
            {
                ApproachIndex(scroll, blank);
                MessageList.ScrollIntoView(blank);
                MessageList.UpdateLayout();
                RememberAnchor(scroll);
            }
            finally
            {
                _restoringAnchor = false;
            }
        }
        ScheduleBlankRecovery();

        var wasAtBottom = _lastScroll.IsAtBottom(StickyTolerance);
        if (layoutChanged && _sticky && wasAtBottom && !_draggingThumb && !_foldPending && !_restoringAnchor)
            StickToEnd(scroll);
        if (layoutChanged)
            _foldPending = false;
        _lastScroll = new ScrollSnapshot(scroll.Offset.Y, scroll.Viewport.Height, scroll.Extent.Height);
        // 一个滚动周期结束: 下个周期没有新的输入事件就说明这次的位移来自布局, 不是用户。
        _scrollInput = null;
    }

    /**
     * 折叠/展开会改变某条消息的高度。列表按 VirtualizingStackPanel 摆放时, 任何一条已实体化项高度变了,
     * 面板就把区间起点判为不可信, 改用"实体化项平均高度"重估整篇文档: 像素偏移不变, 但同一偏移下的
     * 内容换成了更早/更晚的一条, 表现为视口上下乱跳(上游 AvaloniaUI/Avalonia#17831)。
     *
     * 还原办法照抄 SubtitleEdit 的 TableViewScrollAnchor 与 TableViewExtras.PrePositionScroll
     * (https://github.com/SubtitleEdit/subtitleedit/pull/13625, 后续 #14626 补上 extent 缩短的情形):
     * **锚点永远是"视口顶部那条消息"**, 每次滚动之后重新记; 只要发现"高度变了、位置没变"
     * (说明面板在视口下面重估, 不是滚动), 就把它放回原处。像素正是不可靠的那个量, 所以还原按数据项做;
     * 目标被回收时先用实测行高靠近(ApproachIndex)再 ScrollIntoView。
     */
    private void RememberAnchor(ScrollViewer scroll)
    {
        var top = double.MaxValue;
        ListBoxItem? anchor = null;
        foreach (var item in MessageList.GetVisualDescendants().OfType<ListBoxItem>())
        {
            var index = MessageList.IndexFromContainer(item);
            if (index < 0 || item.Bounds.Height <= 0 || !ReferenceEquals(MessageList.ContainerFromIndex(index), item))
                continue;
            if (item.TranslatePoint(default, scroll) is not { } position)
                continue;
            if (position.Y + item.Bounds.Height <= AnchorTolerance || position.Y >= top)
                continue;
            top = position.Y;
            anchor = item;
        }
        if (anchor?.DataContext is not { } data)
        {
            _anchorData = null;
            _anchorIndex = -1;
            _anchorCount = -1;
            return;
        }
        _anchorData = data;
        _anchorIndex = MessageList.IndexFromContainer(anchor);
        _anchorCount = MessageList.ItemCount;
        _anchorTop = top;
    }


    /** 数据项优先于下标: 上面插了一条消息时, 视口里留住的仍是原来那条内容。 */
    private int ResolveAnchorIndex()
    {
        if (MessageList.ItemCount == 0)
            return -1;
        if (_anchorData is { } data)
        {
            for (var index = 0; index < MessageList.ItemCount; index++)
            {
                if (ReferenceEquals(MessageList.Items[index], data))
                    return index;
            }
        }
        return _anchorIndex >= 0 && _anchorIndex < MessageList.ItemCount && MessageList.ItemCount == _anchorCount
            ? _anchorIndex
            : -1;
    }

    /**
     * 视口空掉只在"布局把实体化窗口丢到视口外"时出现, 可能发生在没有任何滚动事件的那一轮布局里,
     * 所以挂一个只检测、只投递的布局回调: 检测到空视口就把修复投递出去(最多一次), 绝不在布局回调里写 offset。
     */
    private void ScheduleBlankRecovery()
    {
        if (_blankRecoveryPosted)
            return;
        _blankRecoveryPosted = true;
        Dispatcher.UIThread.Post(() =>
        {
            _blankRecoveryPosted = false;
            if (_restoringAnchor || MessageList.Scroll is not ScrollViewer scroll || HasVisibleItem(scroll))
                return;
            if (LastRealizedIndex() is var index && index >= 0)
            {
                ApproachIndex(scroll, index);
                MessageList.ScrollIntoView(index);
            }
        }, DispatcherPriority.Background);
    }

    /** 最后一个已实体化项的下标; 视口空掉时用它把内容找回来。 */
    private int LastRealizedIndex()
    {
        var last = -1;
        foreach (var item in MessageList.GetVisualDescendants().OfType<ListBoxItem>())
        {
            var index = MessageList.IndexFromContainer(item);
            if (index > last)
                last = index;
        }
        return last;
    }

    /** 视口里是否还有消息(实体化项的屏幕范围与视口相交)。 */
    private bool HasVisibleItem(ScrollViewer scroll)
    {
        foreach (var item in MessageList.GetVisualDescendants().OfType<ListBoxItem>())
        {
            if (item.Bounds.Height <= 0 || item.TranslatePoint(default, scroll) is not { } position)
                continue;
            if (position.Y < scroll.Viewport.Height && position.Y + item.Bounds.Height > 0)
                return true;
        }
        return false;
    }

    private void RestoreAnchor(ScrollViewer scroll)
    {
        var index = ResolveAnchorIndex();
        if (index < 0)
            return;
        _restoringAnchor = true;
        try
        {
            // 面板重估通常把锚点挤出了实体化范围: 先按实测行高把偏移量挪过去, 再请列表实体化它。
            if (MessageList.ContainerFromIndex(index) is not { Bounds.Height: > 0 })
            {
                ApproachIndex(scroll, index);
                MessageList.ScrollIntoView(index);
                MessageList.UpdateLayout();
            }
            for (var pass = 0; pass < AnchorSettlePasses; pass++)
            {
                if (MessageList.ContainerFromIndex(index) is not { Bounds.Height: > 0 } item
                    || item.TranslatePoint(default, scroll) is not { } position)
                    break;
                var delta = position.Y - _anchorTop;
                if (Math.Abs(delta) <= AnchorTolerance)
                    break;
                // 偏出一屏说明这个锚点已经过期(内容长高/用户滚过): 重新记, 不要把视口拽过去。
                if (Math.Abs(delta) > scroll.Viewport.Height)
                {
                    RememberAnchor(scroll);
                    break;
                }
                var maximum = Math.Max(0, scroll.Extent.Height - scroll.Viewport.Height);
                scroll.Offset = new Vector(scroll.Offset.X, Math.Clamp(scroll.Offset.Y + delta, 0, maximum));
                MessageList.UpdateLayout();
            }
            RememberAnchor(scroll);
        }
        finally
        {
            _restoringAnchor = false;
        }
    }

    /**
     * 先把偏移量挪到目标附近, 免得 ScrollIntoView 按估算一段段走过去。
     * 同样取自 SubtitleEdit 的 TableViewExtras.PrePositionScroll: 用视口里实测的行高走位, 最多三轮。
     */
    private void ApproachIndex(ScrollViewer scroll, int index)
    {
        if (scroll.Viewport.Height <= 0 || index == 0)
            return;
        for (var pass = 0; pass < ApproachPasses; pass++)
        {
            var items = MessageList.GetVisualDescendants().OfType<ListBoxItem>()
                .Select(item => (Index: MessageList.IndexFromContainer(item), Item: item))
                .Where(entry => entry.Index >= 0 && entry.Item.Bounds.Height > 0)
                .OrderBy(entry => entry.Index)
                .ToList();
            if (items.Count == 0 || items.Exists(entry => entry.Index == index))
                return;
            if (items[0].Item.TranslatePoint(default, scroll) is not { } first)
                return;
            var average = items.Average(entry => entry.Item.Bounds.Height);
            var delta = ((index - items[0].Index) * average) - first.Y;
            if (Math.Abs(delta) < scroll.Viewport.Height / 2)
                return;
            var maximum = Math.Max(0, scroll.Extent.Height - scroll.Viewport.Height);
            var target = Math.Clamp(scroll.Offset.Y + delta, 0, maximum);
            if (Math.Abs(target - scroll.Offset.Y) < 1)
                return;
            scroll.Offset = new Vector(scroll.Offset.X, target);
            MessageList.UpdateLayout();
        }
    }

    /**
     * 贴底写"当前最大值"这个具体数值。
     * ScrollViewer.ScrollToEnd 存的是超限的本地值, 内容一长高, 钳制放开, 视口会自己多滚一截,
     * 表现为"折叠/流式之后莫名往下跳"。
     */
    private static void StickToEnd(ScrollViewer scroll)
    {
        var maximum = Math.Max(0, scroll.Extent.Height - scroll.Viewport.Height);
        scroll.Offset = new Vector(scroll.Offset.X, maximum);
    }

    /** 视口是否在底部; 自动贴底只允许发生在这里为真的情形。 */
    private static bool IsAtBottom(ScrollViewer scroll)
        => scroll.Offset.Y + scroll.Viewport.Height >= scroll.Extent.Height - StickyTolerance;

    /** 这一次布局变化来自折叠/展开: 不许顺手贴底, 否则用户刚点开的那条会被顶出视口。 */
    private bool _foldPending;


    /** 视口顶部那条消息(数据项 + 下标 + 当时的条目总数)和它在视口里的位置。 */
    private object? _anchorData;
    private int _anchorIndex = -1;
    private int _anchorCount = -1;
    private double _anchorTop;

    /** 正在还原锚点; 期间产生的滚动事件不再参与判断。 */
    private bool _restoringAnchor;

    /** 已经投递过"空视口修复", 防止重复排队。 */
    private bool _blankRecoveryPosted;

    /** 每轮布局后检查视口是否空掉: 只检测、只投递, 绝不在布局回调里写 offset。 */
    private void OnLayoutUpdatedGuard(object? sender, EventArgs e) => ScheduleBlankRecovery();

    /** 折叠与展开只是视图操作: 位置不需要补偿(上方内容不动), 但要挡掉这一次的触底跟随。 */
    private void OnFoldClick(object? sender, RoutedEventArgs e) => _foldPending = true;

    private void OnCardToggleRequested(object? sender, RoutedEventArgs e) => _foldPending = true;

    /**
     * 右键一条消息: 只在它有事件序号时给出时间线动作(合成的界面行没有序号, 无从回退)。
     * 菜单在代码里建:  Flyout 的内容不参与模板绑定, XAML 里无法把消息当参数递给命令。
     */
    private void OnMessageContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        if (_viewModel is not { } viewModel
            || MessageContainerOf(e.Source)?.DataContext is not MessageViewModel { HasTimelineEntry: true } message)
            return;
        e.Handled = true;
        MenuFlyout menu = new();
        menu.Items.Add(MenuItem("跳到此消息", () => viewModel.JumpToMessageCommand.Execute(message)));
        menu.Items.Add(MenuItem("回退到此消息", () =>
        {
            if (viewModel.RevertToMessageCommand.CanExecute(message))
                _ = ExecuteViewCommandAsync(viewModel, viewModel.RevertToMessageCommand, message);
        }));
        menu.Items.Add(MenuItem("从此消息分叉", () =>
        {
            if (viewModel.ForkAtMessageCommand.CanExecute(message))
                _ = ExecuteViewCommandAsync(viewModel, viewModel.ForkAtMessageCommand, message);
        }));
        menu.ShowAt(MessageContainerOf(e.Source)!, showAtPointer: true);
    }

    private static MenuItem MenuItem(string header, Action action)
    {
        MenuItem item = new() { Header = header };
        item.Click += (_, _) => action();
        return item;
    }

    private ListBoxItem? MessageContainerOf(object? source)
    {
        for (var visual = source as Visual; visual is not null; visual = visual.GetVisualParent())
        {
            if (visual is ListBoxItem item)
                return item;
        }
        return null;
    }

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

    private void ScrollToMessage(MessageViewModel message)
    {
        if (_viewModel is null)
            return;
        if (!_viewModel.Messages.Contains(message))
            return;
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
                StickToEnd(live);
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
        if (HandlesTranscriptKey(e))
            return;
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

    /**
     * 输入框在默认状态下有焦点, 而 TextBox 自己会吞掉 PageUp/PageDown/Home/End,
     * 所以在隧道里先拦一道: 翻页键交给消息列表, Home/End 在输入框为空时跳到记录头尾。
     * 输入框写得比 MaxHeight 高、自己需要滚动时, 这些键仍归 TextBox。
     */
    private bool HandlesTranscriptKey(KeyEventArgs e)
    {
        if (e.KeyModifiers is not (KeyModifiers.None or KeyModifiers.Control))
            return false;
        var scroll = MessageList.Scroll;
        if (scroll is null)
            return false;
        var composerScrolls = InputBox.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault() is { } composer
            && composer.Extent.Height - composer.Viewport.Height > AnchorTolerance;
        var jumpToStart = e.Key == Key.Home && (e.KeyModifiers == KeyModifiers.Control || InputBox.Text is not { Length: > 0 });
        var jumpToEnd = e.Key == Key.End && (e.KeyModifiers == KeyModifiers.Control || InputBox.Text is not { Length: > 0 });
        switch (e.Key)
        {
            case Key.PageUp when !composerScrolls:
                ScrollTranscriptBy(-scroll.Viewport.Height, follow: false);
                break;
            case Key.PageDown when !composerScrolls:
                ScrollTranscriptBy(scroll.Viewport.Height, follow: true);
                break;
            case Key.Home or Key.End when jumpToStart || jumpToEnd:
                ScrollTranscriptTo(jumpToEnd ? double.MaxValue : 0);
                break;
            default:
                return false;
        }
        e.Handled = true;
        return true;
    }

    /** 写偏移用具体像素(禁用 ScrollToEnd): 到底时按"这次落点是否在末尾"重定跟随意图。 */
    private void ScrollTranscriptBy(double delta, bool follow)
        => ScrollTranscriptTo((MessageList.Scroll?.Offset.Y ?? 0) + delta, follow);

    private void ScrollTranscriptTo(double target, bool? follow = null)
    {
        if (MessageList.Scroll is not { } scroll)
            return;
        var maxOffset = Math.Max(0, scroll.Extent.Height - scroll.Viewport.Height);
        var offset = Math.Clamp(target, 0, maxOffset);
        scroll.Offset = new Vector(scroll.Offset.X, offset);
        _sticky = follow ?? offset >= maxOffset - AnchorTolerance;
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
