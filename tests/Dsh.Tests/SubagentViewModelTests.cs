using Avalonia.Threading;
using Dsh.Core;
using Dsh.Gui.ViewModels;
using Dsh.Llm;

namespace Dsh.Tests;

/** GUI 子代理列表与只读视图的视图模型行为: 持久化子树、live 翻转、兄弟导航与只读性。 */
[Collection(GuiSerialCollection.CollectionName)]
public sealed class SubagentViewModelTests
{
    [Fact]
    public async Task Refresh_ListsPersistentDescendants_WithDepthAndLabel()
    {
        using var environment = await GuiTestEnvironment.CreateAsync();
        var store = environment.App.Ctx.Get<SessionStore>(SessionStore.ServiceName)!;
        var parent = environment.Agent.Id;
        var first = SubagentTestData.AddChild(store, parent, "alpha", createdAt: 1);
        SubagentTestData.AddChild(store, first.Id, "beta", createdAt: 2, depth: 2);

        using var panel = new SubagentPanelViewModel(environment.App.Ctx);
        panel.SetRoot(parent);

        Assert.Equal(2, panel.Nodes.Count);
        Assert.Equal(["alpha", "beta"], panel.Nodes.Select(node => node.DisplayName));
        Assert.Equal(1, panel.Nodes[0].Depth);
        Assert.Equal(2, panel.Nodes[1].Depth);
        Assert.True(panel.Nodes[0].HasChildren);
        Assert.False(panel.Nodes[1].HasChildren);
        Assert.True(panel.HasNodes);
        Assert.Equal("one-shot", panel.Nodes[0].ModeLabel);
    }

    [Fact]
    public async Task StartAndEnd_FlipLiveStateAndBadge()
    {
        using var environment = await GuiTestEnvironment.CreateAsync();
        var store = environment.App.Ctx.Get<SessionStore>(SessionStore.ServiceName)!;
        var parent = environment.Agent.Id;
        var first = SubagentTestData.AddChild(store, parent, "alpha", createdAt: 1);
        SubagentTestData.AddChild(store, parent, "beta", createdAt: 2);

        using var panel = new SubagentPanelViewModel(environment.App.Ctx);
        panel.SetRoot(parent);
        Assert.All(panel.Nodes, node => Assert.False(node.IsLive));
        Assert.Equal("0", panel.Badge);

        panel.NotifyStart(first.Id);
        Assert.True(panel.Nodes.Single(node => node.Id == first.Id).IsLive);
        Assert.Equal(1, panel.LiveCount);
        Assert.Equal("1", panel.Badge);

        panel.NotifyEnd(first.Id);
        Assert.False(panel.Nodes.Single(node => node.Id == first.Id).IsLive);
        Assert.Equal(0, panel.LiveCount);
        Assert.Equal("0", panel.Badge);
    }

    [Fact]
    public async Task Open_RendersChildTranscript_AndCloseHides()
    {
        using var environment = await GuiTestEnvironment.CreateAsync();
        var store = environment.App.Ctx.Get<SessionStore>(SessionStore.ServiceName)!;
        var parent = environment.Agent.Id;
        SubagentTestData.AddChild(store, parent, "alpha", createdAt: 1, withToolCall: true);

        using var panel = new SubagentPanelViewModel(environment.App.Ctx);
        panel.SetRoot(parent);
        Assert.False(panel.IsViewing);

        panel.OpenCommand.Execute(panel.Nodes[0]);

        Assert.True(panel.IsViewing);
        var viewing = panel.Viewing;
        Assert.NotNull(viewing);
        Assert.Contains(viewing.Messages, message => message.Kind == MessageKind.Tool && message.Text.Contains("bash"));
        Assert.Contains(viewing.Messages, message => message.Kind == MessageKind.Result && message.Text.Contains("child-out"));

        panel.CloseCommand.Execute(null);
        Assert.False(panel.IsViewing);
        Assert.Null(panel.Viewing);
        Assert.Null(panel.Current);
    }

    [Fact]
    public async Task PreviousNext_NavigatesSiblings()
    {
        using var environment = await GuiTestEnvironment.CreateAsync();
        var store = environment.App.Ctx.Get<SessionStore>(SessionStore.ServiceName)!;
        var parent = environment.Agent.Id;
        SubagentTestData.AddChild(store, parent, "a", createdAt: 1);
        SubagentTestData.AddChild(store, parent, "b", createdAt: 2);
        SubagentTestData.AddChild(store, parent, "c", createdAt: 3);

        using var panel = new SubagentPanelViewModel(environment.App.Ctx);
        panel.SetRoot(parent);
        panel.OpenCommand.Execute(panel.Nodes[1]);

        Assert.Equal("2 / 3", panel.PositionText);
        Assert.True(panel.CanPrevious);
        Assert.True(panel.CanNext);

        panel.NextCommand.Execute(null);
        Assert.Equal("c", panel.Current!.DisplayName);
        Assert.False(panel.CanNext);

        panel.PreviousCommand.Execute(null);
        panel.PreviousCommand.Execute(null);
        Assert.Equal("a", panel.Current!.DisplayName);
        Assert.False(panel.CanPrevious);
    }

    [Fact]
    public async Task View_NeverWritesToChildSession()
    {
        using var environment = await GuiTestEnvironment.CreateAsync();
        var store = environment.App.Ctx.Get<SessionStore>(SessionStore.ServiceName)!;
        var parent = environment.Agent.Id;
        var child = SubagentTestData.AddChild(store, parent, "alpha", createdAt: 1, withToolCall: true);

        using var panel = new SubagentPanelViewModel(environment.App.Ctx);
        panel.SetRoot(parent);
        var before = child.Seq;

        panel.OpenCommand.Execute(panel.Nodes[0]);
        panel.NextCommand.Execute(null);
        panel.PreviousCommand.Execute(null);
        panel.CloseCommand.Execute(null);

        Assert.Equal(before, child.Seq);
    }

    [Fact]
    public async Task LiveChildEvents_AppendToOpenView() => await HeadlessGui.RunAsync(async () =>
    {
        var environment = await GuiTestEnvironment.CreateAsync();
        using var environmentScope = environment;
        var store = environment.App.Ctx.Get<SessionStore>(SessionStore.ServiceName)!;
        var parent = environment.Agent.Id;
        var child = SubagentTestData.AddChild(store, parent, "alpha", createdAt: 1);

        using var panel = new SubagentPanelViewModel(environment.App.Ctx);
        panel.SetRoot(parent);
        panel.NotifyStart(child.Id);
        panel.OpenCommand.Execute(panel.Nodes[0]);
        var initial = panel.Viewing!.Messages.Count;

        child.Append(new TurnStartPayload(1));
        child.Append(new ToolCallPayload(1, 1, ToolCallId.Create("call-live"), "bash", """{"command":"live"}"""));
        for (var attempt = 0; attempt < 200 && panel.Viewing!.Messages.Count <= initial; attempt++)
        {
            await Task.Delay(5, TestContext.Current.CancellationToken);
            Dispatcher.UIThread.RunJobs();
        }

        Assert.Contains(panel.Viewing!.Messages, message => message.Text.Contains("live"));
    });

    [Fact]
    public async Task SubagentToolCall_RendersExpandableCard_WithInlineStream()
    {
        using var environment = await GuiTestEnvironment.CreateAsync();
        var store = environment.App.Ctx.Get<SessionStore>(SessionStore.ServiceName)!;
        var parent = environment.Agent.Session;
        SubagentTestData.AddChild(store, parent.Id, "research", createdAt: 1, withToolCall: true);
        parent.Append(new TurnStartPayload(1));
        var callSeq = parent.Seq;
        parent.Append(new ToolCallPayload(1, 1, ToolCallId.Create("call-sub"), "subagent",
            """{"description":"research","prompt":"go"}"""));
        parent.Append(
            new ToolResultPayload(1, 1, MessageFactory.CreateToolResultMessage(
                ToolCallId.Create("call-sub"), [new TextBlock("done")], false)),
            new SurfaceOp.Append(),
            [callSeq]);
        parent.Append(new TurnEndPayload(1, new TurnEndReason.Completed()));

        using var viewModel = new MainViewModel(environment.App, environment.Agent);

        var card = Assert.Single(viewModel.Messages, message => message.IsSubagentTool);
        Assert.Contains("research", card.Text);
        Assert.False(card.IsExpanded);
        Assert.False(card.ShowSubagentStream);

        viewModel.ToggleMessageFoldCommand.Execute(card);

        Assert.True(card.IsExpanded);
        Assert.True(card.ShowSubagentStream);
        Assert.Contains(card.SubagentStream, message => message.Text.Contains("bash"));
        Assert.Contains(card.SubagentStream, message => message.Text.Contains("child-out"));
    }
}
