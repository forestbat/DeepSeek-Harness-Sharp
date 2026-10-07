using Dsh.Boot;
using Dsh.Core;
using Dsh.Interaction;
using Dsh.Llm;
using Dsh.Runtime;
using Dsh.Subagent;
using Dsh.Tui;

namespace Dsh.Tests;

/** TUI 子代理: 列表弹层、live 翻转、只读查看窗格、父子/兄弟导航、transcript 折叠。 */
public sealed class ChatWindowSubagentTests : IDisposable
{
    private readonly string _homeDir = Path.Combine(
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../artifacts020/test-homes")),
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task CtrlX_A_Lists_Subagent_Tree_With_Label_Mode_And_State()
    {
        var fixture = await CreateFixtureAsync();
        using var chat = fixture.Chat;
        AddChild(fixture.Store, fixture.Parent.Id, "alpha", 1000);
        AddChild(fixture.Store, fixture.Parent.Id, "beta", 2000);

        PressCtrl(chat, ConsoleKey.X);
        Press(chat, ConsoleKey.A);

        var frame = DrawFrame(chat);
        Assert.Contains("子代理 (Ctrl+X A)", frame);
        Assert.Contains("alpha", frame);
        Assert.Contains("beta", frame);
        Assert.Contains("[one-shot]", frame);
        Assert.Contains("○ ended", frame);
    }

    [Fact]
    public async Task Subagent_Start_And_End_Flip_Live_State_In_List()
    {
        var fixture = await CreateFixtureAsync();
        using var chat = fixture.Chat;
        var child = AddChild(fixture.Store, fixture.Parent.Id, "alpha", 1000);

        PressCtrl(chat, ConsoleKey.X);
        Press(chat, ConsoleKey.A);
        Assert.Contains("○ ended", DrawFrame(chat));

        fixture.Ctx.Events.Emit(fixture.Ctx, new SubagentStartNotification(
            new SubagentRunInfo("run-1", "spawn", child.Id, true)));
        chat.DrainUi();
        Assert.Contains("● live", DrawFrame(chat));

        fixture.Ctx.Events.Emit(fixture.Ctx, new SubagentEndNotification(
            new SubagentRunEndInfo("run-1", "spawn", child.Id, true, SubagentStopReason.Completed)));
        chat.DrainUi();
        Assert.Contains("○ ended", DrawFrame(chat));
    }

    [Fact]
    public async Task Enter_Opens_ReadOnly_Pane_And_Typing_Still_Reaches_Main_Pane()
    {
        var fixture = await CreateFixtureAsync();
        using var chat = fixture.Chat;
        var alpha = AddChild(fixture.Store, fixture.Parent.Id, "alpha", 1000, withToolCall: true);
        AddChild(fixture.Store, fixture.Parent.Id, "beta", 2000);

        PressCtrl(chat, ConsoleKey.X);
        Press(chat, ConsoleKey.A);
        Press(chat, ConsoleKey.Enter);
        chat.DrainUi();

        Assert.Equal(2, chat.PaneCount);
        Assert.True(chat.IsFocusedPaneSubagent);
        Assert.Equal(alpha.Id, chat.FocusedSubagentSessionId);
        Assert.Equal(0, chat.InputPaneId);

        Type(chat, "hello");
        Assert.Equal("hello", chat.PaneById(0).Input);

        var frame = DrawFrame(chat);
        Assert.Contains("子代理 alpha", frame);
        Assert.Contains("child-out", frame);
    }

    [Fact]
    public async Task Arrow_Keys_Navigate_Siblings_And_Return_To_Parent()
    {
        var fixture = await CreateFixtureAsync();
        using var chat = fixture.Chat;
        var alpha = AddChild(fixture.Store, fixture.Parent.Id, "alpha", 1000);
        var beta = AddChild(fixture.Store, fixture.Parent.Id, "beta", 2000);

        PressCtrl(chat, ConsoleKey.X);
        Press(chat, ConsoleKey.A);
        Press(chat, ConsoleKey.Enter);
        chat.DrainUi();
        Assert.Equal(alpha.Id, chat.FocusedSubagentSessionId);

        Press(chat, ConsoleKey.RightArrow);
        Assert.Equal(beta.Id, chat.FocusedSubagentSessionId);

        Press(chat, ConsoleKey.LeftArrow);
        Assert.Equal(alpha.Id, chat.FocusedSubagentSessionId);

        Press(chat, ConsoleKey.UpArrow);
        Assert.False(chat.IsFocusedPaneSubagent);
        Assert.Equal(1, chat.PaneCount);
        Assert.Equal(0, chat.FocusedPaneId);
    }

    [Fact]
    public async Task Subagent_Tool_Call_Folds_And_Expands_Child_Tool_Summary()
    {
        var fixture = await CreateFixtureAsync();
        using var chat = fixture.Chat;
        AddChild(fixture.Store, fixture.Parent.Id, "调研子代理", 1000, withToolCall: true);

        AppendDelegation(fixture.Parent, "调研子代理");
        chat.DrainUi();

        var collapsed = DrawFrame(chat);
        Assert.Contains("subagent: 调研子代理", collapsed);
        Assert.DoesNotContain("child-out", collapsed);
        Assert.DoesNotContain("echo child", collapsed);

        Press(chat, ConsoleKey.Tab);
        Press(chat, ConsoleKey.Enter);

        var expanded = DrawFrame(chat);
        Assert.Contains("echo child", expanded);
        Assert.Contains("child-out", expanded);
    }

    [Fact]
    public async Task Persisted_Delegation_Is_Folded_During_Replay()
    {
        var fixture = await CreateFixtureAsync(createChat: false);
        AddChild(fixture.Store, fixture.Parent.Id, "调研子代理", 1000, withToolCall: true);
        AppendDelegation(fixture.Parent, "调研子代理");

        using var chat = new ChatWindow(fixture.Ctx, fixture.Parent, HarnessHome.Resolve(_homeDir));
        chat.DrainUi();

        var frame = DrawFrame(chat);
        Assert.Contains("subagent: 调研子代理", frame);
        Assert.DoesNotContain("child-out", frame);
    }

    [Fact]
    public async Task List_Shows_Live_And_Persisted_Children_Together()
    {
        var fixture = await CreateFixtureAsync();
        using var chat = fixture.Chat;
        AddChild(fixture.Store, fixture.Parent.Id, "alpha", 1000);
        var beta = AddChild(fixture.Store, fixture.Parent.Id, "beta", 2000);
        fixture.Ctx.Events.Emit(fixture.Ctx, new SubagentStartNotification(
            new SubagentRunInfo("run-beta", "spawn", beta.Id, true)));
        chat.DrainUi();

        PressCtrl(chat, ConsoleKey.X);
        Press(chat, ConsoleKey.A);
        var frame = DrawFrame(chat);
        Assert.Contains("○ ended · alpha", frame);
        Assert.Contains("● live · beta", frame);

        Press(chat, ConsoleKey.Escape);
        Assert.DoesNotContain("子代理 (Ctrl+X A)", DrawFrame(chat));
    }

    [Fact]
    public async Task List_Indents_Nested_Descendants_And_Marks_Parents_With_Children()
    {
        var fixture = await CreateFixtureAsync();
        using var chat = fixture.Chat;
        var alpha = AddChild(fixture.Store, fixture.Parent.Id, "alpha", 1000);
        AddChild(fixture.Store, alpha.Id, "gamma", 1500, depth: 2);

        PressCtrl(chat, ConsoleKey.X);
        Press(chat, ConsoleKey.A);
        var frame = DrawFrame(chat);
        Assert.Contains("› ○ ended · alpha [one-shot] +", frame);
        Assert.Contains("    ○ ended · gamma [one-shot]", frame);
        Assert.DoesNotContain("gamma [one-shot] +", frame);
    }

    [Fact]
    public async Task Arrow_Up_From_Nested_Child_Navigates_To_Subagent_Parent()
    {
        var fixture = await CreateFixtureAsync();
        using var chat = fixture.Chat;
        var alpha = AddChild(fixture.Store, fixture.Parent.Id, "alpha", 1000);
        var gamma = AddChild(fixture.Store, alpha.Id, "gamma", 1500, depth: 2);

        PressCtrl(chat, ConsoleKey.X);
        Press(chat, ConsoleKey.A);
        Press(chat, ConsoleKey.DownArrow);
        Press(chat, ConsoleKey.Enter);
        chat.DrainUi();
        Assert.Equal(gamma.Id, chat.FocusedSubagentSessionId);
        Assert.Equal(2, chat.PaneCount);

        Press(chat, ConsoleKey.UpArrow);
        Assert.True(chat.IsFocusedPaneSubagent);
        Assert.Equal(alpha.Id, chat.FocusedSubagentSessionId);
        Assert.Equal(2, chat.PaneCount);
        Assert.Contains("子代理 alpha", DrawFrame(chat));
    }

    [Fact]
    public async Task Footer_Shows_Sibling_Position_And_Updates_On_Navigation()
    {
        var fixture = await CreateFixtureAsync();
        using var chat = fixture.Chat;
        AddChild(fixture.Store, fixture.Parent.Id, "alpha", 1000);
        var beta = AddChild(fixture.Store, fixture.Parent.Id, "beta", 2000);

        PressCtrl(chat, ConsoleKey.X);
        Press(chat, ConsoleKey.A);
        Press(chat, ConsoleKey.Enter);
        chat.DrainUi();
        Assert.Contains("兄弟 1/2", DrawFrame(chat));

        Press(chat, ConsoleKey.RightArrow);
        Assert.Equal(beta.Id, chat.FocusedSubagentSessionId);
        Assert.Contains("兄弟 2/2", DrawFrame(chat));
    }

    [Fact]
    public async Task Live_Subagent_Events_Append_To_Open_Pane()
    {
        var fixture = await CreateFixtureAsync();
        using var chat = fixture.Chat;
        var alpha = AddChild(fixture.Store, fixture.Parent.Id, "alpha", 1000);
        fixture.Ctx.Events.Emit(fixture.Ctx, new SubagentStartNotification(
            new SubagentRunInfo("run-alpha", "spawn", alpha.Id, true)));
        chat.DrainUi();

        PressCtrl(chat, ConsoleKey.X);
        Press(chat, ConsoleKey.A);
        Press(chat, ConsoleKey.Enter);
        chat.DrainUi();
        Assert.Contains("● live", DrawFrame(chat));

        alpha.Append(new ToolCallPayload(1, 1, ToolCallId.Create("call-live"), "bash", """{"command":"echo live"}"""));
        chat.DrainUi();
        Assert.Contains("echo live", DrawFrame(chat));
    }

    private async Task<Fixture> CreateFixtureAsync(bool createChat = true)
    {
        var ctx = new Context();
        var store = new SessionStore(ctx);
        _ = new AgentRegistry(ctx);
        _ = new AgentLoop(ctx);
        CommandsService.Register(ctx);
        _ = new SubagentRuntime(ctx);
        var agents = ctx.Get<AgentRegistry>(AgentRegistry.ServiceName)!;
        var handle = await agents.Create(new CreateAgentOptions(
            SessionId.Create($"session-{Guid.NewGuid():N}"),
            null,
            new AgentOptions("deepseek-official", "deepseek-v4-flash")));
        var parent = (AgentLoopAgent)handle.Agent;
        await parent.WhenIdle();
        var home = HarnessHome.Resolve(_homeDir);
        var chat = createChat ? new ChatWindow(ctx, parent, home) : null!;
        return new Fixture(ctx, store, parent, chat);
    }

    private static Session AddChild(
        SessionStore store,
        SessionId parent,
        string label,
        long createdAt,
        int depth = 1,
        string mode = SubagentDescriptorPayload.OneShotMode,
        bool withToolCall = false)
    {
        var id = SessionId.Create($"session-sub-{Guid.NewGuid():N}");
        var header = new SessionHeader
        {
            Version = SessionHeader.SessionFormatVersion,
            Id = id,
            CreatedAt = createdAt,
            ParentSession = parent,
            IsSeeded = false,
            Origin = "subagent",
            DelegationDepth = depth,
        };
        var session = store.Create(id, header: header);
        session.Append(new SubagentDescriptorPayload(SubagentDescriptorPayload.CurrentVersion, mode, "spawn", label));
        if (withToolCall)
        {
            var callId = ToolCallId.Create($"call-{id.Value}");
            session.Append(new TurnStartPayload(1));
            var callSeq = session.Seq;
            session.Append(new ToolCallPayload(1, 1, callId, "bash", """{"command":"echo child"}"""));
            session.Append(
                new ToolResultPayload(1, 1, MessageFactory.CreateToolResultMessage(callId, [new TextBlock("child-out")], false)),
                new SurfaceOp.Append(),
                [callSeq]);
            session.Append(new TurnEndPayload(1, new TurnEndReason.Completed()));
        }
        return session;
    }

    private static void AppendDelegation(AgentLoopAgent parent, string description)
    {
        var callId = ToolCallId.Create($"call-{Guid.NewGuid():N}");
        var callSeq = parent.Session.Seq;
        parent.Session.Append(new ToolCallPayload(1, 1, callId, "subagent",
            $$"""{"description":"{{description}}","prompt":"go"}"""));
        parent.Session.Append(
            new ToolResultPayload(1, 1, MessageFactory.CreateToolResultMessage(callId, [new TextBlock("done")], false)),
            new SurfaceOp.Append(),
            [callSeq]);
    }

    private static void Type(ChatWindow chat, string text)
    {
        foreach (var character in text)
            chat.HandleKey(new ConsoleKeyInfo(character, ConsoleKey.NoName, false, false, false));
    }

    private static void Press(ChatWindow chat, ConsoleKey key)
        => chat.HandleKey(new ConsoleKeyInfo('\0', key, false, false, false));

    private static void PressCtrl(ChatWindow chat, ConsoleKey key)
        => chat.HandleKey(new ConsoleKeyInfo('\0', key, false, false, true));

    private static string DrawFrame(ChatWindow chat)
    {
        var layout = LayoutEngine.Calculate(120, 40);
        var grid = new CellGrid(120, 40);
        chat.Draw(grid, layout);
        var lines = new List<string>();
        for (var y = 0; y < grid.Height; y++)
        {
            var chars = new char[grid.Width];
            for (var x = 0; x < grid.Width; x++)
                chars[x] = grid[x, y].Character;
            lines.Add(new string(chars).Replace("\0", ""));
        }

        return string.Join('\n', lines);
    }

    public void Dispose()
    {
        if (Directory.Exists(_homeDir))
            Directory.Delete(_homeDir, true);
    }

    private sealed record Fixture(Context Ctx, SessionStore Store, AgentLoopAgent Parent, ChatWindow Chat);
}
