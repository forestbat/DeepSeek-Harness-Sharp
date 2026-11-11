using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Controls.Primitives;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Dsh.Android.Services;
using Dsh.Android.ViewModels;
using Dsh.Gui.ViewModels;
using Dsh.Llm;

namespace Dsh.Android.Views;

public sealed partial class ShellView : UserControl
{
    /** 距离顶部/底部还剩这么多像素就开始自动续取。 */
    private const double AutoLoadThreshold = 400;

    /** 程序自己滚动之后的一小段时间内不再自动续取, 否则换窗口时重置到 0 会立刻再触发一次, 变成连滚。 */
    private const int AutoLoadHoldMilliseconds = 500;

    private MobileShellViewModel? _viewModel;
    private IDisposable? _backRegistration;
    private DateTime _autoLoadHold = DateTime.MinValue;

    /** 是否跟随会话末尾: 用户停在底部时为 true; 一旦翻到更早的位置就置 false, 新事件不再把他拽回去。 */
    private bool _followTail = true;

    /** 上滑加载前的内容高度, 插到队首之后用它补齐滚动位置。 */
    private double _extentBeforeOlder;

    public ShellView()
    {
        InitializeComponent();
        /*
         * ScrollViewer 在 ItemsControl 自己的模板里, 所以 ScrollChanged 从它向上冒泡会经过 MessageScroll。
         * 之前两种写法都不对: 挂在外层 ScrollViewer 的子控件上收不到(事件只向上走),
         * 或者把 ScrollViewer 放在外面导致虚拟化失效。
         */
        MessageScroll.AddHandler(ScrollViewer.ScrollChangedEvent, OnScrollChanged, RoutingStrategies.Bubble);
        // 长按/双击由列表内部的消息发起, 冒泡到这里统一处理。
        MessageScroll.AddHandler(InputElement.HoldingEvent, OnMessageHolding, RoutingStrategies.Bubble);
        MessageScroll.AddHandler(InputElement.DoubleTappedEvent, OnMessageDoubleTapped, RoutingStrategies.Bubble);
        DataContextChanged += (_, _) => Attach();
    }

    protected override void OnAttachedToVisualTree(Avalonia.VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        Attach();
        KeyboardInset.Changed += OnKeyboardInset;
        ApplyKeyboardInset(KeyboardInset.Bottom);
        Dispatcher.UIThread.Post(ScrollToEnd, DispatcherPriority.Loaded);
    }

    protected override void OnDetachedFromVisualTree(Avalonia.VisualTreeAttachmentEventArgs e)
    {
        KeyboardInset.Changed -= OnKeyboardInset;
        Detach();
        base.OnDetachedFromVisualTree(e);
    }

    private void OnKeyboardInset(double bottom) => Dispatcher.UIThread.Post(() => ApplyKeyboardInset(bottom));

    /** 输入法弹起时把外壳底部顶高同样高度, 输入框就浮在键盘上方。 */
    private void ApplyKeyboardInset(double bottom) => ShellRoot.Margin = new Thickness(0, 0, 0, bottom);

    private void Attach()
    {
        if (_viewModel is not null)
        {
            _viewModel.TranscriptChanged -= ScrollToEnd;
            _viewModel.ScrollToSeqRequested -= ScrollToSeq;
            _viewModel.OlderLoaded -= OnOlderLoaded;
        }
        _viewModel = DataContext as MobileShellViewModel;
        if (_viewModel is not null)
        {
            _viewModel.TranscriptChanged += ScrollToEnd;
            _viewModel.ScrollToSeqRequested += ScrollToSeq;
            _viewModel.OlderLoaded += OnOlderLoaded;
        }
        // 系统返回热键先交给界面: 能退一级就退一级, 最外层才让系统退出 App。
        _backRegistration ??= BackNavigation.Register(() =>
        {
            if (_viewModel is not { } viewModel)
                return false;
            var canGoBack = viewModel.CanGoBack;
            if (canGoBack)
                _ = viewModel.GoBackAsync();
            return canGoBack;
        });
    }

    private void Detach()
    {
        _backRegistration?.Dispose();
        _backRegistration = null;
        if (_viewModel is null)
            return;
        _viewModel.TranscriptChanged -= ScrollToEnd;
        _viewModel.ScrollToSeqRequested -= ScrollToSeq;
        _viewModel.OlderLoaded -= OnOlderLoaded;
        _viewModel = null;
    }

    /**
     * 队首插入更早的消息后, 把滚动位置补回原来的那一条。
     * 插入会让内容整体下移, 不补的话视线会被推走; 补的量就是这次新增的高度。
     */
    private void OnOlderLoaded(int count)
    {
        if (count <= 0 || Scroller is not { } scroll)
            return;
        var before = _extentBeforeOlder;
        Dispatcher.UIThread.Post(() =>
        {
            if (Scroller is not { } current)
                return;
            Hold();
            // 插入的这几条要先落一次布局才有真实高度, 差值就是它们撑出来的部分。
            // 插入前的 extent 还不可信(首屏尚未量出)时不做补偿, 否则会把滚动位置设成一个凭空算出的值。
            var delta = current.Extent.Height - before;
            if (before > 0 && delta > 0)
                current.Offset = new Vector(0, current.Offset.Y + delta);
            MobileLog.Info($"older-loaded: count={count} extent={before}->{current.Extent.Height} delta={delta} offset={current.Offset.Y}");
        }, DispatcherPriority.Loaded);
    }

    /**
     * 滚到两头就自动续取: 上滑到顶取更早的一段, 下滑到底取更晚的一段。
     * 程序滚动(换窗口/定位到某条)之后短暂停手, 否则新窗口从 0 开始会立刻再触发一次加载。
     */
    private void OnScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        if (_viewModel is null || _viewModel.IsLoadingOlder)
            return;
        if (e.Source is not ScrollViewer scroll)
            return;
        var remaining = scroll.Extent.Height - scroll.Viewport.Height - scroll.Offset.Y;
        // 回到末尾就恢复跟随, 之后新来的事件继续自动滚到底。
        _followTail = remaining <= AutoLoadThreshold;
        if (DateTime.UtcNow < _autoLoadHold)
            return;
        if (scroll.Offset.Y <= AutoLoadThreshold && _viewModel.CanLoadOlder)
        {
            // 记下插入前的高度, 补位时用它算新增了多少。
            _extentBeforeOlder = scroll.Extent.Height;
            Hold();
            _ = _viewModel.LoadOlderAsync();
            return;
        }
        if (remaining <= AutoLoadThreshold && _viewModel.CanLoadNewer)
        {
            Hold();
            _ = _viewModel.LoadNewerAsync();
        }
    }

    /** 双击一条消息: 折叠 ↔ 展开。长消息默认折叠, 否则一条就占掉好几屏。 */
    private void OnMessageDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (FindMessage(e.Source) is not { } message || !message.IsFoldable)
            return;
        message.IsExpanded = !message.IsExpanded;
    }

    /**
     * 长按一条消息: 复制全文并提示。
     * 列表本身不参与选中, 所以滑动永远不会误选, 手指也不必和选中逻辑抢手势。
     */
    private async void OnMessageHolding(object? sender, HoldingRoutedEventArgs e)
    {
        if (_viewModel is null || e.HoldingState != HoldingState.Started)
            return;
        if (FindMessage(e.Source) is not { } message)
            return;
        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard is null)
            return;
        var transfer = new DataTransfer();
        transfer.Add(DataTransferItem.CreateText(message.Text));
        await clipboard.SetDataAsync(transfer);
        _viewModel.ReportNotice("已复制这条消息");
    }

    /** 事件源向上找第一条带消息 DataContext 的控件。 */
    private static MessageViewModel? FindMessage(object? source)
        => source is not Visual visual
            ? null
            : visual.GetSelfAndVisualAncestors()
                .OfType<Control>()
                .Select(control => control.DataContext)
                .OfType<MessageViewModel>()
                .FirstOrDefault();

    /** 程序滚动之后的一小段时间里不让滚动位置变化引发自动续取。 */
    private void Hold() => _autoLoadHold = DateTime.UtcNow.AddMilliseconds(AutoLoadHoldMilliseconds);

    /** 列表模板里的滚动容器; 模板套用前为 null。 */
    private ScrollViewer? Scroller => MessageScroll.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault();

    /**
     * 新事件到达时跟随到底部 —— 但只在用户本来就停在底部时。
     * 翻到更早的位置之后再被拉回末尾, 等于把刚看到的东西丢掉。
     */
    private void ScrollToEnd()
    {
        if (_viewModel is null || _viewModel.Messages.Count == 0)
            return;
        if (!_followTail)
            return;
        Hold();
        // 先把末尾这一条带进来, 面板才会生成它并算出真实高度; 否则 Extent 还是旧的估计值。
        MessageScroll.ScrollIntoView(_viewModel.Messages.Count - 1);
        Dispatcher.UIThread.Post(() =>
        {
            Hold();
            if (Scroller is { } scroll)
                scroll.Offset = new Vector(0, scroll.Extent.Height);
        }, DispatcherPriority.Loaded);
    }

    /** 时间线"跳到"落在这里: 按事件序号在已渲染的消息里找到那条, 滚过去并停在它的上沿。 */
    private void ScrollToSeq(long seq)
    {
        if (_viewModel is null)
            return;
        Hold();
        // 定位到某条之后不再跟随末尾, 否则下一条事件就会把视图拽回去。
        _followTail = false;
        // 列表刚换了整窗内容, 要等这一次布局量出真实位置再滚, 否则会作用在旧坐标上。
        Dispatcher.UIThread.Post(() =>
        {
            if (_viewModel is null)
                return;
            var index = -1;
            for (var position = 0; position < _viewModel.Messages.Count; position += 1)
            {
                if (_viewModel.Messages[position].Seq is { } value && value <= seq)
                    index = position;
            }
            // 找不到说明这一窗确实不包含锚点, ViewModel 会再取一窗; 这里不弹提示。
            if (index < 0)
                return;
            Hold();
            // 虚拟化下容器只在可见区附近存在, 因此让面板自己把这一条带进来, 再对齐到它的上沿。
            _ = BringIntoViewAsync(index);
        }, DispatcherPriority.Loaded);
    }

    /** 把第 index 条对齐到视口上沿; 虚拟化面板会为此先生成这一条的容器。 */
    private async Task BringIntoViewAsync(int index)
    {
        if (Scroller is not { } scroll)
            return;
        // 目标容器还没生成时先粗滚到大致位置, 让面板把它造出来, 再精确对齐。
        if (MessageScroll.ContainerFromIndex(index) is not Control container)
        {
            var estimate = scroll.Extent.Height * index / Math.Max(1, _viewModel!.Messages.Count);
            scroll.Offset = new Vector(0, Math.Max(0, estimate));
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Loaded);
            container = MessageScroll.ContainerFromIndex(index) as Control;
            if (container is null)
                return;
        }
        container.BringIntoView();
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Loaded);
        Hold();
        scroll.Offset = new Vector(0, Math.Max(0, container.Bounds.Y - 8));
    }

    /** 图片选择走 Android 的系统选择器; 只把字节读进内存, 不保留 content URI 或流。 */
    private async void OnPickAttachment(object? sender, RoutedEventArgs e)
    {
        if (_viewModel is null)
            return;
        var storage = TopLevel.GetTopLevel(this)?.StorageProvider;
        if (storage is null)
            return;
        try
        {
            var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "选择图片",
                AllowMultiple = false,
                FileTypeFilter = [FilePickerFileTypes.ImageAll],
            });
            if (files.Count == 0)
                return;
            var file = files[0];
            using var buffer = new MemoryStream();
            await using (var stream = await file.OpenReadAsync())
                await stream.CopyToAsync(buffer);
            if (buffer.Length > ImageAttachments.MaxBytes)
            {
                _viewModel.ReportError($"图片不能超过 {ImageAttachments.MaxBytes / (1024 * 1024)} MB");
                return;
            }
            var bytes = buffer.ToArray();
            var mediaType = ImageAttachments.Sniff(bytes)
                ?? ImageAttachments.MediaTypeForExtension(Path.GetExtension(file.Name));
            if (mediaType is null)
            {
                _viewModel.ReportError("不支持的图片格式");
                return;
            }
            _viewModel.SetAttachment(file.Name, mediaType, bytes);
        }
        catch (Exception error)
        {
            _viewModel.ReportError($"选择图片失败: {error.Message}");
        }
    }

    private void OnClearAttachment(object? sender, RoutedEventArgs e) => _viewModel?.ClearAttachment();

    private async void OnCopyPhoneToolsUrl(object? sender, RoutedEventArgs e)
    {
        if (_viewModel is null || _viewModel.PhoneToolsUrl.Length == 0)
            return;
        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard is null)
            return;
        var transfer = new DataTransfer();
        transfer.Add(DataTransferItem.CreateText(_viewModel.PhoneToolsUrl));
        await clipboard.SetDataAsync(transfer);
    }

    private async void OnCopyPublicKey(object? sender, RoutedEventArgs e)
    {
        if (_viewModel is null || _viewModel.PublicKeyLine.Length == 0)
            return;
        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard is null)
            return;
        var transfer = new DataTransfer();
        transfer.Add(DataTransferItem.CreateText(_viewModel.PublicKeyLine));
        await clipboard.SetDataAsync(transfer);
    }
}
