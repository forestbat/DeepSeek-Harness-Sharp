using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Dsh.Core;
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

    [AvaloniaFact]
    public void MainWindow_RendersEveryPage()
    {
        var environment = CreateEnvironment();
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
    }

    [AvaloniaFact]
    public void DecisionDialog_RendersAndResolvesApproval()
    {
        var environment = CreateEnvironment();
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
    }

    [AvaloniaFact]
    public void DecisionDialog_RendersQuestion_AndSubmitsSelection()
    {
        var environment = CreateEnvironment();
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
    }

    [AvaloniaFact]
    public void ThemeSwitch_AppliesLightVariantToTheWholeApp()
    {
        var environment = CreateEnvironment();
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
    }

    [AvaloniaFact]
    public void SettingsGraphics_ListsAdaptersAndSaves()
    {
        var environment = CreateEnvironment();
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
            Assert.Contains(adapters, option => option.Value.Contains("780M", StringComparison.OrdinalIgnoreCase));
            Assert.Contains(adapters, option => option.Value.Contains("RTX 4060", StringComparison.OrdinalIgnoreCase));
        }

        var target = adapters[^1];
        viewModel.Preferences.SelectedGpuAdapterOption = target;
        viewModel.Preferences.SaveGraphicsCommand.Execute(null);
        Assert.Equal(target.Value, viewModel.Gui.Load().GpuAdapter);
        Assert.Contains("显卡", viewModel.Preferences.Status, StringComparison.Ordinal);

        window.Close();
        Dispatcher.UIThread.RunJobs();
    }

    /** 1000 条消息的渲染冒烟: 只验证绑定集合到虚拟化列表的路径不因体量崩掉, 并记录耗时。 */
    [AvaloniaFact]
    public void MessageList_RendersThousandMessages()
    {
        var environment = CreateEnvironment();
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
    }

    private static GuiTestEnvironment CreateEnvironment()
        => Task.Run(async () => await GuiTestEnvironment.CreateAsync()).GetAwaiter().GetResult();

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
