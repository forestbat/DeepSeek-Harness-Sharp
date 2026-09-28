using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
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
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "artifacts", "gui-screenshots"));

    [Fact]
    public async Task MainWindow_RendersEveryPage() => await HeadlessGui.RunAsync(async () =>
        {
            var environment = await GuiTestEnvironment.CreateAsync();
            using var environmentScope = environment;
            var window = new MainWindow(environment.App, environment.Agent);
            var viewModel = window.ViewModel!;
            window.Show();
            Dispatcher.UIThread.RunJobs();

            Capture(window, "headless-chat");
            Assert.True(viewModel.IsChatPage);
            Assert.True(window.GetVisualDescendants().OfType<ChatView>().Single().IsVisible);

            viewModel.ShowSettingsCommand.Execute(null);
            Dispatcher.UIThread.RunJobs();
            Capture(window, "headless-settings");
            var settings = window.GetVisualDescendants().OfType<SettingsPageView>().Single();
            Assert.True(settings.IsVisible);
            Assert.NotEmpty(settings.GetVisualDescendants());
            Assert.NotEmpty(viewModel.Preferences.Plugins);

            viewModel.ShowMarketCommand.Execute(null);
            Dispatcher.UIThread.RunJobs();
            Capture(window, "headless-market");
            Assert.True(window.GetVisualDescendants().OfType<MarketPageView>().Single().IsVisible);

            viewModel.ShowChatCommand.Execute(null);
            viewModel.ShowTraceCommand.Execute(null);
            Dispatcher.UIThread.RunJobs();
            Assert.True(viewModel.IsTraceTab);
            Capture(window, "headless-trace");

            window.Close();
            Dispatcher.UIThread.RunJobs();
        });

    [Fact]
    public async Task DecisionDialog_RendersAndResolvesApproval() => await HeadlessGui.RunAsync(async () =>
        {
            var environment = await GuiTestEnvironment.CreateAsync();
            using var environmentScope = environment;
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
            using var environmentScope = environment;
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
            single.SelectCommand.Execute(single.Options[1]);
            Assert.True(decision.CanSubmit);

            decision.SubmitCommand.Execute(null);
            Dispatcher.UIThread.RunJobs();
            var answer = Assert.IsType<AskUserQuestionAnswer>(decision.Result);
            Assert.Equal("全量", Assert.Single(answer.Answers).Selected[0]);
        });

    [Fact]
    public async Task ThemeSwitch_AppliesLightVariantToTheWholeApp() => await HeadlessGui.RunAsync(async () =>
        {
            var environment = await GuiTestEnvironment.CreateAsync();
            using var environmentScope = environment;
            var window = new MainWindow(environment.App, environment.Agent);
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
            using var environmentScope = environment;
            var window = new MainWindow(environment.App, environment.Agent);
            var viewModel = window.ViewModel!;
            window.Show();
            Dispatcher.UIThread.RunJobs();

            viewModel.ShowSettingsCommand.Execute(null);
            var graphics = viewModel.Preferences.Sections.First(section => section.Section == SettingsSection.Graphics);
            graphics.SelectCommand!.Execute(graphics);
            Dispatcher.UIThread.RunJobs();
            Capture(window, "headless-settings-graphics");

            var adapters = viewModel.Preferences.GpuAdapters;
            Assert.NotEmpty(adapters);
            Assert.Equal(GpuCatalog.AutoAdapter, adapters[0].Value);
            if (OperatingSystem.IsWindows())
            {
                // 本机是 AMD 780M + NVIDIA 4060 Laptop: 列表必须给出真实显卡而不是"渲染后端"。
                // 落盘值是 PCI slot(同名多卡唯一可区分), 卡名在 Label 里。
                var amd = adapters.First(option => option.Label.Contains("780M", StringComparison.OrdinalIgnoreCase));
                var nvidia = adapters.First(option => option.Label.Contains("RTX 4060", StringComparison.OrdinalIgnoreCase));
                Assert.True(GpuPreference.LooksLikePciSlot(amd.Value), $"AMD 卡的落盘值不是 PCI slot: {amd.Value}");
                Assert.True(GpuPreference.LooksLikePciSlot(nvidia.Value), $"NVIDIA 卡的落盘值不是 PCI slot: {nvidia.Value}");
                Assert.NotEqual(amd.Value, nvidia.Value);
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
            using var environmentScope = environment;
            var window = new MainWindow(environment.App, environment.Agent);
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

    /** 模拟点击 preset 浮层里的项: 验证 XAML Click 处理器到 /preset 命令管道的整条接线。 */
    [Fact]
    public async Task PresetChip_Click_Item_SwitchesPreset() => await HeadlessGui.RunAsync(async () =>
    {
        // 环境组合必须离开 UI 线程(Task.Run): 在 headless dispatcher 上组合会拖垮后续的窗口测试。
        var environment = await GuiTestEnvironment.CreateAsync();
        using var environmentScope = environment;
        var window = new MainWindow(environment.App, environment.Agent);
        var viewModel = window.ViewModel!;
        window.Show();
        Dispatcher.UIThread.RunJobs();
        try
        {
            Assert.Equal("标准", viewModel.PresetLabel);
            var chip = window.GetVisualDescendants().OfType<Button>()
                .First(button => button.Flyout is Flyout { Content: ItemsControl });
            var flyout = (Flyout)chip.Flyout!;
            flyout.ShowAt(chip);
            Dispatcher.UIThread.RunJobs();

            var item = ((ItemsControl)flyout.Content!).GetVisualDescendants().OfType<Button>()
                .First(button => button.DataContext is PresetListItem { Id: "minimal" });
            item.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

            for (var attempt = 0; attempt < 400 && viewModel.PresetLabel != "极简"; attempt++)
            {
                await Task.Delay(5, TestContext.Current.CancellationToken);
                Dispatcher.UIThread.RunJobs();
            }

            Assert.Equal("极简", viewModel.PresetLabel);
            Assert.Contains(environment.Agent.Session.SnapshotEvents(), sessionEvent => sessionEvent.Type == "preset/mode");
            var tools = environment.App.Ctx.Get<ToolRuntime>(ToolRuntime.ServiceName)!;
            var names = tools.Schemas(environment.Agent.ScopeKey).Select(schema => schema.Name).ToList();
            Assert.NotEmpty(names);
            Assert.All(names, name => Assert.Contains(name, new[] { "bash", "pwsh" }));
        }
        finally
        {
            window.Close();
            Dispatcher.UIThread.RunJobs();
        }
    });

    /** 拖动字号滑块实时预览(不落盘): FontSizeText 跟随 + App 资源更新 + 磁盘值不变。 */
    [Fact]
    public async Task FontSizeSlider_PreviewsLive_WithoutPersisting() => await HeadlessGui.RunAsync(async () =>
    {
        var environment = await GuiTestEnvironment.CreateAsync();
        using var environmentScope = environment;
        var window = new MainWindow(environment.App, environment.Agent);
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
        using var environmentScope = environment;
        var store = environment.App.Ctx.Get<SessionStore>(SessionStore.ServiceName)!;
        SubagentTestData.AddChild(store, environment.Agent.Id, "headless-sub", createdAt: 1, withToolCall: true);
        var window = new MainWindow(environment.App, environment.Agent);
        var viewModel = window.ViewModel!;
        window.Show();
        Dispatcher.UIThread.RunJobs();
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
            Dispatcher.UIThread.RunJobs();

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

    /** diff 卡片真实渲染截图(阶段 4): 折叠态与展开态各一张, 供与参考图 artifacts/bugs/7925849368c492dfb053dda75d5b683f.png 对照。 */
    [Fact]
    public async Task DiffCard_RendersCollapsedAndExpanded() => await HeadlessGui.RunAsync(async () =>
        {
            var environment = await GuiTestEnvironment.CreateAsync();
            using var environmentScope = environment;
            var window = new MainWindow(environment.App, environment.Agent);
            var viewModel = window.ViewModel!;
            window.Show();
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
