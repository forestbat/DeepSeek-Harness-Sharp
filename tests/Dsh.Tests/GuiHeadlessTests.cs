using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.VisualTree;
using Avalonia.Styling;
using Avalonia.Threading;
using Dsh.Core;
using Dsh.Gui.Services;
using Dsh.Gui.ViewModels;
using Dsh.Gui.Views;
using Dsh.Interaction;
using Dsh.Llm;
using Dsh.Tui;
using TextBlock = Avalonia.Controls.TextBlock;

namespace Dsh.Tests;

/** 真实渲染验证: headless Avalonia + Skia, 打开主窗口逐页截图, 消息列表按 1000 条压测渲染。 */
[Collection(GuiSerialCollection.CollectionName)]
public sealed class GuiHeadlessTests(ITestOutputHelper output)
{
    private const int BulkMessages = 1000;
    private static readonly string ScreenshotDirectory = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "artifacts020", "gui-screenshots"));

    [Fact]
    public async Task MainWindow_RendersEveryPage() => await HeadlessGui.RunAsync(async () =>
        {
            var environment = await GuiTestEnvironment.CreateAsync();
            await using var environmentScope = environment;
            var window = new MainWindow(environment.App, await environment.OpenChatAsync());
            var viewModel = window.ViewModel!;
            window.Show();
            Dispatcher.UIThread.RunJobs();

            Capture(window, "headless-dashboard");
            Assert.True(viewModel.IsDashboardPage);
            Assert.True(window.GetVisualDescendants().OfType<DashboardView>().Single().IsVisible);

            viewModel.ShowChatCommand.Execute(null);
            Dispatcher.UIThread.RunJobs();
            Capture(window, "headless-chat");
            Assert.True(viewModel.IsChatPage);
            Assert.True(window.GetVisualDescendants().OfType<ChatView>().Single().IsVisible);
            Assert.Empty(window.GetVisualDescendants().OfType<DashboardView>());
            var inputBox = window.GetVisualDescendants().OfType<ChatView>().Single().FindControl<TextBox>("InputBox");
            Assert.NotNull(inputBox);
            Assert.True(inputBox.IsVisible);
            Assert.True(inputBox.Bounds.Height > 0, $"input box should have a height, got {inputBox.Bounds}");

            viewModel.ShowSettingsCommand.Execute(null);
            await GuiTestEnvironment.WaitForAsync(viewModel.Preferences.Plugins,
                () => viewModel.Preferences.Plugins.Count > 0);
            Dispatcher.UIThread.RunJobs();
            Capture(window, "headless-settings");
            var settings = window.GetVisualDescendants().OfType<SettingsPageView>().Single();
            Assert.True(settings.IsVisible);
            Assert.NotEmpty(settings.GetVisualDescendants());
            Assert.NotEmpty(viewModel.Preferences.Plugins);
            Assert.Empty(window.GetVisualDescendants().OfType<DashboardView>());

            viewModel.ShowMarketCommand.Execute(null);
            Dispatcher.UIThread.RunJobs();
            Capture(window, "headless-market");
            Assert.True(window.GetVisualDescendants().OfType<MarketPageView>().Single().IsVisible);
            Assert.Empty(window.GetVisualDescendants().OfType<SettingsPageView>());

            viewModel.ShowChatCommand.Execute(null);
            viewModel.ShowTraceCommand.Execute(null);
            Dispatcher.UIThread.RunJobs();
            Assert.True(viewModel.IsTraceTab);
            Capture(window, "headless-trace");

            window.Close();
            Dispatcher.UIThread.RunJobs();
        });

    /** 侧栏收起时顶栏标题要让开左上角的展开按钮, 否则汉堡键压住标题。 */
    [Fact]
    public async Task CollapsedSidebar_ShiftsChatHeaderRight() => await HeadlessGui.RunAsync(async () =>
        {
            var environment = await GuiTestEnvironment.CreateAsync();
            await using var environmentScope = environment;
            var window = new MainWindow(environment.App, await environment.OpenChatAsync());
            var viewModel = window.ViewModel!;
            window.Show();
            Dispatcher.UIThread.RunJobs();

            var chat = window.GetVisualDescendants().OfType<ChatView>().Single();
            var header = chat.FindControl<Grid>("ChatHeader")!;

            viewModel.IsSidebarVisible = false;
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(56, header.Margin.Left);

            viewModel.IsSidebarVisible = true;
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(20, header.Margin.Left);

            window.Close();
            Dispatcher.UIThread.RunJobs();
        });

    [Fact]
    public async Task DecisionDialog_RendersAndResolvesApproval() => await HeadlessGui.RunAsync(async () =>
        {
            var environment = await GuiTestEnvironment.CreateAsync();
            await using var environmentScope = environment;
            var decision = DecisionViewModel.ForApproval(new ApprovalRequest(
                environment.Agent,
                "bash",
                ToolCallId.Create("call-1"),
                "执行构建命令",
                """{"command":"rm -rf ./bin"}"""));
            var dialog = new DecisionDialog(decision);
            dialog.Show();
            Dispatcher.UIThread.RunJobs();

            Capture(dialog, "headless-decision-approval", 430);
            Assert.Contains(dialog.GetVisualDescendants(), visual => visual is TextBlock block && block.Text == "bash");
            // 弹窗要展示真实命令: 参数从 ApprovalRequest.Arguments 解析出来。
            Assert.Contains(dialog.GetVisualDescendants(), visual => visual is TextBlock block && block.Text == "rm -rf ./bin");

            decision.AllowAlwaysCommand.Execute(null);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(ApprovalOutcome.AllowedForSession, decision.Result);
        });

    [Fact]
    public async Task DecisionDialog_RendersQuestion_AndSubmitsSelection() => await HeadlessGui.RunAsync(async () =>
        {
            var environment = await GuiTestEnvironment.CreateAsync();
            await using var environmentScope = environment;
            var decision = DecisionViewModel.ForQuestion(new AskUserQuestionRequest(
                [
                    new AskUserQuestionItem(
                    "q1",
                    "选择部署目标",
                    Detail: "# 发布计划\n- 灰度\n- 全量",
                    Header: "部署",
                    Options: [new AskUserQuestionOption("灰度", "先放 5% 流量"), new AskUserQuestionOption("全量")]),
            ],
                environment.Agent));
            var dialog = new DecisionDialog(decision);
            dialog.Show();
            Dispatcher.UIThread.RunJobs();

            Capture(dialog, "headless-decision-question", 520);
            var single = decision.Questions[0];
            var option = dialog.GetVisualDescendants().OfType<Button>().Single(button => ReferenceEquals(button.DataContext, single.Options[1]));
            var point = option.TranslatePoint(new Point(option.Bounds.Width / 2, option.Bounds.Height / 2), dialog)!.Value;
            dialog.MouseDown(point, MouseButton.Left, RawInputModifiers.None);
            dialog.MouseUp(point, MouseButton.Left, RawInputModifiers.None);
            Dispatcher.UIThread.RunJobs();
            Assert.True(decision.CanSubmit);

            decision.SubmitCommand.Execute(null);
            Dispatcher.UIThread.RunJobs();
            var answer = Assert.IsType<AskUserQuestionAnswer>(decision.Result);
            Assert.Equal("全量", Assert.Single(answer.Answers).Selected[0]);
        });

    [Theory]
    [InlineData(Key.Y, ApprovalOutcome.AllowedOnce)]
    [InlineData(Key.A, ApprovalOutcome.AllowedForSession)]
    [InlineData(Key.N, ApprovalOutcome.Rejected)]
    [InlineData(Key.C, ApprovalOutcome.Cancelled)]
    [InlineData(Key.Escape, ApprovalOutcome.Cancelled)]
    public async Task DecisionDialog_ApprovalKeyboardActions_CloseWithoutAllowingOnEscape(Key key, ApprovalOutcome expected)
        => await HeadlessGui.RunAsync(() =>
        {
            var decision = DecisionViewModel.ForApproval("bash", """{"command":"echo review"}""", "Review execution");
            var dialog = new DecisionDialog(decision);
            dialog.Show();
            Dispatcher.UIThread.RunJobs();
            var input = new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = key };
            dialog.RaiseEvent(input);
            Dispatcher.UIThread.RunJobs();
            Assert.True(input.Handled);
            Assert.Equal(expected, decision.Result);
            Assert.False(dialog.IsVisible);
            return Task.CompletedTask;
        });

    [Fact]
    public async Task DecisionDialog_EscapeFromCustomInput_DismissesWholeQuestionGroup()
        => await HeadlessGui.RunAsync(() =>
        {
            var decision = DecisionViewModel.ForQuestion(new AskUserQuestionRequest(
            [
                new("choice", "Choose", Options: [new("Default", Value: "default", Default: true)]),
                new("custom", "Add details"),
            ]));
            var dialog = new DecisionDialog(decision);
            dialog.Show();
            Dispatcher.UIThread.RunJobs();
            var custom = dialog.GetVisualDescendants().OfType<TextBox>().Last();
            custom.Focus();
            custom.Text = "Partial answer";
            var input = new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Escape };
            custom.RaiseEvent(input);
            Dispatcher.UIThread.RunJobs();
            Assert.True(input.Handled);
            var result = Assert.IsType<AskUserQuestionAnswer>(decision.Result);
            Assert.True(result.IsDismissed);
            Assert.Empty(result.Answers);
            Assert.Equal("user_cancelled", result.Reason);
            Assert.False(dialog.IsVisible);
            return Task.CompletedTask;
        });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DecisionDialog_UnsupportedCancellation_LeavesPendingVisible_AndExistingAnswerAvailable(bool approval)
        => await HeadlessGui.RunAsync(() =>
        {
            var decision = approval
                ? DecisionViewModel.ForApproval("bash", null, "Review")
                : DecisionViewModel.ForQuestion(new AskUserQuestionRequest(
                    [new("q", "Choose", Options: [new("Yes", Value: "yes", Default: true)])]));
            decision.SetCancellationSupported(false);
            var dialog = new DecisionDialog(decision);
            dialog.Show();
            Dispatcher.UIThread.RunJobs();
            var cancel = dialog.GetVisualDescendants().OfType<Button>().First(button => button.Command == decision.CancelCommand);
            Assert.False(cancel.IsEffectivelyEnabled);
            Assert.Contains(dialog.GetVisualDescendants(), visual =>
                visual is TextBlock { IsVisible: true, Text: { } text } && text.Contains("Unsupported", StringComparison.Ordinal));

            var escape = new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Escape };
            dialog.RaiseEvent(escape);
            dialog.Close();
            Dispatcher.UIThread.RunJobs();
            Assert.True(escape.Handled);
            Assert.True(dialog.IsVisible);
            Assert.False(decision.IsClosed);
            Assert.Null(decision.Result);

            var answer = new KeyEventArgs
            {
                RoutedEvent = InputElement.KeyDownEvent,
                Key = approval ? Key.N : Key.Enter,
                KeyModifiers = approval ? KeyModifiers.None : KeyModifiers.Control,
            };
            dialog.RaiseEvent(answer);
            Dispatcher.UIThread.RunJobs();
            Assert.False(dialog.IsVisible);
            if (approval)
                Assert.Equal(ApprovalOutcome.Rejected, decision.Result);
            else
                Assert.Equal(["yes"], Assert.Single(Assert.IsType<AskUserQuestionAnswer>(decision.Result).Answers).Selected);
            return Task.CompletedTask;
        });

    [Fact]
    public async Task ThemeSwitch_AppliesLightVariantToTheWholeApp() => await HeadlessGui.RunAsync(async () =>
        {
            var environment = await GuiTestEnvironment.CreateAsync();
            await using var environmentScope = environment;
            var window = new MainWindow(environment.App, await environment.OpenChatAsync());
            var viewModel = window.ViewModel!;
            window.Show();
            Dispatcher.UIThread.RunJobs();

            viewModel.Preferences.SelectThemeCommand.Execute("light");
            Dispatcher.UIThread.RunJobs();
            var application = Application.Current;
            Assert.NotNull(application);
            Assert.Equal(ThemeVariant.Light, application.ActualThemeVariant);
            viewModel.ShowSettingsCommand.Execute(null);
            Dispatcher.UIThread.RunJobs();
            Capture(window, "headless-settings-light");

            viewModel.Preferences.SelectThemeCommand.Execute("dark");
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(ThemeVariant.Dark, application.ActualThemeVariant);

            window.Close();
            Dispatcher.UIThread.RunJobs();
        });

    [Fact]
    public async Task SettingsGraphics_ListsAdaptersAndSaves() => await HeadlessGui.RunAsync(async () =>
        {
            var environment = await GuiTestEnvironment.CreateAsync();
            await using var environmentScope = environment;
            var window = new MainWindow(environment.App, await environment.OpenChatAsync());
            var viewModel = window.ViewModel!;
            window.Show();
            Dispatcher.UIThread.RunJobs();

            viewModel.ShowSettingsCommand.Execute(null);
            var graphics = viewModel.Preferences.Sections.First(section => section.Section == SettingsSection.Graphics);
            graphics.SelectCommand!.Execute(graphics);
            Dispatcher.UIThread.RunJobs();
            Capture(window, "headless-settings-graphics");

            var adapters = viewModel.Preferences.GpuAdapters;
            // 首项始终是"自动"; 无 GPU 的 CI runner / 无驱动环境不会枚举出真实显卡。
            Assert.NotEmpty(adapters);
            Assert.Equal(GpuCatalog.AutoAdapter, adapters[0].Value);
            var realAdapters = adapters
                .Where(option => !string.Equals(option.Value, GpuCatalog.AutoAdapter, StringComparison.Ordinal))
                .Where(option => !option.Label.Contains("当前不可用", StringComparison.Ordinal))
                .ToList();
            if (OperatingSystem.IsWindows())
            {
                // 有真实显卡时只校验"非空且可区分"(有 PCI slot 用 slot, 无 slot 的虚拟卡回退显示名);
                // 具体映射(SelectionIdOf/LooksLikePciSlot)由 GpuPreferenceTests 覆盖。
                Assert.All(realAdapters, option => Assert.False(string.IsNullOrWhiteSpace(option.Value)));
                Assert.Equal(realAdapters.Count, realAdapters.Select(option => option.Value).Distinct(StringComparer.Ordinal).Count());
                if (realAdapters.Count == 0)
                    Console.WriteLine("未枚举到真实显卡, 仅校验\"自动\"项与保存链路。");
            }

            var target = adapters[^1];
            viewModel.Preferences.SelectedGpuAdapterOption = target;
            viewModel.Preferences.SaveGraphicsCommand.Execute(null);
            Assert.Equal(target.Value, viewModel.Gui.Load().GpuAdapter);
            Assert.Contains("显卡", viewModel.Preferences.Status, StringComparison.Ordinal);

            window.Close();
            Dispatcher.UIThread.RunJobs();
        });

    /** 1000 条消息的渲染冒烟: 只验证绑定集合到虚拟化列表的路径不因体量崩掉, 并记录耗时。 */
    [Fact]
    public async Task MessageList_RendersThousandMessages() => await HeadlessGui.RunAsync(async () =>
        {
            var environment = await GuiTestEnvironment.CreateAsync();
            await using var environmentScope = environment;
            var window = new MainWindow(environment.App, await environment.OpenChatAsync());
            var viewModel = window.ViewModel!;
            window.Show();
            Dispatcher.UIThread.RunJobs();

            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            for (var index = 0; index < BulkMessages; index++)
            {
                var kind = (index % 4) switch
                {
                    1 => MessageKind.Reasoning,
                    2 => MessageKind.Tool,
                    3 => MessageKind.Result,
                    _ => MessageKind.Assistant,
                };
                var role = kind == MessageKind.Assistant ? "助手" : "轨迹";
                viewModel.Messages.Add(new MessageViewModel(role, $"第 {index} 条内容", kind, false));
            }
            Dispatcher.UIThread.RunJobs();
            var elapsed = stopwatch.Elapsed;
            Capture(window, "headless-1000-messages");

            output.WriteLine($"追加 {BulkMessages} 条消息并完成渲染耗时 {elapsed.TotalMilliseconds:0} ms");
            Assert.True(elapsed < TimeSpan.FromSeconds(15), $"1000 条消息渲染耗时 {elapsed.TotalSeconds:0.0}s, 超过冒烟上限");
            Assert.Equal(BulkMessages, viewModel.Messages.Count);

            window.Close();
            Dispatcher.UIThread.RunJobs();
        });


    /** 拖动字号滑块实时预览(不落盘): FontSizeText 跟随 + App 资源更新 + 磁盘值不变。 */
    [Fact]
    public async Task FontSizeSlider_PreviewsLive_WithoutPersisting() => await HeadlessGui.RunAsync(async () =>
    {
        var environment = await GuiTestEnvironment.CreateAsync();
        await using var environmentScope = environment;
        var window = new MainWindow(environment.App, await environment.OpenChatAsync());
        var viewModel = window.ViewModel!;
        window.Show();
        Dispatcher.UIThread.RunJobs();
        try
        {
            var preferences = viewModel.Preferences;
            Assert.Equal("13.5", preferences.FontSizeText);
            preferences.FontSize = 16.5;
            Dispatcher.UIThread.RunJobs();
            Assert.Equal("16.5", preferences.FontSizeText);
            Assert.Equal(16.5, Assert.IsType<double>(Application.Current!.Resources["FontSize.Body"]));
            Assert.Equal(13.5, new GuiSettings(environment.App.Home).Load().FontSize);
        }
        finally
        {
            window.Close();
            Dispatcher.UIThread.RunJobs();
        }
    });

    /** 聊天正文是构建期字号, 必须随 App 资源变化重建(旧实现硬编码常量, 永不跟随)。 */
    [Fact]
    public async Task MarkdownView_Rebuilds_OnFontSizeResourceChange() => await HeadlessGui.RunAsync(() =>
        {
        var app = Application.Current!;
        var markdown = new MarkdownView { Text = "# 标题\n\n正文" };
        var window = new Window { Content = markdown };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        try
        {
            app.Resources["FontSize.Body"] = 13.5;
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(20, LargestFont(markdown));
            app.Resources["FontSize.Body"] = 27.0;
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(40, LargestFont(markdown));
        }
        finally
        {
            app.Resources["FontSize.Body"] = 13.5;
            window.Close();
            Dispatcher.UIThread.RunJobs();
        }
    });

    /** 真实点击子代理浮层里的条目: 验证 XAML Click 处理器到只读视图的整条接线。 */
    [Fact]
    public async Task SubagentChip_ClickItem_OpensReadOnlyView() => await HeadlessGui.RunAsync(async () =>
    {
        // 环境组合必须离开 UI 线程(Task.Run): 在 headless dispatcher 上组合会拖垮后续的 MainWindow 测试。
        var environment = await GuiTestEnvironment.CreateAsync();
        await using var environmentScope = environment;
        var store = environment.App.Ctx.Get<SessionStore>(SessionStore.ServiceName)!;
        SubagentTestData.AddChild(store, environment.Agent.Id, "headless-sub", createdAt: 1, withToolCall: true);
        var window = new MainWindow(environment.App, await environment.OpenChatAsync());
        var viewModel = window.ViewModel!;
        window.Show();
        viewModel.ShowChatCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();
        await viewModel.Subagents.RefreshAsync();
        Flyout? flyout = null;
        try
        {
            Assert.True(viewModel.Subagents.HasNodes);
            var chip = window.GetVisualDescendants().OfType<Button>()
                .First(button => button.Flyout is Flyout
                    && button.GetVisualDescendants().OfType<TextBlock>().Any(text => text.Text == "子代理"));
            flyout = (Flyout)chip.Flyout!;
            flyout.ShowAt(chip);
            Dispatcher.UIThread.RunJobs();

            var item = ((StackPanel)flyout.Content!).GetVisualDescendants().OfType<Button>()
                .First(button => button.DataContext is SubagentNodeViewModel);
            item.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await GuiTestEnvironment.WaitForAsync(viewModel.Subagents, () => viewModel.Subagents.IsViewing);

            Assert.True(viewModel.Subagents.IsViewing);
            Assert.NotNull(viewModel.Subagents.Viewing);
            Assert.NotEmpty(viewModel.Subagents.Viewing!.Messages);
        }
        finally
        {
            flyout?.Hide();
            window.Close();
            Dispatcher.UIThread.RunJobs();
        }
    });

    /** diff 卡片真实渲染截图(阶段 4): 折叠态与展开态各一张, 供与参考图 artifacts020/bugs/7925849368c492dfb053dda75d5b683f.png 对照。 */
    [Fact]
    public async Task DiffCard_RendersCollapsedAndExpanded() => await HeadlessGui.RunAsync(async () =>
        {
            var environment = await GuiTestEnvironment.CreateAsync();
            await using var environmentScope = environment;
            var window = new MainWindow(environment.App, await environment.OpenChatAsync());
            var viewModel = window.ViewModel!;
            window.Show();
            viewModel.ShowChatCommand.Execute(null);
            Dispatcher.UIThread.RunJobs();

            var before = string.Join('\n', Enumerable.Range(0, 12).Select(index => $"line {index}")) + "\n";
            var after = before.Replace("line 3\n", "line 3 changed\n").Replace("line 7\n", "line 7 changed\n") + "added tail\n";
            var card = new DiffCard("edit src/sample.cs", "pairs", DiffCardExtractor.DiffLines(before, after));
            var message = new MessageViewModel("结果", before, MessageKind.Result, false);
            message.SetDiff(card);
            viewModel.Messages.Add(message);
            Dispatcher.UIThread.RunJobs();

            var diffView = window.GetVisualDescendants().OfType<DiffView>().Single();
            message.IsExpanded = false;
            Dispatcher.UIThread.RunJobs();
            Assert.True(message.ShowPreview);
            Capture(window, "headless-diff-card-collapsed");

            message.IsExpanded = true;
            Dispatcher.UIThread.RunJobs();
            Assert.Contains(diffView.GetVisualDescendants().OfType<TextBlock>(), block => block.Text == "+3");
            Assert.Contains(diffView.GetVisualDescendants().OfType<TextBlock>(), block => block.Text == "-2");
            Assert.Contains(diffView.GetVisualDescendants().OfType<TextBlock>(), block => block.Text == "line 3 changed");
            Capture(window, "headless-diff-card-expanded");

            // 卡片内 chevron: 点击只隐藏 diff 行, 标题与计数保留
            var chevron = diffView.GetVisualDescendants().OfType<Button>().Single();
            chevron.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Dispatcher.UIThread.RunJobs();
            Assert.False(message.IsDiffExpanded);
            Assert.Contains(diffView.GetVisualDescendants().OfType<TextBlock>(), block => block.Text == "+3");
            var changedLine = diffView.GetVisualDescendants().OfType<TextBlock>().FirstOrDefault(block => block.Text == "line 3 changed");
            Assert.NotNull(changedLine);
            Assert.False(changedLine.IsEffectivelyVisible);
            Capture(window, "headless-diff-card-chevron-collapsed");

            chevron.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Dispatcher.UIThread.RunJobs();
            Assert.True(message.IsDiffExpanded);
            Assert.True(changedLine.IsEffectivelyVisible);

            window.Close();
            Dispatcher.UIThread.RunJobs();
        });

    /** @引用补全只看光标处的未完成 token: 选中候选或继续打提示词之后, 提示词都要保住并且能整段发送。 */
    [Fact]
    public async Task ComposerMentionMenu_ClosesOncePromptFollowsTheReference() => await HeadlessGui.RunAsync(async () =>
        {
            var environment = await GuiTestEnvironment.CreateIsolatedAsync();
            await using var environmentScope = environment;
            var cwd = environment.Agent.Session.Header.Cwd ?? throw new InvalidOperationException("session has no cwd");
            await File.WriteAllTextAsync(Path.Combine(cwd, "notes.md"), "见附图说明", TestContext.Current.CancellationToken);
            var window = new MainWindow(environment.App, await environment.OpenChatAsync());
            var viewModel = window.ViewModel!;
            window.Show();
            viewModel.ShowChatCommand.Execute(null);
            Dispatcher.UIThread.RunJobs();
            var inputBox = window.GetVisualDescendants().OfType<ChatView>().Single().FindControl<TextBox>("InputBox")!;
            Assert.True(inputBox.Focus());

            // 选中引用候选后光标必须落在 token 之后, 继续打的提示词不能被清掉。
            window.KeyTextInput("@notes");
            Assert.Equal("@notes", viewModel.Composer.Input);
            Assert.True(viewModel.IsSuggestionOpen);
            Assert.Contains(viewModel.Suggestions, suggestion => suggestion.Label == "@notes.md");
            window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
            Assert.Equal("@notes.md ", viewModel.Composer.Input);
            Assert.Equal(viewModel.Composer.Input.Length, inputBox.CaretIndex);
            Assert.False(viewModel.IsSuggestionOpen);

            window.KeyTextInput("请修改这个文件");
            Assert.Equal("@notes.md 请修改这个文件", viewModel.Composer.Input);
            Assert.False(viewModel.IsSuggestionOpen);
            window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
            if (viewModel.SubmitCommand.ExecutionTask is { } submission)
                await GuiTestEnvironment.PumpAsync(submission);
            Assert.Equal("", viewModel.Composer.Input);
            await GuiTestEnvironment.WaitForAsync(viewModel.Messages,
                () => viewModel.Messages.Any(message => message.Kind == MessageKind.User
                    && message.Text.Contains("[file: notes.md]", StringComparison.Ordinal)
                    && message.Text.Contains("请修改这个文件", StringComparison.Ordinal)),
                () => GuiTestEnvironment.DescribeChatState(viewModel));

            window.Close();
            Dispatcher.UIThread.RunJobs();
        });

    private static double LargestFont(Visual root)
        => root.GetVisualDescendants().OfType<TextBlock>().Max(block => block.FontSize);


    private static void Capture(Window window, string name, double height = 0)
    {
        try
        {
            if (height > 0)
            {
                // headless 窗口不按 SizeToContent 调整客户区, 截图时固定高度以便看到完整弹窗。
                window.SizeToContent = SizeToContent.Manual;
                window.Height = height;
                Dispatcher.UIThread.RunJobs();
            }
            Directory.CreateDirectory(ScreenshotDirectory);
            using var frame = window.CaptureRenderedFrame();
            frame?.Save(Path.Combine(ScreenshotDirectory, $"{name}.png"), PngBitmapEncoderOptions.Default);
        }
        catch (Exception error)
        {
            Console.Error.WriteLine($"screenshot {name} failed: {error.Message}");
        }
    }
}
