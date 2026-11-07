using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Dsh.Gui.ViewModels;
using Dsh.Gui.Views;
using Dsh.Core;

namespace Dsh.Tests;

/** GUI 消息区: 鼠标拖动选中文字后 Ctrl+C 必须写入剪贴板。 */
[Collection(GuiSerialCollection.CollectionName)]
public sealed class MessageSelectionTests
{
    [Fact]
    public async Task DraggingMarkdownText_SelectsItAndCopiesWithCtrlC() => await HeadlessGui.RunAsync(async () =>
    {
        await using var scene = await SelectionScene.CreateAsync("第一段可选中文字\n\n第二段可选中文字");
        var block = Assert.Single(scene.TextBlocks, candidate => TextOf(candidate).Contains("第一段", StringComparison.Ordinal));
        var start = block.TranslatePoint(new Point(2, 5), scene.Window)!.Value;
        var end = block.TranslatePoint(new Point(block.Bounds.Width - 2, 5), scene.Window)!.Value;
        scene.Window.MouseDown(start, MouseButton.Left);
        scene.Window.MouseMove(end, RawInputModifiers.LeftMouseButton);
        scene.Window.MouseUp(end, MouseButton.Left);
        scene.Render();
        Assert.True(block.IsFocused, "拖动选中后文本块应获得焦点");
        Assert.False(string.IsNullOrEmpty(block.SelectedText), "拖动后应处于选中状态");
        Assert.Contains("第一段可选中文字", block.SelectedText);
        scene.Window.KeyPress(Key.C, RawInputModifiers.Control, PhysicalKey.C, null);
        scene.SettleClipboard();
        var data = await scene.Window.Clipboard!.TryGetDataAsync();
        Assert.NotNull(data);
        Assert.Equal(block.SelectedText, await data.TryGetTextAsync());
    });

    [Fact]
    public async Task DragSelectingToolOutput_Works() => await HeadlessGui.RunAsync(async () =>
    {
        await using var scene = await SelectionScene.CreateAsync("工具输出第一行\n工具输出第二行", MessageKind.Result);
        var block = Assert.Single(scene.TextBlocks, candidate => TextOf(candidate).Contains("工具输出", StringComparison.Ordinal));
        var start = block.TranslatePoint(new Point(2, 5), scene.Window)!.Value;
        var end = block.TranslatePoint(new Point(block.Bounds.Width - 2, 5), scene.Window)!.Value;
        scene.Window.MouseDown(start, MouseButton.Left);
        scene.Window.MouseMove(end, RawInputModifiers.LeftMouseButton);
        scene.Window.MouseUp(end, MouseButton.Left);
        scene.Render();
        Assert.Contains("工具输出", block.SelectedText);
    });

    [Fact]
    public async Task DragSelectingCodeBlock_Works() => await HeadlessGui.RunAsync(async () =>
    {
        await using var scene = await SelectionScene.CreateAsync("说明\n\n```\nvar x = 1;\nvar y = 2;\n```");
        var block = Assert.Single(scene.TextBlocks, candidate => TextOf(candidate).Contains("var x", StringComparison.Ordinal));
        SelectFirstLine(scene, block);
        Assert.Contains("var x = 1;", block.SelectedText);
    });

    [Fact]
    public async Task DragSelectingDiffLine_Works() => await HeadlessGui.RunAsync(async () =>
    {
        var message = new MessageViewModel("结果", "已生成改动", MessageKind.Result, false);
        var lines = new List<DiffLine>
        {
            new(DiffLineKind.Add, 1, 1, "新增的一行改动"),
            new(DiffLineKind.Delete, 2, null, "删除的一行改动"),
        };
        message.SetDiff(new DiffCard("文件.cs", "", lines));
        // diff 卡片默认折叠, 选中行为要在用户展开后的卡片上验证。
        message.IsExpanded = true;
        message.IsDiffExpanded = true;
        await using var scene = await SelectionScene.CreateAsync(message);
        var block = Assert.Single(scene.TextBlocks, candidate => TextOf(candidate).Contains("新增的一行改动", StringComparison.Ordinal));
        SelectFirstLine(scene, block);
        Assert.Contains("新增的一行改动", block.SelectedText);
    });

    private static void SelectFirstLine(SelectionScene scene, SelectableTextBlock block)
    {
        var start = block.TranslatePoint(new Point(2, 5), scene.Window)!.Value;
        var end = block.TranslatePoint(new Point(block.Bounds.Width - 2, 5), scene.Window)!.Value;
        scene.Window.MouseDown(start, MouseButton.Left);
        scene.Window.MouseMove(end, RawInputModifiers.LeftMouseButton);
        scene.Window.MouseUp(end, MouseButton.Left);
        scene.Render();
    }

    private static string TextOf(SelectableTextBlock block) => block.Inlines?.Text ?? block.Text ?? "";

    private sealed class SelectionScene : IAsyncDisposable
    {
        private readonly GuiTestEnvironment _environment;

        private SelectionScene(GuiTestEnvironment environment, MainViewModel viewModel, Window window, ChatView chat)
        {
            _environment = environment;
            ViewModel = viewModel;
            Window = window;
            Chat = chat;
        }

        private MainViewModel ViewModel { get; }

        public Window Window { get; }

        private ChatView Chat { get; }

        public IReadOnlyList<SelectableTextBlock> TextBlocks =>
        [
            .. Chat.GetVisualDescendants().OfType<SelectableTextBlock>()
                .Where(block => block.IsEffectivelyVisible && !string.IsNullOrWhiteSpace(TextOf(block))),
        ];

        public static Task<SelectionScene> CreateAsync(string text, MessageKind kind = MessageKind.Assistant)
            => CreateAsync(new MessageViewModel("assistant", text, kind, false));

        public static async Task<SelectionScene> CreateAsync(MessageViewModel message)
        {
            var environment = await GuiTestEnvironment.CreateAsync();
            var viewModel = new MainViewModel(environment.App, await environment.OpenChatAsync());
            viewModel.Messages.Add(message);
            var chat = new ChatView { DataContext = viewModel };
            var window = new Window { Width = 1280, Height = 800, Content = chat };
            window.Show();
            var scene = new SelectionScene(environment, viewModel, window, chat);
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

        public void SettleClipboard()
        {
            for (var pass = 0; pass < 5; pass++)
                Render();
        }

        public async ValueTask DisposeAsync()
        {
            Window.Close();
            Dispatcher.UIThread.RunJobs();
            await ViewModel.DisposeAsync();
            await _environment.DisposeAsync();
        }
    }
}
