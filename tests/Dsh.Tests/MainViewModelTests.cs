using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Dsh.Boot;
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
    public async Task PageTabAndSidebarCommands_SwitchState()
    {
        using var environment = await GuiTestEnvironment.CreateAsync();
        using var viewModel = new MainViewModel(environment.App, environment.Agent);

        Assert.True(viewModel.IsChatPage);
        viewModel.ShowSettingsCommand.Execute(null);
        Assert.True(viewModel.IsSettingsPage);
        viewModel.ShowMarketCommand.Execute(null);
        Assert.True(viewModel.IsMarketPage);
        viewModel.ShowChatCommand.Execute(null);
        Assert.True(viewModel.IsChatPage);

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
    }

    [Fact]
    public async Task OpenSession_FromSettingsOrMarket_ReturnsToChatPage()
    {
        using var environment = await GuiTestEnvironment.CreateAsync();
        using var viewModel = new MainViewModel(environment.App, environment.Agent);
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
    }

    [Fact]
    public async Task NewSession_FromSettingsPage_ReturnsToChatPage()
    {
        using var environment = await GuiTestEnvironment.CreateAsync();
        using var viewModel = new MainViewModel(environment.App, environment.Agent);
        viewModel.ShowSettingsCommand.Execute(null);

        viewModel.NewSessionCommand.Execute(null);

        for (var attempt = 0; attempt < 500 && viewModel.SelectedSession?.SessionId == environment.Agent.Id; attempt++)
        {
            await Task.Delay(5, TestContext.Current.CancellationToken);
            Dispatcher.UIThread.RunJobs();
        }
        Assert.True(viewModel.IsChatPage);
        Assert.NotNull(viewModel.SelectedSession);
        Assert.NotEqual(environment.Agent.Id, viewModel.SelectedSession!.SessionId);
    }

    [Fact]
    public async Task Sessions_GroupedByWorkspace_AndMarkCurrentSessionSelected()
    {
        using var environment = await GuiTestEnvironment.CreateAsync();
        using var viewModel = new MainViewModel(environment.App, environment.Agent);

        var workspace = Assert.Single(viewModel.Workspaces);
        Assert.NotEmpty(workspace.Name);
        var node = Assert.Single(workspace.Sessions);
        Assert.Equal(environment.Agent.Id, node.SessionId);
        Assert.Same(node, viewModel.SelectedSession);
        Assert.True(node.IsSelected);
        Assert.True(node.IsLive);
    }

    [Fact]
    public async Task SnapshotReplay_MapsEventsToMessagesAndTrace()
    {
        using var environment = await GuiTestEnvironment.CreateAsync();
        var session = environment.Agent.Session;
        session.Append(new UserMessagePayload(MessageFactory.CreateUserText("你好")), new SurfaceOp.Append());
        session.Append(new TurnStartPayload(1));
        session.Append(new RequestContextPayload("test", "test-model"));
        session.Append(new ToolCallPayload(1, 1, ToolCallId.Create("call-1"), "bash", """{"command":"ls"}"""));
        session.Append(new TurnEndPayload(1, new TurnEndReason.Completed()));

        using var viewModel = new MainViewModel(environment.App, environment.Agent);

        Assert.Collection(
            viewModel.Messages,
            message => Assert.Equal(MessageKind.User, message.Kind),
            message => Assert.Equal(MessageKind.Tool, message.Kind));
        Assert.Contains(viewModel.TraceItems, item => item.Kind == TraceKind.Turn);
        Assert.Contains(viewModel.TraceItems, item => item.Kind == TraceKind.Context);
        Assert.Contains(viewModel.TraceItems, item => item.Kind == TraceKind.Tool);
        Assert.False(viewModel.IsBusy);
    }

    /** 运行时上下文注入只应出现一行: 请求元数据(request/context)不重复进正文, 插件名也不该再补一个 @。 */
    [Fact]
    public async Task PluginInjectedContext_RendersSingleRow()
    {
        using var environment = await GuiTestEnvironment.CreateAsync();
        var session = environment.Agent.Session;
        session.Append(
            new UserMessagePayload(MessageFactory.CreateUserMessage(
                [new TextBlock("runtime context")],
                new PluginMessageSource("@deepseek-ai/dsh-system-prompt"))),
            new SurfaceOp.Append());
        session.Append(new RequestContextPayload("openai-compatible", "deepseek-v4-flash"));

        using var viewModel = new MainViewModel(environment.App, environment.Agent);

        var message = Assert.Single(viewModel.Messages);
        Assert.Equal(MessageKind.Context, message.Kind);
        Assert.Equal("上下文注入 · @deepseek-ai/dsh-system-prompt", message.Text);
        Assert.False(message.HasRole);
        Assert.Contains(viewModel.TraceItems, item => item.Title.StartsWith("请求上下文", StringComparison.Ordinal));
    }

    [Fact]
    public async Task SelectTraceItem_MarksSelectionAndRequestsScroll()
    {
        using var environment = await GuiTestEnvironment.CreateAsync();
        var session = environment.Agent.Session;
        session.Append(new UserMessagePayload(MessageFactory.CreateUserText("你好")), new SurfaceOp.Append());
        session.Append(new TurnStartPayload(1));
        using var viewModel = new MainViewModel(environment.App, environment.Agent);

        MessageViewModel? scrolled = null;
        viewModel.ScrollRequested += message => scrolled = message;
        var trace = viewModel.TraceItems.First(item => item.Kind == TraceKind.Turn);
        viewModel.SelectTraceItemCommand.Execute(trace);

        Assert.True(trace.IsSelected);
        Assert.Same(viewModel.Messages[^1], scrolled);
    }

    [Fact]
    public async Task Submit_SlashCommand_RunsThroughCommandsService()
    {
        using var environment = await GuiTestEnvironment.CreateAsync();
        using var viewModel = new MainViewModel(environment.App, environment.Agent);

        viewModel.Composer.Input = "/plugins";
        await viewModel.SubmitCommand.ExecuteAsync(null);

        Assert.Equal("", viewModel.Composer.Input);
        var message = Assert.Single(viewModel.Messages);
        Assert.Equal(MessageKind.System, message.Kind);
        Assert.Contains("dsh-", message.Text);
    }

    [AvaloniaFact]
    public async Task Submit_TextMessage_IsRenderedOnceFromSessionEvent()
    {
        using var environment = await GuiTestEnvironment.CreateAsync();
        using var viewModel = new MainViewModel(environment.App, environment.Agent);

        viewModel.Composer.Input = "你好";
        await viewModel.SubmitCommand.ExecuteAsync(null);

        // 提交本身不回显, 消息要等会话事件到达才出现。
        Assert.Empty(viewModel.Messages);
        for (var attempt = 0; attempt < 200 && viewModel.Messages.Count == 0; attempt += 1)
        {
            await Task.Delay(10);
            Dispatcher.UIThread.RunJobs();
        }
        Assert.Single(viewModel.Messages, message => message.Kind == MessageKind.User);
        Assert.Equal("你好", viewModel.Messages[0].Text);
    }

    [Fact]
    public async Task Submit_EmptyInput_DoesNothing()
    {
        using var environment = await GuiTestEnvironment.CreateAsync();
        using var viewModel = new MainViewModel(environment.App, environment.Agent);

        viewModel.Composer.Input = "   ";
        await viewModel.SubmitCommand.ExecuteAsync(null);

        Assert.Empty(viewModel.Messages);
        Assert.False(viewModel.IsBusy);
    }

    [Fact]
    public async Task SettingsFacade_WritesSafetySettingsThroughSingleEntry()
    {
        using var environment = await GuiTestEnvironment.CreateAsync();
        using var viewModel = new MainViewModel(environment.App, environment.Agent);

        viewModel.Settings.UpdateSafety(autoApprove: true, ["bash: rm -rf /*"]);

        var reloaded = viewModel.Settings.Load();
        var safety = Assert.IsType<SafetySettings>(reloaded.Safety);
        Assert.True(safety.AutoApprove);
        Assert.Contains("bash: rm -rf /*", safety.Blacklist);
        Assert.True(File.Exists(viewModel.Settings.SettingsPath));
    }

    [Fact]
    public async Task RestoredSession_KeepsListedAndReplayable()
    {
        using var environment = await GuiTestEnvironment.CreateAsync();
        var sessionId = environment.Agent.Id;
        environment.Agent.Session.Append(new UserMessagePayload(MessageFactory.CreateUserText("第一条")), new SurfaceOp.Append());
        await environment.App.Ctx.Get<SessionStore>(SessionStore.ServiceName)!.Flush(environment.Agent.Session);

        using var restarted = await environment.RestartAsync();
        var agents = restarted.App.Ctx.Get<AgentRegistry>(AgentRegistry.ServiceName)!;
        var handle = await agents.Resume(new ResumeAgentOptions(sessionId, new AgentOptions("test", "test-model")), TestContext.Current.CancellationToken);
        var resumed = (AgentLoopAgent)handle.Agent;
        using var viewModel = new MainViewModel(restarted.App, resumed);

        Assert.Collection(viewModel.Messages, message => Assert.Equal("第一条", message.Text));
        var nodes = viewModel.Workspaces.SelectMany(workspace => workspace.Sessions).ToList();
        var node = Assert.Single(nodes, candidate => candidate.SessionId == sessionId);
        Assert.Same(resumed, node.Agent);
        Assert.True(node.IsLive);
    }

    [Fact]
    public async Task SetMode_AppliesSessionApprovalPolicy_AndUpdatesPermissionLabel()
    {
        using var environment = await GuiTestEnvironment.CreateAsync();
        using var viewModel = new MainViewModel(environment.App, environment.Agent);
        var approval = environment.App.Ctx.Get<ApprovalService>(ApprovalService.ServiceName)!;
        environment.Agent.Session.Append(new TurnStartPayload(1));

        viewModel.SetModeCommand.Execute("readonly");
        Assert.Equal(ApprovalPolicy.Never, approval.EffectivePolicy(environment.Agent.Session));
        Assert.Equal("只读（自动拒绝）", viewModel.Composer.PermissionLabel);

        viewModel.SetModeCommand.Execute("full");
        Assert.Equal(ApprovalPolicy.Auto, approval.EffectivePolicy(environment.Agent.Session));
        Assert.Equal("Full access", viewModel.Composer.PermissionLabel);

        viewModel.SetModeCommand.Execute("standard");
        Assert.Equal(ApprovalPolicy.Ask, approval.EffectivePolicy(environment.Agent.Session));
    }

    [AvaloniaFact]
    public async Task ApprovalRequest_ReachesUnifiedDecisionWindow()
    {
        using var environment = await GuiTestEnvironment.CreateAsync();
        using var viewModel = new MainViewModel(environment.App, environment.Agent);
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
        Assert.True(seen!.IsApproval);
        Assert.Equal("bash", seen.ToolName);
        Assert.Equal("ls -la", seen.Command);
    }

    [AvaloniaFact]
    public async Task UserQuestion_ReachesUnifiedDecisionWindow()
    {
        using var environment = await GuiTestEnvironment.CreateAsync();
        using var viewModel = new MainViewModel(environment.App, environment.Agent);
        viewModel.DecisionRequested += decision => Task.FromResult<object?>(new AskUserQuestionAnswer(
            [new AskUserQuestionAnswerItem(decision.Questions[0].Item.Id, ["继续"])]));
        var questions = environment.App.Ctx.Get<UserQuestionService>(UserQuestionService.ServiceName)!;

        var ask = questions.Ask(new AskUserQuestionRequest(
            [new AskUserQuestionItem("q1", "继续吗？", Options: [new AskUserQuestionOption("继续"), new AskUserQuestionOption("停下")])],
            environment.Agent));
        var answer = await PumpAsync(ask);

        Assert.Equal("继续", Assert.Single(answer.Answers).Selected[0]);
    }

    [Fact]
    public async Task TraceFilter_NarrowsTraceView_WithoutDroppingItems()
    {
        using var environment = await GuiTestEnvironment.CreateAsync();
        var session = environment.Agent.Session;
        session.Append(new UserMessagePayload(MessageFactory.CreateUserText("你好")), new SurfaceOp.Append());
        session.Append(new TurnStartPayload(1));
        session.Append(new RequestContextPayload("test", "test-model"));
        using var viewModel = new MainViewModel(environment.App, environment.Agent);

        var filter = viewModel.TraceFilters.First(candidate => candidate.Kind == TraceKind.Context);
        viewModel.SelectTraceFilterCommand.Execute(filter);

        Assert.All(viewModel.TraceView, item => Assert.Equal(TraceKind.Context, item.Kind));
        Assert.Contains(viewModel.TraceItems, item => item.Kind == TraceKind.Turn);
    }

    [Fact]
    public async Task SearchText_FiltersSessions()
    {
        using var environment = await GuiTestEnvironment.CreateAsync();
        using var viewModel = new MainViewModel(environment.App, environment.Agent);
        Assert.NotEmpty(viewModel.Workspaces);

        viewModel.SearchText = "不存在的关键词";

        Assert.Empty(viewModel.Workspaces);
        viewModel.SearchText = "";
        Assert.NotEmpty(viewModel.Workspaces);
    }

    [Fact]
    public async Task Suggestions_OfferCommandsAndSessionMentions()
    {
        using var environment = await GuiTestEnvironment.CreateAsync();
        using var viewModel = new MainViewModel(environment.App, environment.Agent);

        viewModel.Composer.Input = "/mo";

        Assert.True(viewModel.IsSuggestionOpen);
        Assert.Contains(viewModel.Suggestions, suggestion => suggestion.Label == "/model");

        viewModel.Composer.Input = $"@{environment.Agent.Id.Value[..6]}";

        Assert.True(viewModel.IsSuggestionOpen);
        Assert.Contains(viewModel.Suggestions, suggestion => suggestion.InsertText == $"@{environment.Agent.Id.Value}");
    }

    [Fact]
    public async Task WorkspaceView_SwitchPersistsIntoGuiParameters()
    {
        using var environment = await GuiTestEnvironment.CreateAsync();
        using var viewModel = new MainViewModel(environment.App, environment.Agent);

        viewModel.SetWorkspaceViewCommand.Execute("filesystem");

        Assert.True(viewModel.IsFileSystemView);
        Assert.Equal(GuiSettings.ViewFilesystem, viewModel.Gui.Load().WorkspaceView);
    }

    [Fact]
    public async Task DeleteCurrentSession_IsRefused()
    {
        using var environment = await GuiTestEnvironment.CreateAsync();
        using var viewModel = new MainViewModel(environment.App, environment.Agent);
        var node = Assert.Single(viewModel.Workspaces.SelectMany(workspace => workspace.Sessions));

        viewModel.DeleteSessionCommand.Execute(node);

        Assert.Equal("不能删除当前正在使用的会话", viewModel.StatusText);
    }

    private static async Task<T> PumpAsync<T>(Task<T> task)
    {
        for (var attempt = 0; attempt < 500 && !task.IsCompleted; attempt += 1)
        {
            await Task.Delay(5);
            Dispatcher.UIThread.RunJobs();
        }
        return await task;
    }
}
