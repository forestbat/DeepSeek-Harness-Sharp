using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Dsh.Gui.ViewModels;
using Dsh.Gui.Views;

namespace Dsh.Tests;

/** 一个真实的 ChatView + 24 轮消息: 折叠/滚动用例共用同一套装配与坐标读取。 */
internal sealed class ChatScene : IAsyncDisposable
{
    private const int MessagePairs = 24;

    private readonly GuiTestEnvironment _environment;

    private ChatScene(GuiTestEnvironment environment, MainViewModel viewModel, Window window, ChatView chat)
    {
        _environment = environment;
        ViewModel = viewModel;
        Window = window;
        Chat = chat;
    }

    public MainViewModel ViewModel { get; }

    public Window Window { get; }

    public ChatView Chat { get; }

    public ListBox List => Chat.FindControl<ListBox>("MessageList")!;

    public ScrollViewer Scroll => Assert.IsType<ScrollViewer>(List.Scroll);

    public static async Task<ChatScene> CreateAsync()
    {
        var environment = await GuiTestEnvironment.CreateAsync();
        var viewModel = new MainViewModel(environment.App, await environment.OpenChatAsync());
        var chat = new ChatView { DataContext = viewModel };
        var window = new Window { Width = 1280, Height = 800, Content = chat };
        window.Show();
        for (var index = 0; index < MessagePairs; index++)
        {
            viewModel.Messages.Add(new MessageViewModel("用户", $"问题 {index}", MessageKind.User, false));
            viewModel.Messages.Add(new MessageViewModel("结果", LongText(index), MessageKind.Result, false));
        }
        var scene = new ChatScene(environment, viewModel, window, chat);
        scene.Render();
        return scene;
    }

    public void Render()
    {
        Dispatcher.UIThread.RunJobs();
        Window.UpdateLayout();
        Window.CaptureRenderedFrame()?.Dispose();
        Dispatcher.UIThread.RunJobs();
    }

    /** 折叠后的对齐要跨多轮布局: 回收/实体化目标项、修正偏移、再确认稳定。 */
    public void Settle()
    {
        for (var pass = 0; pass < 5; pass++)
            Render();
    }

    public void ScrollToMiddle(ScrollViewer? scroll = null)
    {
        scroll ??= Scroll;
        var wheelPoint = scroll.TranslatePoint(new Point(60, 60), Window)!.Value;
        for (var attempt = 0; attempt < 16; attempt++)
        {
            if (scroll.Offset.Y < scroll.Extent.Height - scroll.Viewport.Height - 50)
                break;
            Window.MouseWheel(wheelPoint, new Vector(0, 6));
            Render();
        }
        Assert.True(scroll.Offset.Y < scroll.Extent.Height - scroll.Viewport.Height - 50,
            "precondition: the list must be scrolled away from the tail");
    }

    /** 把目标项放到视口中指定位置, 排除虚拟化估算带来的偏移误差。 */
    public void ScrollToTop(object data, double desiredTop)
    {
        for (var attempt = 0; attempt < 5; attempt++)
        {
            var top = TryTopOf(List, data);
            if (top is null)
            {
                List.ScrollIntoView(data);
                Render();
                continue;
            }
            if (Math.Abs(top.Value - desiredTop) <= 2)
                return;
            Scroll.Offset = new Vector(0, Math.Max(0, Scroll.Offset.Y + top.Value - desiredTop));
            Render();
        }
    }

    public MessageViewModel FindVisibleFoldTarget()
    {
        var button = List.GetVisualDescendants().OfType<Button>()
            .Where(candidate => candidate is { IsEffectivelyVisible: true, DataContext: MessageViewModel { IsFoldable: true } })
            .OrderBy(TopOf)
            .FirstOrDefault(candidate => TopOf(candidate) > 40 && TopOf(candidate) < Scroll.Viewport.Height - 40);
        Assert.NotNull(button);
        return Assert.IsType<MessageViewModel>(button.DataContext);
    }

    public void ToggleAndAssertPosition(MessageViewModel message, string label, bool expanded = true)
    {
        var before = TopOf(List, message);
        var button = List.GetVisualDescendants().OfType<Button>()
            .First(candidate => ReferenceEquals(candidate.DataContext, message));
        Tap(button);
        Settle();
        Assert.Equal(expanded, message.IsExpanded);
        var after = TopOf(List, message);
        Assert.True(Math.Abs(after - before) <= 2,
            $"{label}: message moved from {before:0} to {after:0} (offset {Scroll.Offset.Y:0})");
    }

    public void Tap(Button button)
    {
        var point = button.TranslatePoint(new Point(button.Bounds.Width / 2, button.Bounds.Height / 2), Window)!.Value;
        Window.MouseDown(point, MouseButton.Left);
        Window.MouseUp(point, MouseButton.Left);
    }

    /** 在视口中部的一条消息上按下/抬起指针: 会同时改变列表选中项与焦点。 */
    public void ClickInTranscript()
    {
        var point = Scroll.TranslatePoint(new Point(Scroll.Bounds.Width / 2, Scroll.Viewport.Height / 2), Window)!.Value;
        Window.MouseDown(point, MouseButton.Left);
        Window.MouseUp(point, MouseButton.Left);
    }

    /** 鼠标滚轮滚动消息列表; delta>0 表示向上滚。 */
    public void Wheel(double delta)
    {
        var point = Scroll.TranslatePoint(new Point(Scroll.Bounds.Width / 2, Scroll.Viewport.Height / 2), Window)!.Value;
        Window.MouseWheel(point, new Vector(0, delta));
    }

    /** 视口里最后一条消息(展开到正文), 用来模拟正在接收流式增量的那条。 */
    public MessageViewModel LastStreamingMessage()
    {
        var message = ViewModel.Messages[^1];
        if (!message.ShowBody)
        {
            message.IsExpanded = true;
            Render();
        }
        return message;
    }

    public double TopOf(Visual visual) => visual.TranslatePoint(default, Scroll)?.Y ?? double.NaN;    public double TopOf(ListBox list, object data)
    {
        var top = TryTopOf(list, data);
        Assert.True(top is not null,
            $"item is not realized: offset {Scroll.Offset.Y:0}/{Scroll.Extent.Height:0} viewport {Scroll.Viewport.Height:0}, "
            + $"items {list.ItemCount}, realized {list.GetVisualDescendants().OfType<ListBoxItem>().Count()}");
        return top!.Value;
    }

    private static double? TryTopOf(ListBox list, object data)
    {
        var scroll = Assert.IsType<ScrollViewer>(list.Scroll);
        var item = list.GetVisualDescendants().OfType<ListBoxItem>()
            .FirstOrDefault(candidate => ReferenceEquals(candidate.DataContext, data));
        return item?.TranslatePoint(default, scroll)?.Y;
    }

    public async ValueTask DisposeAsync()
    {
        Window.Close();
        Dispatcher.UIThread.RunJobs();
        await ViewModel.DisposeAsync();
        await _environment.DisposeAsync();
    }

    internal static string LongText(int index)
    {
        var text = new System.Text.StringBuilder();
        for (var line = 0; line < 12; line++)
            text.AppendLine($"第 {index} 条结果第 {line} 行: 这是一段用于撑高消息卡片的长文本。");
        return text.ToString();
    }
}
