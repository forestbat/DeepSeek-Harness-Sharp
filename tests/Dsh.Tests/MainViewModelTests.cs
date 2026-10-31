using System.Collections.Specialized;
using Avalonia.Threading;
using Dsh.Boot;
using Dsh.Compaction;
using Dsh.Core;
using Dsh.Gui.Services;
using Dsh.Gui.ViewModels;
using Dsh.Interaction;
using Dsh.Llm;

namespace Dsh.Tests;

/** 主窗口视图模型的行为: 页面/标签切换、会话分组、命令通道、轨迹选中。 */
[Collection(GuiSerialCollection.CollectionName)]
public sealed class MainViewModelTests
{
    [Fact]
    public async Task PageTabAndSidebarCommands_SwitchState() => await HeadlessGui.RunAsync(async () =>
    {
        await using var environment = await GuiTestEnvironment.CreateIsolatedAsync();
        await using var viewModel = new MainViewModel(environment.App, await environment.OpenChatAsync());

        Assert.True(viewModel.IsDashboardPage);
        Assert.False(viewModel.IsChatPage);
        viewModel.ShowSettingsCommand.Execute(null);
        Assert.True(viewModel.IsSettingsPage);
        viewModel.GoBackCommand.Execute(null);
        Assert.True(viewModel.IsDashboardPage);
        viewModel.ShowMarketCommand.Execute(null);
        Assert.True(viewModel.IsMarketPage);
        viewModel.ShowChatCommand.Execute(null);
        Assert.True(viewModel.IsChatPage);
        viewModel.GoBackCommand.Execute(null);
        Assert.True(viewModel.IsChatPage);
        viewModel.ShowDashboardCommand.Execute(null);
        Assert.True(viewModel.IsDashboardPage);

        Assert.False(viewModel.IsTraceTab);
        viewModel.ShowTraceCommand.Execute(null);
        Assert.True(viewModel.IsTraceTab);
        viewModel.ShowConversationCommand.Execute(null);
        Assert.False(viewModel.IsTraceTab);

        Assert.True(viewModel.IsSidebarVisible);
        viewModel.ToggleSidebarCommand.Execute(null);
        Assert.False(viewModel.IsSidebarVisible);
        viewModel.ToggleSidebarCommand.Execute(null);
        Assert.True(viewModel.IsSidebarVisible);
    });

    [Fact]
    public async Task AttachImage_AddsAttachmentAndSubmitsImageBlock() => await HeadlessGui.RunAsync(async () =>
    {
        await using var environment = await GuiTestEnvironment.CreateAsync();
        await using var viewModel = new MainViewModel(environment.App, await environment.OpenChatAsync());

        var png = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3 };
        viewModel.AttachImage("shot.png", png, 4, 2);

        var attachment = Assert.Single(viewModel.Attachments);
        Assert.True(viewModel.HasAttachments);
        Assert.Equal("shot.png", attachment.Name);

        viewModel.Composer.Input = "look at this";
        await viewModel.SubmitCommand.ExecuteAsync(null);
        await GuiTestEnvironment.WaitForAsync(viewModel.Messages,
            () => viewModel.Messages.Any(message => message.Kind == MessageKind.User),
            () => GuiTestEnvironment.DescribeChatState(viewModel));

        Assert.Empty(viewModel.Attachments);
        Assert.False(viewModel.HasAttachments);

        var userMessage = environment.Agent.Session.SnapshotEvents()
            .Select(sessionEvent => sessionEvent.Data)
            .OfType<UserMessagePayload>()
            .Select(payload => payload.Message)
            .LastOrDefault(message => message.Content.Any(block => block is ImageBlock));
        Assert.NotNull(userMessage);
        Assert.Contains(userMessage.Content, block => block is ImageBlock);
        Assert.Contains(userMessage.Content, block => block is TextBlock { Text: "look at this" });
    });

    [Fact]
    public async Task Prompt_DoesNotAttachImagesReferencedInsideTextFiles() => await HeadlessGui.RunAsync(async () =>
    {
        await using var environment = await GuiTestEnvironment.CreateAsync();
        await using var viewModel = new MainViewModel(environment.App, await environment.OpenChatAsync());
        var cwd = environment.Agent.Session.Header.Cwd;
        Assert.NotNull(cwd);
        var image = Path.Combine(cwd, "shot.png");
        await File.WriteAllBytesAsync(image, [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3], TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(
            Path.Combine(cwd, "notes.md"),
            "见 shot.png 附图",
            TestContext.Current.CancellationToken);

        viewModel.Composer.Input = "read @notes.md";
        await viewModel.SubmitCommand.ExecuteAsync(null);
        static bool HasPrompt(UserMessagePayload payload)
            => payload.Message.Content.Any(block => block is TextBlock text && text.Text.Contains("shot.png", StringComparison.Ordinal));
        await GuiTestEnvironment.WaitForAsync(
            viewModel.Messages,
            () => environment.Agent.Session.SnapshotEvents()
                .Select(sessionEvent => sessionEvent.Data)
                .OfType<UserMessagePayload>()
                .Any(HasPrompt),
            () => GuiTestEnvironment.DescribeChatState(viewModel));

        var message = environment.Agent.Session.SnapshotEvents()
            .Select(sessionEvent => sessionEvent.Data)
            .OfType<UserMessagePayload>()
            .Last(HasPrompt).Message;
        Assert.DoesNotContain(message.Content, block => block is ImageBlock);
    });

    [Fact]
    public async Task OpenSession_FromSettingsOrMarket_ReturnsToChatPage() => await HeadlessGui.RunAsync(async () =>
    {
        await using var environment = await GuiTestEnvironment.CreateAsync();
        await using var viewModel = new MainViewModel(environment.App, await environment.OpenChatAsync());
        var node = viewModel.Workspaces.SelectMany(workspace => workspace.Sessions).First();

        viewModel.ShowSettingsCommand.Execute(null);
        Assert.True(viewModel.IsSettingsPage);
        viewModel.OpenSessionCommand.Execute(node);
        Assert.True(viewModel.IsChatPage);
        Assert.Same(node, viewModel.SelectedSession);

        viewModel.ShowMarketCommand.Execute(null);
        Assert.True(viewModel.IsMarketPage);
        viewModel.OpenSessionCommand.Execute(node);
        Assert.True(viewModel.IsChatPage);
    });

    [Fact]
    public async Task NewSession_FromSettingsPage_ReturnsToChatPage() => await HeadlessGui.RunAsync(async () =>
    {
        await using var environment = await GuiTestEnvironment.CreateAsync();
        await using var viewModel = new MainViewModel(environment.App, await environment.OpenChatAsync());
        viewModel.ShowSettingsCommand.Execute(null);

        viewModel.NewSessionCommand.Execute(null);

        await GuiTestEnvironment.WaitForAsync(viewModel,
            () => viewModel.SelectedSession is { } selected && selected.SessionId != environment.Agent.Id);
        Assert.True(viewModel.IsChatPage);
        Assert.NotNull(viewModel.SelectedSession);
        Assert.NotEqual(environment.Agent.Id, viewModel.SelectedSession!.SessionId);
    });

    [Fact]
    public async Task Sessions_GroupedByWorkspace_AndMarkCurrentSessionSelected() => await HeadlessGui.RunAsync(async () =>
    {
        await using var environment = await GuiTestEnvironment.CreateAsync();
        await using var viewModel = new MainViewModel(environment.App, await environment.OpenChatAsync());

        var workspace = Assert.Single(viewModel.Workspaces);
        Assert.NotEmpty(workspace.Name);
        var node = Assert.Single(workspace.Sessions);
        Assert.Equal(environment.Agent.Id, node.SessionId);
        Assert.Same(node, viewModel.SelectedSession);
        Assert.True(node.IsSelected);
        Assert.True(node.IsLive);
    });

    /** 侧栏"运行中"指示由后台会话的 TurnStart/TurnEnd 驱动, 不限于当前显示会话。 */
    [Fact]
    public async Task SessionNode_TracksRunningState_FromTurnEvents() => await HeadlessGui.RunAsync(async () =>
    {
        var environment = await GuiTestEnvironment.CreateAsync();
        await using var environmentScope = environment;
        await using var viewModel = new MainViewModel(environment.App, await environment.OpenChatAsync());
        var session = environment.Agent.Session;
        var node = viewModel.Workspaces.SelectMany(workspace => workspace.Sessions).First(item => item.SessionId == session.Id);
        Assert.False(node.IsRunning);

        await Task.Run(() => session.Append(new TurnStartPayload(1)), TestContext.Current.CancellationToken);
        await GuiTestEnvironment.WaitForAsync(viewModel, () => viewModel.IsBusy);
        Assert.True(FindNode(viewModel, session.Id)!.IsRunning);

        await Task.Run(() => session.Append(new TurnEndPayload(1, new TurnEndReason.Completed())), TestContext.Current.CancellationToken);
        await GuiTestEnvironment.WaitForAsync(viewModel, () => !viewModel.IsBusy);
        Assert.False(FindNode(viewModel, session.Id)!.IsRunning);
    });

    /** 交付物清单: 打开会话时从日志回放, 之后跟随新声明与会话截断。 */
    [Fact]
    public async Task Deliverables_ReplayFromLog_ThenFollowLiveEventsAndTruncation() => await HeadlessGui.RunAsync(async () =>
    {
        var environment = GuiTestEnvironment.CreateBlocking();
        await using var environmentScope = environment;
        var session = environment.Agent.Session;
        var report = Path.Combine(session.Header.Cwd!, "report.docx");
        var data = Path.Combine(session.Header.Cwd!, "data.xlsx");
        await File.WriteAllTextAsync(report, "report", TestContext.Current.CancellationToken);
        session.Append(new DeliverablesPresentedPayload(1, ToolCallId.Create("call-1"), [new PresentedFile(report, "报告")]));
        await using var viewModel = new MainViewModel(environment.App, await environment.OpenChatAsync());

        var replayed = Assert.Single(viewModel.Deliverables.Items);
        Assert.Equal((report, "报告", true), (replayed.Path, replayed.Description, replayed.Exists));
        Assert.Equal("1", viewModel.Deliverables.Badge);
        Assert.Contains(viewModel.TraceItems, item => item.Title == "交付物 1 个");

        var cut = session.Seq;
        session.Append(new DeliverablesPresentedPayload(1, ToolCallId.Create("call-2"), [new PresentedFile(data)]));
        await GuiTestEnvironment.WaitForAsync(viewModel.Deliverables.Items, () => viewModel.Deliverables.Items.Count == 2);
        Assert.False(viewModel.Deliverables.Items[1].Exists);

        Assert.True(session.TryTruncateTo(cut));
        await GuiTestEnvironment.WaitForAsync(viewModel.Deliverables.Items, () => viewModel.Deliverables.Items.Count == 1);
        Assert.Equal(report, Assert.Single(viewModel.Deliverables.Items).Path);
    });


    [Fact]
    public async Task SnapshotReplay_MapsEventsToMessagesAndTrace() => await HeadlessGui.RunAsync(async () =>
    {
        await using var environment = await GuiTestEnvironment.CreateAsync();
        var session = environment.Agent.Session;
        session.Append(new UserMessagePayload(MessageFactory.CreateUserText("你好")), new SurfaceOp.Append());
        session.Append(new TurnStartPayload(1));
        session.Append(new RequestContextPayload("test", "test-model"));
        session.Append(new ToolCallPayload(1, 1, ToolCallId.Create("call-1"), "bash", """{"command":"ls"}"""));
        session.Append(new TurnEndPayload(1, new TurnEndReason.Completed()));

        await using var viewModel = new MainViewModel(environment.App, await environment.OpenChatAsync());

        Assert.Collection(
            viewModel.Messages,
            message => Assert.Equal(MessageKind.User, message.Kind),
            message => Assert.Equal(MessageKind.Tool, message.Kind));
        Assert.Contains(viewModel.TraceItems, item => item.Kind == TraceKind.Turn);
        Assert.Contains(viewModel.TraceItems, item => item.Kind == TraceKind.Context);
        Assert.Contains(viewModel.TraceItems, item => item.Kind == TraceKind.Tool);
        Assert.False(viewModel.IsBusy);
    });

    /** 运行时上下文注入只应出现一行: 请求元数据(request/context)不重复进正文, 插件名也不该再补一个 @。 */
    [Fact]
    public async Task PluginInjectedContext_RendersSingleRow() => await HeadlessGui.RunAsync(async () =>
    {
        await using var environment = await GuiTestEnvironment.CreateAsync();
        var session = environment.Agent.Session;
        session.Append(
            new UserMessagePayload(MessageFactory.CreateUserMessage(
                [new TextBlock("runtime context")],
                new PluginMessageSource("@deepseek-ai/dsh-system-prompt"))),
            new SurfaceOp.Append());
        session.Append(new RequestContextPayload("openai-compatible", "deepseek-v4-flash"));

        await using var viewModel = new MainViewModel(environment.App, await environment.OpenChatAsync());

        var message = Assert.Single(viewModel.Messages);
        Assert.Equal(MessageKind.Context, message.Kind);
        Assert.Equal("上下文注入 · @deepseek-ai/dsh-system-prompt", message.Text);
        Assert.False(message.HasRole);
        Assert.Contains(viewModel.TraceItems, item => item.Title.StartsWith("请求上下文", StringComparison.Ordinal));
    });

    [Fact]
    public async Task SelectTraceItem_MarksSelectionAndRequestsScroll() => await HeadlessGui.RunAsync(async () =>
    {
        await using var environment = await GuiTestEnvironment.CreateAsync();
        var session = environment.Agent.Session;
        session.Append(new UserMessagePayload(MessageFactory.CreateUserText("你好")), new SurfaceOp.Append());
        session.Append(new TurnStartPayload(1));
        await using var viewModel = new MainViewModel(environment.App, await environment.OpenChatAsync());

        MessageViewModel? scrolled = null;
        viewModel.ScrollRequested += message => scrolled = message;
        var trace = viewModel.TraceItems.First(item => item.Kind == TraceKind.Turn);
        viewModel.SelectTraceItemCommand.Execute(trace);

        Assert.True(trace.IsSelected);
        Assert.Same(viewModel.Messages[^1], scrolled);
    });

    [Fact]
    public async Task Submit_SlashCommand_RunsThroughCommandsService() => await HeadlessGui.RunAsync(async () =>
    {
        await using var environment = await GuiTestEnvironment.CreateAsync();
        await using var viewModel = new MainViewModel(environment.App, await environment.OpenChatAsync());

        viewModel.Composer.Input = "/plugins";
        await viewModel.SubmitCommand.ExecuteAsync(null);

        Assert.Equal("", viewModel.Composer.Input);
        var message = Assert.Single(viewModel.Messages);
        Assert.Equal(MessageKind.System, message.Kind);
        Assert.Contains("dsh-", message.Text);
    });

    /**
     * 压缩必须有自己的进度行与忙碌状态。摘要要发若干次模型请求, 一次压缩可能持续数分钟;
     * 界面完全没反应时用户只会再按一次 /compact, 而那一次必然被"压缩已在进行"挡回来。
     * 这里走"重新加载会话"这条确定性路径, 覆盖与实时流相同的渲染代码(ApplyEvents)。
     */
    [Fact]
    public async Task Compaction_ShowsProgressThenSummaryInsteadOfStayingSilent() => await HeadlessGui.RunAsync(async () =>
    {
        await using var environment = await GuiTestEnvironment.CreateIsolatedAsync();
        var chat = await environment.OpenChatAsync();
        var session = environment.Agent.Session;
        var compactionId = CompactionId.Create("cmp-gui");
        session.Append(new CompactionStartPayload(compactionId, null, null));
        session.Append(new CompactionSummaryPayload(
            compactionId,
            null,
            [new TextBlock("GUI 压缩摘要正文")],
            new ShadowedRange(0, 10),
            [0, 1, 2, 3],
            4321,
            "deepseek-official",
            "deepseek-v4-flash"));
        session.Append(new CompactionEndPayload(compactionId, null, null));
        await using var viewModel = new MainViewModel(environment.App, chat);
        // 走实时流(不是重新加载): 用户报的正是"压缩开始后界面没动静"这条路。
        await GuiTestEnvironment.WaitForAsync(
            (INotifyCollectionChanged)viewModel.Messages,
            () => viewModel.Messages.Any(message => message.Text.Contains("正在压缩上下文", StringComparison.Ordinal)),
            () => GuiTestEnvironment.DescribeChatState(viewModel));
        var summary = Assert.Single(viewModel.Messages, message =>
            message.Text.Contains("上下文已压缩", StringComparison.Ordinal));
        Assert.Contains("收起 4 条历史", summary.Text, StringComparison.Ordinal);
        Assert.Contains("GUI 压缩摘要正文", summary.Detail, StringComparison.Ordinal);
        // 成功的压缩结束后不能把界面留在忙碌态。
        Assert.False(viewModel.IsBusy);
    });

    /** 压缩检查点的正文已经由摘要行渲染过, 再当成上下文注入会让同一段摘要出现两次。 */
    [Fact]
    public async Task CompactionCheckpointMessage_IsNotRenderedTwiceAsContextInjection() => await HeadlessGui.RunAsync(async () =>
    {
        await using var environment = await GuiTestEnvironment.CreateIsolatedAsync();
        var chat = await environment.OpenChatAsync();
        var session = environment.Agent.Session;
        var compactionId = CompactionId.Create("cmp-gui-dupe");
        session.Append(new CompactionStartPayload(compactionId, null, null));
        session.Append(new CompactionSummaryPayload(
            compactionId, null, [new TextBlock("唯一摘要戊")], new ShadowedRange(0, 5), [0, 1], 100,
            "deepseek-official", "deepseek-v4-flash"));
        session.Append(
            new UserMessagePayload(new UserMessage
            {
                Id = MessageFactory.NewId(),
                Content = [new TextBlock("唯一摘要戊")],
                Source = CompactionCheckpoint.Source(compactionId),
            }),
            new SurfaceOp.Append());
        session.Append(new CompactionEndPayload(compactionId, null, null));
        await GuiTestEnvironment.PumpAsync(chat.ReloadAsync());
        await using var viewModel = new MainViewModel(environment.App, chat);

        Assert.Single(viewModel.Messages, message =>
            message.Text.Contains("唯一摘要戊", StringComparison.Ordinal)
            || message.Detail.Contains("唯一摘要戊", StringComparison.Ordinal));
    });
    [Fact]
    public async Task Submit_TextMessage_IsRenderedOnceFromSessionEvent() => await HeadlessGui.RunAsync(async () =>
        {
            var environment = await GuiTestEnvironment.CreateIsolatedAsync();
            await using var environmentScope = environment;
            await using var viewModel = new MainViewModel(environment.App, await environment.OpenChatAsync());

            viewModel.Composer.Input = "你好";
            await viewModel.SubmitCommand.ExecuteAsync(null);

            // Host events may arrive before the RPC reply; verify the eventual single rendered user message.
            await GuiTestEnvironment.WaitForAsync(viewModel.Messages,
                () => viewModel.Messages.Any(message => message.Kind == MessageKind.User),
                () => GuiTestEnvironment.DescribeChatState(viewModel));
            Assert.Single(viewModel.Messages, message => message.Kind == MessageKind.User);
            Assert.Equal("你好", viewModel.Messages[0].Text);
        });

    [Fact]
    public async Task Submit_EmptyInput_DoesNothing() => await HeadlessGui.RunAsync(async () =>
    {
        await using var environment = await GuiTestEnvironment.CreateAsync();
        await using var viewModel = new MainViewModel(environment.App, await environment.OpenChatAsync());

        viewModel.Composer.Input = "   ";
        await viewModel.SubmitCommand.ExecuteAsync(null);

        Assert.Empty(viewModel.Messages);
        Assert.False(viewModel.IsBusy);
    });

    [Fact]
    public async Task SettingsFacade_WritesSafetySettingsThroughSingleEntry() => await HeadlessGui.RunAsync(async () =>
    {
        await using var environment = await GuiTestEnvironment.CreateAsync();
        await using var viewModel = new MainViewModel(environment.App, await environment.OpenChatAsync());

        viewModel.Settings.UpdateSafety(autoApprove: true, ["bash: rm -rf /*"]);

        var reloaded = viewModel.Settings.Load();
        var safety = Assert.IsType<SafetySettings>(reloaded.Safety);
        Assert.True(safety.AutoApprove);
        Assert.Contains("bash: rm -rf /*", safety.Blacklist);
        Assert.True(File.Exists(viewModel.Settings.SettingsPath));
    });

    [Fact]
    public async Task RestoredSession_KeepsListedAndReplayable() => await HeadlessGui.RunAsync(async () =>
    {
        await using var environment = await GuiTestEnvironment.CreateIsolatedAsync();
        var sessionId = environment.Agent.Id;
        environment.Agent.Session.Append(new UserMessagePayload(MessageFactory.CreateUserText("第一条")), new SurfaceOp.Append());
        await environment.App.Ctx.Get<SessionStore>(SessionStore.ServiceName)!.Flush(environment.Agent.Session);

        await using var restarted = await environment.RestartAsync();
        await using var viewModel = new MainViewModel(restarted.App, await restarted.OpenChatAsync(sessionId));

        Assert.Collection(viewModel.Messages, message => Assert.Equal("第一条", message.Text));
        var nodes = viewModel.Workspaces.SelectMany(workspace => workspace.Sessions).ToList();
        var node = Assert.Single(nodes, candidate => candidate.SessionId == sessionId);
        Assert.Equal(sessionId, viewModel.CurrentSessionId);
        Assert.True(node.IsLive);
    });


    /** 内容命中: 标题/workspace 不匹配时靠 FTS 命中, 并带出摘要; 关闭搜索后复位。 */
    [Fact]
    public async Task SessionSearch_ContentHit_CarriesSnippet_AndResetsOnClose() => await HeadlessGui.RunAsync(async () =>
    {
        var environment = await GuiTestEnvironment.CreateAsync();
        await using var environmentScope = environment;
        await using var viewModel = new MainViewModel(environment.App, await environment.OpenChatAsync());
        environment.Agent.Session.Append(
            new UserMessagePayload(MessageFactory.CreateUserText("unique-platypus-token")), new SurfaceOp.Append());
        await environment.App.Ctx.Get<SessionStore>(SessionStore.ServiceName)!.Flush(environment.Agent.Session);

        Assert.False(viewModel.IsSearchOpen);
        viewModel.ToggleSearchCommand.Execute(null);
        Assert.True(viewModel.IsSearchOpen);
        viewModel.SearchText = "platypus";

        await GuiTestEnvironment.WaitForAsync(viewModel.Workspaces,
            () => FindNode(viewModel, environment.Agent.Id) is { HasMatchSnippet: true });
        var node = FindNode(viewModel, environment.Agent.Id);

        Assert.NotNull(node);
        Assert.Contains("platypus", node.MatchSnippet);

        viewModel.ToggleSearchCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();
        Assert.False(viewModel.IsSearchOpen);
        Assert.Equal("", viewModel.SearchText);
        Assert.True(FindNode(viewModel, environment.Agent.Id) is { HasMatchSnippet: false });
    });

    private static SessionNodeViewModel? FindNode(MainViewModel viewModel, SessionId id)
        => viewModel.Workspaces.SelectMany(group => group.Sessions).FirstOrDefault(node => node.SessionId == id)
            ?? viewModel.FileSystemNodes.SelectMany(group => group.Sessions).FirstOrDefault(node => node.SessionId == id);

    [Fact]
    public async Task ApprovalRequest_ReachesUnifiedDecisionWindow() => await HeadlessGui.RunAsync(async () =>
        {
            var environment = await GuiTestEnvironment.CreateAsync();
            await using var environmentScope = environment;
            await using var viewModel = new MainViewModel(environment.App, await environment.OpenChatAsync());
            DecisionViewModel? seen = null;
            viewModel.DecisionRequested += decision =>
            {
                seen = decision;
                return Task.FromResult<object?>(ApprovalOutcome.AllowedOnce);
            };
            var approval = environment.App.Ctx.Get<ApprovalService>(ApprovalService.ServiceName)!;
            environment.Agent.Session.Append(new TurnStartPayload(1));

            var request = approval.Request(
                new ApprovalRequest(environment.Agent, "bash", ToolCallId.Create("call-1"), null, """{"command":"ls -la"}"""),
                default);
            var outcome = await PumpAsync(request);

            Assert.Equal(ApprovalOutcome.AllowedOnce, outcome);
            Assert.NotNull(seen);
            Assert.True(seen.IsApproval);
            Assert.Equal("bash", seen.ToolName);
            Assert.Equal("ls -la", seen.Command);
        });

    [Fact]
    public async Task UserQuestion_ReachesUnifiedDecisionWindow() => await HeadlessGui.RunAsync(async () =>
        {
            var environment = await GuiTestEnvironment.CreateAsync();
            await using var environmentScope = environment;
            await using var viewModel = new MainViewModel(environment.App, await environment.OpenChatAsync());
            viewModel.DecisionRequested += decision => Task.FromResult<object?>(new AskUserQuestionAnswer(
                [new AskUserQuestionAnswerItem(decision.Questions[0].Item.Id, ["继续"])]));
            var questions = environment.App.Ctx.Get<UserQuestionService>(UserQuestionService.ServiceName)!;

            var ask = questions.Ask(new AskUserQuestionRequest(
                [new AskUserQuestionItem("q1", "继续吗？", Options: [new AskUserQuestionOption("继续"), new AskUserQuestionOption("停下")])],
                environment.Agent));
            var answer = await PumpAsync(ask);

            Assert.Equal("继续", Assert.Single(answer.Answers).Selected[0]);
        });

    [Fact]
    public async Task TraceFilter_NarrowsTraceView_WithoutDroppingItems() => await HeadlessGui.RunAsync(async () =>
    {
        await using var environment = await GuiTestEnvironment.CreateAsync();
        var session = environment.Agent.Session;
        session.Append(new UserMessagePayload(MessageFactory.CreateUserText("你好")), new SurfaceOp.Append());
        session.Append(new TurnStartPayload(1));
        session.Append(new RequestContextPayload("test", "test-model"));
        await using var viewModel = new MainViewModel(environment.App, await environment.OpenChatAsync());

        var filter = viewModel.TraceFilters.First(candidate => candidate.Kind == TraceKind.Context);
        viewModel.SelectTraceFilterCommand.Execute(filter);

        Assert.All(viewModel.TraceView, item => Assert.Equal(TraceKind.Context, item.Kind));
        Assert.Contains(viewModel.TraceItems, item => item.Kind == TraceKind.Turn);
    });

    [Fact]
    public async Task SearchText_FiltersSessions() => await HeadlessGui.RunAsync(async () =>
    {
        await using var environment = await GuiTestEnvironment.CreateAsync();
        await using var viewModel = new MainViewModel(environment.App, await environment.OpenChatAsync());
        Assert.NotEmpty(viewModel.Workspaces);

        viewModel.SearchText = "不存在的关键词";

        Assert.Empty(viewModel.Workspaces);
        viewModel.SearchText = "";
        Assert.NotEmpty(viewModel.Workspaces);
    });

    [Fact]
    public async Task Suggestions_OfferCommandsAndSessionMentions() => await HeadlessGui.RunAsync(async () =>
    {
        await using var environment = await GuiTestEnvironment.CreateIsolatedAsync();
        await using var viewModel = new MainViewModel(environment.App, await environment.OpenChatAsync());

        viewModel.Composer.Input = "/mo";

        Assert.True(viewModel.IsSuggestionOpen);
        Assert.Contains(viewModel.Suggestions, suggestion => suggestion.Label == "/model");

        viewModel.Composer.Input = $"@{environment.Agent.Id.Value[..6]}";
        viewModel.Composer.CaretIndex = viewModel.Composer.Input.Length;

        Assert.True(viewModel.IsSuggestionOpen);
        Assert.Contains(viewModel.Suggestions, suggestion => suggestion.Label == $"@{environment.Agent.Id.Value}");
    });

    /** 引用补全只看光标处的未完成 @token: 引用之后继续打提示词, 菜单必须收起。 */
    [Fact]
    public async Task MentionSuggestions_CloseOncePromptFollowsTheReference() => await HeadlessGui.RunAsync(async () =>
    {
        await using var environment = await GuiTestEnvironment.CreateIsolatedAsync();
        await using var viewModel = new MainViewModel(environment.App, await environment.OpenChatAsync());
        await WriteReferencedFileAsync(environment, "notes.md", "见附图说明");

        viewModel.Composer.Input = "@notes";
        viewModel.Composer.CaretIndex = viewModel.Composer.Input.Length;

        Assert.True(viewModel.IsSuggestionOpen);
        Assert.Contains(viewModel.Suggestions, suggestion => suggestion.Label == "@notes.md");

        viewModel.Composer.Input = "@notes.md 请修改这个文件";
        viewModel.Composer.CaretIndex = viewModel.Composer.Input.Length;

        Assert.False(viewModel.IsSuggestionOpen);
        Assert.Empty(viewModel.Suggestions);
    });

    /** 选中引用候选只替换光标处的 token, 用户已经打在 token 之后的提示词必须保留。 */
    [Fact]
    public async Task ConfirmMention_KeepsPromptTypedAfterToken() => await HeadlessGui.RunAsync(async () =>
    {
        await using var environment = await GuiTestEnvironment.CreateIsolatedAsync();
        await using var viewModel = new MainViewModel(environment.App, await environment.OpenChatAsync());
        await WriteReferencedFileAsync(environment, "notes.md", "见附图说明");

        viewModel.Composer.Input = "@notes 请修改这个文件";
        viewModel.Composer.CaretIndex = "@notes".Length;

        var candidate = Assert.Single(viewModel.Suggestions, suggestion => suggestion.Label == "@notes.md");
        viewModel.ConfirmSuggestionCommand.Execute(candidate);

        Assert.Equal("@notes.md  请修改这个文件", viewModel.Composer.Input);
        Assert.Equal("@notes.md ".Length, viewModel.Composer.CaretIndex);
        Assert.False(viewModel.IsSuggestionOpen);
    });

    /** 引用之后接着打的提示词必须能整段发出, 不能被补全菜单吞掉。 */
    [Fact]
    public async Task Submit_KeepsPromptTypedAfterReference() => await HeadlessGui.RunAsync(async () =>
    {
        await using var environment = await GuiTestEnvironment.CreateIsolatedAsync();
        await using var viewModel = new MainViewModel(environment.App, await environment.OpenChatAsync());
        await WriteReferencedFileAsync(environment, "notes.md", "见附图说明");

        viewModel.Composer.Input = "参考 @notes.md 请修改这个文件";
        viewModel.Composer.CaretIndex = viewModel.Composer.Input.Length;
        Assert.False(viewModel.IsSuggestionOpen);

        await viewModel.SubmitCommand.ExecuteAsync(null);

        static bool HasPrompt(UserMessagePayload payload)
            => payload.Message.Content.Any(block =>
                block is TextBlock text && text.Text.Contains("请修改这个文件", StringComparison.Ordinal));
        await GuiTestEnvironment.WaitForAsync(
            viewModel.Messages,
            () => environment.Agent.Session.SnapshotEvents()
                .Select(sessionEvent => sessionEvent.Data)
                .OfType<UserMessagePayload>()
                .Any(HasPrompt),
            () => GuiTestEnvironment.DescribeChatState(viewModel));
        Assert.Equal("", viewModel.Composer.Input);
    });

    private static Task WriteReferencedFileAsync(GuiTestEnvironment environment, string name, string content)
    {
        var cwd = environment.Agent.Session.Header.Cwd ?? throw new InvalidOperationException("session has no cwd");
        return File.WriteAllTextAsync(Path.Combine(cwd, name), content, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task ConfirmSuggestion_PrefersClickedItem_OverKeyboardIndex() => await HeadlessGui.RunAsync(async () =>
    {
        await using var environment = await GuiTestEnvironment.CreateIsolatedAsync();
        await using var viewModel = new MainViewModel(environment.App, await environment.OpenChatAsync());
        viewModel.Composer.Input = "/";

        Assert.True(viewModel.IsSuggestionOpen);
        Assert.True(viewModel.Suggestions.Count > 1);
        var clicked = viewModel.Suggestions[1];
        viewModel.ConfirmSuggestionCommand.Execute(clicked);

        Assert.Equal(clicked.InsertText, viewModel.Composer.Input);

        viewModel.Composer.Input = "/";
        var first = viewModel.Suggestions[0];
        viewModel.ConfirmSuggestionCommand.Execute(null);

        Assert.Equal(first.InsertText, viewModel.Composer.Input);
    });

    [Fact]
    public async Task CommitRename_WritesThroughPersistence_AndSyncsTitle() => await HeadlessGui.RunAsync(async () =>
    {
        await using var environment = await GuiTestEnvironment.CreateAsync();
        await using var viewModel = new MainViewModel(environment.App, await environment.OpenChatAsync());
        var node = viewModel.Workspaces.SelectMany(workspace => workspace.Sessions).First();

        viewModel.BeginRenameCommand.Execute(node);
        node.RenameDraft = " 新名字 ";
        await viewModel.CommitRenameCommand.ExecuteAsync(node);

        var persistence = environment.App.Ctx.Get<ISessionPersistence>(ISessionPersistence.ServiceName)!;
        Assert.Equal("新名字", persistence.Stat(environment.Agent.Id)?.Header.Title);
        Assert.Equal("新名字", environment.Agent.Session.Header.Title);
        Assert.Equal("新名字", viewModel.SessionTitle);
        var refreshed = viewModel.Workspaces.SelectMany(workspace => workspace.Sessions).First();
        Assert.Equal("新名字", refreshed.Title);
    });

    [Fact]
    public async Task ModelLabels_FollowRequestHeaderPayload() => await HeadlessGui.RunAsync(async () =>
    {
        var environment = await GuiTestEnvironment.CreateAsync();
        await using var environmentScope = environment;
        await using var viewModel = new MainViewModel(environment.App, await environment.OpenChatAsync());
        Assert.Equal("/", viewModel.Composer.ModelLabel);
        Assert.Equal("推理", viewModel.Composer.ReasoningLabel);

        environment.Agent.Session.Append(new RequestHeaderPayload(
            new EpochHeader(new LlmCallConfig("other", "other-model", ReasoningEffortId.Create("high"))),
            RequestHeaderReasons.Change,
            true));
        await GuiTestEnvironment.WaitForAsync(viewModel.Composer, () => viewModel.Composer.ModelLabel == "other/other-model");

        Assert.Equal("other/other-model", viewModel.Composer.ModelLabel);
        Assert.Equal("other/other-model", viewModel.SessionSubtitle);
        Assert.Equal("high", viewModel.Composer.ReasoningLabel);
    });

    [Fact]
    public async Task SwitchCommand_Updates_Labels_For_Multi_Segment_Model() => await HeadlessGui.RunAsync(async () =>
    {
        await using var environment = await GuiTestEnvironment.CreateAsync("""
            logging:
              file: false
            providers:
              pa:
                type: openai-compatible
                options:
                  baseUrl: https://example.invalid
                  apiKey: sk-test
                models:
                  m1: {}
                  aion-labs/aion-3.0-mini: {}
            """);
        await using var viewModel = new MainViewModel(environment.App, await environment.OpenChatAsync());

        await viewModel.SwitchModelCommand.ExecuteAsync("pa/aion-labs/aion-3.0-mini");

        Assert.Equal("pa/aion-labs/aion-3.0-mini", viewModel.Composer.ModelLabel);
        Assert.Equal("pa/aion-labs/aion-3.0-mini", viewModel.SessionSubtitle);
    });

    [Fact]
    public async Task SwitchReasoningCommand_Updates_Label_For_Flagged_Model() => await HeadlessGui.RunAsync(async () =>
    {
        await using var environment = await GuiTestEnvironment.CreateIsolatedAsync("""
            logging:
              file: false
            providers:
              deepseek:
                type: openai-compatible
                options:
                  baseUrl: https://api.deepseek.com
                  apiKey: sk-test
                models:
                  deepseek-flash:
                    name: DeepSeek Flash
            """);
        await using var viewModel = new MainViewModel(environment.App, await environment.OpenChatAsync());
        environment.Agent.Session.Append(new RequestHeaderPayload(
            new EpochHeader(new LlmCallConfig("deepseek", "deepseek-flash", ReasoningEffortId.Create("max"))),
            RequestHeaderReasons.Change,
            true));

        await GuiTestEnvironment.WaitForAsync(viewModel.Composer, () => viewModel.Composer.ReasoningLabel == "max");
        Assert.Equal("max", viewModel.Composer.ReasoningLabel);
    });

    [Fact]
    public async Task AssistantMessages_AreLabeledWithCurrentModel() => await HeadlessGui.RunAsync(async () =>
    {
        await using var environment = await GuiTestEnvironment.CreateIsolatedAsync();
        var session = environment.Agent.Session;
        session.Append(new RequestHeaderPayload(
            new EpochHeader(new LlmCallConfig("other", "other-model")),
            RequestHeaderReasons.Change));
        session.Append(new AssistantMessagePayload(
            1,
            1,
            MessageFactory.CreateAssistantMessage([new TextBlock("回答")], "other", "other-model")),
            new SurfaceOp.Append());

        await using var viewModel = new MainViewModel(environment.App, await environment.OpenChatAsync());

        var assistant = Assert.Single(viewModel.Messages, message => message.Kind == MessageKind.Assistant);
        Assert.Equal("other-model", assistant.Role);
    });

    [Fact]
    public async Task WorkspaceView_SwitchPersistsIntoGuiParameters() => await HeadlessGui.RunAsync(async () =>
    {
        await using var environment = await GuiTestEnvironment.CreateAsync();
        await using var viewModel = new MainViewModel(environment.App, await environment.OpenChatAsync());

        viewModel.SetWorkspaceViewCommand.Execute("filesystem");

        Assert.True(viewModel.IsFileSystemView);
        Assert.Equal(GuiSettings.ViewFilesystem, viewModel.Gui.Load().WorkspaceView);
    });

    [Fact]
    public async Task DeleteCurrentSession_IsRefused() => await HeadlessGui.RunAsync(async () =>
    {
        await using var environment = await GuiTestEnvironment.CreateIsolatedAsync();
        await using var viewModel = new MainViewModel(environment.App, await environment.OpenChatAsync());
        var node = Assert.Single(viewModel.Workspaces.SelectMany(workspace => workspace.Sessions));

        await viewModel.DeleteSessionCommand.ExecuteAsync(node);

        Assert.Equal("不能删除当前正在使用的会话", viewModel.StatusText);
    });

    private static Task<T> PumpAsync<T>(Task<T> task) => GuiTestEnvironment.PumpAsync(task);
}
