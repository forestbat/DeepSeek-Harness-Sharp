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

    /** 跳到某条消息时它离视口顶端的距离。 */
    private const double TranscriptTopPadding = 8;

    /** 已打开的预览在轨道/面板边界外再多留几像素: 指针横穿那条缝时不能把它抖没。 */
    private const double TimelineKeepReach = 4;

    /** 折叠锚点最多生效几次布局变化: 只够它自己那一次重估, 之后的流式增长不许再动视口。 */
    private const int FoldAnchorPasses = 2;

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
    private int _timelineHovered = -1;
    private bool _previewScrolling;

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
        // 时间线入口: 刻度点击在轨道上做(见 WireTimeline), 右键只留"回退/分叉"这类必须落到消息上的动作。
        MessageList.AddHandler(ContextRequestedEvent, OnMessageContextRequested, RoutingStrategies.Tunnel);
        WireTimeline();
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

    /** 轨道悬停出预览、点击跳转; 预览面板自己可滚动, 鼠标移到面板上时保持打开。 */
    private void WireTimeline()
    {
        Rail.Transcript = MessageList;
        Rail.HoverChanged += ShowTimelinePreview;
        Rail.Activated += index =>
        {
            var marks = Rail.Marks;
            if (index < 0 || index >= marks.Count)
                return;
            HideTimelinePreview();
            _viewModel?.JumpToMessageCommand.Execute(marks[index].Message);
        };
        // 悬停/关闭都在 ChatView 上判定: 指针从刻度挪到预览面板要跨过轨道左边的空白,
        // 只靠"离开控件就关"会把面板在用户手上抽走。
        AddHandler(PointerMovedEvent, OnTimelinePointerMoved, RoutingStrategies.Tunnel, handledEventsToo: true);
        TimelinePreviewList.AddHandler(PointerPressedEvent, OnTimelinePreviewPressed, RoutingStrategies.Tunnel);
        // 拖预览自己的滚动条时指针可能被甩到面板外, 这段时间一律不关。
        TimelinePreview.AddHandler(Thumb.DragStartedEvent, (_, _) => _previewScrolling = true,
            RoutingStrategies.Bubble, handledEventsToo: true);
        TimelinePreview.AddHandler(Thumb.DragCompletedEvent, (_, _) => _previewScrolling = false,
            RoutingStrategies.Bubble, handledEventsToo: true);
    }

    /**
     * 指针移动时统一决定预览去留。
     *
     * 两套判定必须分开, 混用会二选一地踩到两个坑:
     * - **打开/切换**只认轨道列的真实边界 + 刻度容差: 外扩会把右侧滚动条一起吞掉, 想拖滚动条却点出了预览。
     * - **保持**用轨道列与预览面板的并集(各外扩几个像素): 轨道与面板之间哪怕只有一两像素的缝,
     *   指针横穿过去的那一帧既不在轨道也不在面板里, 面板就会在用户手上消失。
     */
    private void OnTimelinePointerMoved(object? sender, PointerEventArgs e)
    {
        if (!Rail.IsEffectivelyVisible)
        {
            HideTimelinePreview();
            return;
        }
        if (_previewScrolling)
            return;
        var overRail = IsWithin(Rail, e, 0);
        if (TimelinePreview.IsVisible
            && (overRail || IsWithin(Rail, e, TimelineKeepReach) || IsWithin(TimelinePreview, e, TimelineKeepReach)))
        {
            if (overRail)
                HoverTickAt(e.GetPosition(Rail).Y);
            return;
        }
        if (!overRail)
        {
            HideTimelinePreview();
            return;
        }
        HoverTickAt(e.GetPosition(Rail).Y);
    }

    private void HoverTickAt(double y)
    {
        var index = Rail.MarkAt(y);
        if (index < 0 || index == _timelineHovered)
            return;
        _timelineHovered = index;
        Rail.SetHovered(index);
        ShowTimelinePreview(index);
    }

    /** 指针是否落在某个控件的矩形里(向外扩 reach 像素)。 */
    private static bool IsWithin(Visual target, PointerEventArgs e, double reach)
        => new Rect(target.Bounds.Size).Inflate(reach).Contains(e.GetPosition(target));

    private void ShowTimelinePreview(int index)
    {
        if (!Rail.IsEffectivelyVisible)
            return;
        var marks = Rail.Marks;
        if (index < 0 || index >= marks.Count)
        {
            HideTimelinePreview();
            return;
        }
        TimelinePreview.IsVisible = true;
        FillPreviewItems();
        PlaceTimelinePreview(index);
        CenterTimelinePreview(marks[index].Message);
    }

    /**
     * 预览只列用户消息(时间线的候选), 不列工具/思考这类中间状态:
     * 面板是刻度的放大镜, 列了别的就等于把轨道变成了另一份转录。
     */
    private void FillPreviewItems()
    {
        if (_viewModel is not { } viewModel)
            return;
        var userMessages = viewModel.Messages.Where(message => message.Kind == MessageKind.User).ToList();
        if (TimelinePreviewList.ItemsSource is List<MessageViewModel> current && current.SequenceEqual(userMessages))
            return;
        TimelinePreviewList.ItemsSource = userMessages;
    }

    /** 预览停在指针所指那条的前后几条上: 先让它可见, 再把它的位置挪到面板中间。 */
    private void CenterTimelinePreview(MessageViewModel message)
    {
        // 面板刚显示出来还没应用模板, 连 Scroll 都还是 null: 等这一次指针事件走完再定位。
        Dispatcher.UIThread.Post(() =>
        {
            var index = TimelinePreviewList.Items.IndexOf(message);
            if (index < 0)
                return;
            TimelinePreviewList.UpdateLayout();
            if (TimelinePreviewList.Scroll is ScrollViewer scroll)
                RevealIndex(TimelinePreviewList, scroll, index, double.NaN);
        }, DispatcherPriority.Background);
    }

    /** 预览面板贴着指针所在的那条刻度: 让"从刻度往左移到面板"是一条水平路径,
     * 面板也就不必固定居中而在上下两头留出够不着的缝。面板右缘与轨道左缘严格相接(不留缝)。 */
    private void PlaceTimelinePreview(int index)
    {
        if (TimelinePreview.Parent is not Visual parent)
            return;
        var tickY = Rail.TranslatePoint(new Point(0, Rail.OffsetOf(index)), parent)?.Y;
        if (tickY is not { } centerY)
            return;
        var height = TimelinePreview.Bounds.Height > 0 ? TimelinePreview.Bounds.Height : TimelinePreview.MaxHeight;
        var available = parent.Bounds.Height;
        var top = Math.Clamp(centerY - height / 2, 0, Math.Max(0, available - height));
        var gap = Rail.Margin;
        TimelinePreview.Margin = new Thickness(0, top, gap.Left + Rail.Bounds.Width + gap.Right, 0);
    }

    private void HideTimelinePreview()
    {
        _timelineHovered = -1;
        Rail.SetHovered(-1);
        TimelinePreview.IsVisible = false;
    }

    private void OnTimelinePreviewPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.Source is not Visual source
            || source.GetVisualAncestors().OfType<ListBoxItem>().FirstOrDefault()?.DataContext is not MessageViewModel message)
            return;
        HideTimelinePreview();
        _viewModel?.JumpToMessageCommand.Execute(message);
        e.Handled = true;
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
    private void OnScrollInput(object? sender, RoutedEventArgs e)
    {
        _scrollInput = e.Route == RoutingStrategies.Tunnel ? e : null;
        // 用户自己一动视口, 折叠锚点立刻作废: 它只负责"点开的那一下别让卡片乱跳"。
        if (_scrollInput is not null && IsUserScrollInput(e))
            _foldAnchorPasses = 0;
    }

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
        // 折叠锚点优先: 面板这次重估能把被点那条甩出几百像素, 而下面的常规判据要求偏移量几乎没变,
        // 那种情况下它不会出手。
        // 触发条件不能只看"内容高度变了": 展开一条很长的消息时, 面板会把它自己甩出实体化窗口,
        // 这一趟可能只报 offset 变化而 extent 尚未更新(实测 offset 17481→14199 却 layoutChanged=false),
        // 于是锚点一次都不出手。锚点等待期间只要视口动了就还原, 是否真需要由还原自己判断(位置够近就不动)。
        if (_foldAnchorPasses > 0 && !_draggingThumb && !_restoringAnchor)
        {
            QueueFoldRestore(scroll);
        }
        // SubtitleEdit 的判据是"高度变了、位置没变"。这里把"位置"放宽到 SubPixelDrift: 我们自己的修正
        // 会让面板再估一次并再漂几像素, 用 0.5px 的严格判据会把这类后续漂移漏掉, 累积成可见位移。
        else if (!_restoringAnchor && !_draggingThumb && !_sticky
            && Math.Abs(e.ExtentDelta.Y) > AnchorTolerance
            && (Math.Abs(e.OffsetDelta.Y) <= SubPixelDrift || CoercedToEnd(scroll, e))
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
                ApproachIndex(MessageList, scroll, blank);
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
        UpdateJumpToLatest();
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
                ApproachIndex(MessageList, scroll, index);
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
        => RestoreAnchor(scroll, ResolveAnchorIndex(), _anchorTop);

    /**
     * 按"某条消息 + 它当时的屏幕位置"还原视口。
     * 折叠用被点那条做锚点: 面板重估会把它甩出几百像素, 按"视口顶部那条"还原对它无效。
     * 位移多大都照样拉回: 面板重估一次能甩出一屏以上(实测追加内容后锚点位移 561px),
     * 那不是用户滚动, 按它自己报的坐标重新记锚点等于把读者正在读的位置丢掉。
     */
    private void RestoreAnchor(ScrollViewer scroll, int index, double anchorTop)
    {
        if (index < 0)
            return;
        _restoringAnchor = true;
        try
        {
            // 面板重估通常把锚点挤出了实体化范围: 先按实测行高把偏移量挪过去, 再请列表实体化它。
            // ApproachIndex 一次只走半屏(它怕走过头), 而展开一条长消息能把锚点甩出几屏,
            // 所以要反复"靠近 + ScrollIntoView"直到它真的被实体化 —— 只试一次的话,
            // 被点那条会永久留在区间外(实测: 下标 218, 区间 [225..236]), 用户点开却看不见。
            for (var attempt = 0; attempt < ApproachPasses && MessageList.ContainerFromIndex(index) is not { Bounds.Height: > 0 }; attempt++)
            {
                ApproachIndex(MessageList, scroll, index);
                MessageList.ScrollIntoView(index);
                MessageList.UpdateLayout();
            }
            for (var pass = 0; pass < AnchorSettlePasses; pass++)
            {
                if (MessageList.ContainerFromIndex(index) is not { Bounds.Height: > 0 } item
                    || item.TranslatePoint(default, scroll) is not { } position)
                    break;
                var delta = position.Y - anchorTop;
                if (Math.Abs(delta) <= AnchorTolerance)
                    break;
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
     * 把某一项放到列表视口里的指定高度(desiredTop 为 NaN 表示居中): 先 ScrollIntoView 让它实体化,
     * 再按实际位置修正, 最多几轮。视口尺寸要等排过版才有效, 所以先 UpdateLayout 再判断。
     */
    private static void RevealIndex(ListBox list, ScrollViewer scroll, int index, double desiredTop)
    {
        if (index < 0)
            return;
        list.UpdateLayout();
        list.ScrollIntoView(index);
        list.UpdateLayout();
        for (var pass = 0; pass <= AnchorSettlePasses; pass++)
        {
            if (scroll.Viewport.Height <= 0)
                return;
            if (list.ContainerFromIndex(index) is not { } container
                || container.TranslatePoint(default, scroll) is not { } position)
            {
                // 目标还没实体化: 按视口里实测的行高先走过去(ScrollIntoView 对没实体化的项不生效)。
                ApproachIndex(list, scroll, index);
                continue;
            }
            var top = double.IsNaN(desiredTop) ? (scroll.Viewport.Height - container.Bounds.Height) / 2 : desiredTop;
            var delta = position.Y - top;
            if (Math.Abs(delta) < 1)
                return;
            var maximum = Math.Max(0, scroll.Extent.Height - scroll.Viewport.Height);
            var target = Math.Clamp(scroll.Offset.Y + delta, 0, maximum);
            if (Math.Abs(target - scroll.Offset.Y) < 1)
                return;
            scroll.Offset = new Vector(scroll.Offset.X, target);
            list.UpdateLayout();
        }
    }

    /**
     * 先把偏移量挪到目标附近, 免得 ScrollIntoView 按估算一段段走过去。
     * 同样取自 SubtitleEdit 的 TableViewExtras.PrePositionScroll: 用视口里实测的行高走位, 最多三轮。
     */
    private static void ApproachIndex(ListBox list, ScrollViewer scroll, int index)
    {
        if (scroll.Viewport.Height <= 0)
            return;
        // 第 0 项没法"按实测行高靠近"(它上面没有参照物): 直接把偏移量归零, 否则永远实体化不出来,
        // 表现为"点最上面那个刻度跳不到第一条消息"。
        if (index == 0)
        {
            if (scroll.Offset.Y > 0)
            {
                scroll.Offset = new Vector(scroll.Offset.X, 0);
                list.UpdateLayout();
            }
            return;
        }
        for (var pass = 0; pass < ApproachPasses; pass++)
        {
            var items = list.GetVisualDescendants().OfType<ListBoxItem>()
                .Select(item => (Index: list.IndexFromContainer(item), Item: item))
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
            list.UpdateLayout();
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

    /**
     * "回到最新"按钮只在视口不在底部时出现。
     * 没有自动贴底兜底之后(点折叠会把视口留在原处), 用户需要一个自己回到最新的入口。
     */
    private void UpdateJumpToLatest()
        => JumpToLatest.IsVisible = !_sticky && _viewModel?.Messages.Count is > 0;

    private void OnJumpToLatestClick(object? sender, RoutedEventArgs e)
    {
        if (MessageList.Scroll is not ScrollViewer scroll)
            return;
        _sticky = true;
        StickToEnd(scroll);
        UpdateJumpToLatest();
    }

    /**
     * 这次位移是不是"内容变矮把 offset 强行钳到新最大值"(SubtitleEdit #14626)。
     *
     * extent 缩短到 offset + viewport 以下时, ScrollViewer 会在同一趟里把 offset 直接压到新的最大值,
     * 位移可以和用户滚动一样大(实测单次 10032px)。它跟着 extent 缩短一起来、又正好落在末端,
     * 因此不是滚动, 而是面板重估的后果 —— 不认这一条, 锚点在"内容变矮"时一次都不会出手。
     */
    private static bool CoercedToEnd(ScrollViewer scroll, ScrollChangedEventArgs e)
    {
        if (e.ExtentDelta.Y >= -AnchorTolerance || e.OffsetDelta.Y >= -AnchorTolerance)
            return false;
        var maximum = Math.Max(0, scroll.Extent.Height - scroll.Viewport.Height);
        return Math.Abs(scroll.Offset.Y - maximum) <= AnchorTolerance;
    }

    /** 这一次布局变化来自折叠/展开: 不许顺手贴底, 否则用户刚点开的那条会被顶出视口。 */
    private bool _foldPending;

    /** 折叠锚点: 被点那条(数据项 + 点击时的屏幕位置), 只在紧接着的两次布局里用来把它放回原处。 */
    private object? _foldAnchorData;
    private double _foldAnchorTop;
    private int _foldAnchorPasses;


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

    /**
     * 折叠/展开会改变被点那条的高度, 而面板在已实体化项高度变化时会重估整篇坐标, 一次能甩出几百像素。
     * 所以点击时记下被点那条及其屏幕位置, 接下来几次布局按它还原; 用户一滚(或重新贴底)就作废。
     *
     * 贴底(跟随中)时同样按这条走: 用户点的是"这一条", 就该让它停在原地。
     * 贴底态原来交给"贴住末尾", 结果是它被自身涨高的量直接顶走(实测 -133px), 这正是用户报的跳动。
     * 代价是视口不再是贴底 —— 想回底部请点右下角的"回到最新"。
     */
    private void OnFoldClick(object? sender, RoutedEventArgs e) => BeginFoldAnchor(e.Source);

    private void OnCardToggleRequested(object? sender, RoutedEventArgs e) => BeginFoldAnchor(e.Source);

    private void BeginFoldAnchor(object? source)
    {
        _foldPending = true;
        _foldAnchorPasses = 0;
        if (MessageList.Scroll is not ScrollViewer scroll
            || MessageContainerOf(source)?.DataContext is not { } data)
            return;
        for (var index = 0; index < MessageList.ItemCount; index++)
        {
            if (!ReferenceEquals(MessageList.Items[index], data)
                || MessageList.ContainerFromIndex(index) is not { Bounds.Height: > 0 } item
                || item.TranslatePoint(default, scroll) is not { } position)
                continue;
            _foldAnchorData = data;
            _foldAnchorTop = position.Y;
            _foldAnchorPasses = FoldAnchorPasses;
            // 贴底态下也保住被点那条: 退出跟随, 否则下面"贴住末尾"的分支会立刻把它顶走。
            _sticky = false;
            UpdateJumpToLatest();
            return;
        }
    }

    /**
     * 折叠引起的重估要等这一轮布局结束才落定(滚动事件里读到的还是旧位置), 所以把还原投递到布局之后。
     *
     * 配额**只在真的修了位置时才消耗**: 一次折叠会连发好几个 ScrollChanged(实测: 先 extent+5867 而
     * offset 不动, 再 extent−9866 且 offset 被钳到末端 −2633, 再 +1531/−649, 最后 −1531), 而真正需要
     * 纠正的是中间那几次。若按"每来一个事件就扣一次"发放配额, 打开一个长消息就能把配额用光,
     * 于是最该纠正的那一次反而没人管 —— 表现就是"点开自己却看不见了"。
     */
    private void QueueFoldRestore(ScrollViewer scroll)
    {
        if (_foldAnchorPasses <= 0)
            return;
        Dispatcher.UIThread.Post(() =>
        {
            if (_sticky || _draggingThumb || _restoringAnchor || _foldAnchorData is not { } data)
                return;
            for (var index = 0; index < MessageList.ItemCount; index++)
            {
                if (!ReferenceEquals(MessageList.Items[index], data))
                    continue;
                // 位置已经对得上就不算一次配额消耗(这一趟只是面板在重估, 视口没被挪走)。
                if (IsFoldAnchorInPlace(scroll, index))
                    return;
                _foldAnchorPasses--;
                RestoreAnchor(scroll, index, _foldAnchorTop);
                return;
            }
        }, DispatcherPriority.Loaded);
    }

    /**
     * 面板这次是否已经把被点那条挪离了点击时的位置。
     * 判据是**容器取到没取到**而不是"取到的容器位置对不对": 面板把它甩出实体化窗口时,
     * `ContainerFromIndex` 可能返回一个已经属于别处的旧容器, 它的位置恰好在原处,
     * 于是"位置对得上"会被误判 —— 实测展开一条 4163 字符的思维链, 容器被回收、
     * 该条根本不在视口里, 这里却报告"在位", 还原因此一次都不出手。
     */
    private bool IsFoldAnchorInPlace(ScrollViewer scroll, int index)
        => MessageList.ContainerFromIndex(index) is { Bounds.Height: > 0 } item
            && ReferenceEquals(MessageList.ContainerFromIndex(index), CurrentContainerOf(index))
            && item.TranslatePoint(default, scroll) is { } position
            && Math.Abs(position.Y - _foldAnchorTop) <= AnchorTolerance;

    /** 列表当前真挂着的那个容器(视觉树里找, 不信任 ContainerFromIndex 的缓存)。 */
    private ListBoxItem? CurrentContainerOf(int index)
    {
        foreach (var item in MessageList.GetVisualDescendants().OfType<ListBoxItem>())
            if (MessageList.IndexFromContainer(item) == index)
                return item;
        return null;
    }

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

    /** 跳到某条消息: 落在视口顶部并且不再跟随(用户是特意过来看的)。 */
    private void ScrollToMessage(MessageViewModel message)
    {
        if (_viewModel is not { } viewModel || MessageList.Scroll is not ScrollViewer scroll)
            return;
        var index = viewModel.Messages.IndexOf(message);
        if (index < 0)
            return;
        _sticky = false;
        Dispatcher.UIThread.Post(() => RevealIndex(MessageList, scroll, index, TranscriptTopPadding));
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
        // 端点 /models 自报的模型是适配器后台拉取后才有的, 打开浮层时重算一次清单。
        _viewModel?.Preferences.ReloadModels();
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

    /** 打开浮层时按当前会话模型重算强度候选: 端点自报/探测是后台完成的, 打开时刻通常已就绪。 */
    private void OnReasoningFlyoutOpened(object? sender, EventArgs e)
    {
        _reasoningFlyout = sender as Flyout;
        _viewModel?.RefreshReasoningEfforts();
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

    /** 视图级按键入口: 焦点不在输入框时(用户正在读转录)由这里接管 Home/End 与翻页键。 */
    private void OnTranscriptKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Handled || _viewModel is null)
            return;
        HandlesTranscriptKey(e);
    }

    /**
     * 迁移键与转录导航的归属: 输入框有焦点(有闪烁光标)时 Home/End 属于光标, 归 TextBox;
     * 焦点不在输入框时 Home/End 跳会话头尾。翻页键只看输入框是否自己需要滚动。
     * 输入框写得比 MaxHeight 高、自带滚动条时, 翻页键仍归 TextBox。
     */
    private bool HandlesTranscriptKey(KeyEventArgs e)
    {
        if (e.KeyModifiers is not (KeyModifiers.None or KeyModifiers.Control))
            return false;
        var scroll = MessageList.Scroll;
        if (scroll is null)
            return false;
        // 输入框内容超过 MaxHeight 时它自己要滚动, 翻页键归它。
        var composerScrolls = InputBox.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault() is { } composer
            && composer.Extent.Height - composer.Viewport.Height > AnchorTolerance;
        // 只有焦点真的落在输入框里才算"正在编辑": 焦点在消息列表时 Home/End 属于转录导航。
        var composerFocused = InputBox.IsFocused || InputBox.IsKeyboardFocusWithin;
        var jumpToStart = e.Key == Key.Home && (e.KeyModifiers == KeyModifiers.Control || !composerFocused);
        var jumpToEnd = e.Key == Key.End && (e.KeyModifiers == KeyModifiers.Control || !composerFocused);
        switch (e.Key)
        {
            case Key.PageUp when !composerScrolls:
                ScrollTranscriptBy(-scroll.Viewport.Height);
                break;
            case Key.PageDown when !composerScrolls:
                ScrollTranscriptBy(scroll.Viewport.Height);
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

    private void ScrollTranscriptBy(double delta)
        => ScrollTranscriptTo((MessageList.Scroll?.Offset.Y ?? 0) + delta);

    /**
     * 写偏移用具体像素(禁用 ScrollToEnd); 跟随意图只按**落点**判定, 由调用方指定的方向一律不作数。
     * 否则(例如把 PageDown 当成"恢复跟随")用户停在半途时会被下一帧流式输出拽回底部。
     */
    private void ScrollTranscriptTo(double target)
    {
        if (MessageList.Scroll is not { } scroll)
            return;
        var maxOffset = Math.Max(0, scroll.Extent.Height - scroll.Viewport.Height);
        var offset = Math.Clamp(target, 0, maxOffset);
        scroll.Offset = new Vector(scroll.Offset.X, offset);
        _sticky = offset >= maxOffset - AnchorTolerance;
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
        // Home/End 与翻页键在两种焦点下都要有归属: 挂在视图上才收得到"焦点在转录里"的按键
        // (挂 InputBox 只覆盖输入框有焦点的情况, 那时 Home/End 本来就该留给光标)。
        AddHandler(InputElement.KeyDownEvent, OnTranscriptKeyDown, RoutingStrategies.Tunnel);
        FocusComposer();
    }

    protected override void OnUnloaded(RoutedEventArgs e)
    {
        InputBox.RemoveHandler(InputElement.KeyDownEvent, OnInputKeyDown);
        RemoveHandler(InputElement.KeyDownEvent, OnTranscriptKeyDown);
        Detach();
        base.OnUnloaded(e);
    }

    /** 一次滚动事件时的视口状态; 初始为"在底部"(空列表没有可滚内容)。 */
    private readonly record struct ScrollSnapshot(double Offset, double Viewport, double Extent)
    {
        public bool IsAtBottom(double tolerance) => Offset + Viewport >= Extent - tolerance;
    }
}
