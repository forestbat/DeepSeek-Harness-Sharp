using Dsh.Boot;
using Dsh.Core;
using Dsh.Interaction;
using Dsh.Llm;
using Dsh.Runtime;
using Dsh.Tui;

namespace Dsh.Tests;

/** TUI 分屏: pane 增删/焦点、鼠标与键盘路由、Ctrl+X W 总览。 */
public sealed class ChatWindowPaneTests : IDisposable
{
    private readonly string _homeDir = Path.Combine(
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../artifacts/test-homes")),
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task AddPane_Splits_And_Draws_Both_Headers()
    {
        var (chat, _, _, first, second) = await CreateChatWithTwoAgents();

        Assert.Equal(2, chat.PaneCount);
        var frame = DrawFrame(chat);
        Assert.Contains(first.Id.ToString(), frame);
        Assert.Contains(second.Id.ToString(), frame);
        Assert.Contains("▶ ", frame);
        chat.Dispose();
    }

    [Fact]
    public async Task Mouse_Click_On_Left_Pane_Changes_Focus()
    {
        var (chat, _, _, _, _) = await CreateChatWithTwoAgents();
        var layout = LayoutEngine.Calculate(120, 40);
        _ = DrawFrameReturningGrid(chat, layout);

        Assert.Equal(1, chat.FocusedPaneId);
        chat.HandleMouseClick(2, 2, layout);

        Assert.Equal(0, chat.FocusedPaneId);
        chat.Dispose();
    }

    [Fact]
    public async Task Mouse_Wheel_Routes_To_Pane_Under_Pointer()
    {
        var (chat, _, _, _, _) = await CreateChatWithTwoAgents();
        var layout = LayoutEngine.Calculate(120, 40);
        _ = DrawFrameReturningGrid(chat, layout);

        chat.HandleMouseWheel(1, 2, 2, layout);

        Assert.False(chat.PaneById(0).StickToBottom);
        Assert.True(chat.PaneById(1).StickToBottom);
        chat.Dispose();
    }

    [Fact]
    public async Task Keyboard_Input_Goes_To_Focused_Pane_Only()
    {
        var (chat, _, _, _, _) = await CreateChatWithTwoAgents();

        Type(chat, "hello");

        Assert.Equal("hello", chat.PaneById(1).Input);
        Assert.Equal("", chat.PaneById(0).Input);
        chat.Dispose();
    }

    [Fact]
    public async Task CtrlX_O_Cycles_Focus()
    {
        var (chat, _, _, _, _) = await CreateChatWithTwoAgents();
        var layout = LayoutEngine.Calculate(120, 40);
        _ = DrawFrameReturningGrid(chat, layout);

        PressCtrl(chat, ConsoleKey.X);
        Press(chat, ConsoleKey.O);

        Assert.Equal(0, chat.FocusedPaneId);
        chat.Dispose();
    }

    [Fact]
    public async Task CtrlX_LeftArrow_Moves_Focus_By_Geometry()
    {
        var (chat, _, _, _, _) = await CreateChatWithTwoAgents();
        var layout = LayoutEngine.Calculate(120, 40);
        _ = DrawFrameReturningGrid(chat, layout);

        PressCtrl(chat, ConsoleKey.X);
        Press(chat, ConsoleKey.LeftArrow);

        Assert.Equal(0, chat.FocusedPaneId);
        chat.Dispose();
    }

    [Fact]
    public async Task CtrlX_Minus_Closes_View_But_Keeps_Session()
    {
        var (chat, _, agents, _, second) = await CreateChatWithTwoAgents();

        PressCtrl(chat, ConsoleKey.X);
        Press(chat, ConsoleKey.OemMinus);

        Assert.Equal(1, chat.PaneCount);
        Assert.Equal(0, chat.FocusedPaneId);
        Assert.NotNull(agents.Get(second.Id));
        chat.Dispose();
    }

    [Fact]
    public async Task CtrlX_W_Opens_Overview_And_Escape_Closes()
    {
        var (chat, _, _, first, second) = await CreateChatWithTwoAgents();
        var layout = LayoutEngine.Calculate(120, 40);
        _ = DrawFrameReturningGrid(chat, layout);

        PressCtrl(chat, ConsoleKey.X);
        Press(chat, ConsoleKey.W);

        var frame = DrawFrame(chat);
        Assert.Contains("总览 PTY", frame);
        Assert.Contains("PTY", frame);
        Assert.Contains("会话", frame);
        Assert.Contains("窗格", frame);
        Assert.Contains(first.Id.ToString(), frame);
        Assert.Contains(second.Id.ToString(), frame);

        Press(chat, ConsoleKey.Escape);
        Assert.DoesNotContain("总览 PTY", DrawFrame(chat));
        chat.Dispose();
    }

    [Fact]
    public async Task CtrlX_W_Enter_Jumps_Focus_To_Selected_Pane()
    {
        var (chat, _, _, _, _) = await CreateChatWithTwoAgents();
        var layout = LayoutEngine.Calculate(120, 40);
        _ = DrawFrameReturningGrid(chat, layout);

        PressCtrl(chat, ConsoleKey.X);
        Press(chat, ConsoleKey.W);
        Press(chat, ConsoleKey.UpArrow);
        Press(chat, ConsoleKey.Enter);

        Assert.Equal(0, chat.FocusedPaneId);
        chat.Dispose();
    }

    [Fact]
    public async Task CtrlX_Plus_Splits_Even_With_Modifier_Keydown_And_Char_Only_Plus()
    {
        var (chat, _, _, _, _) = await CreateChatWithTwoAgents();
        var layout = LayoutEngine.Calculate(120, 40);
        _ = DrawFrameReturningGrid(chat, layout);

        PressCtrl(chat, ConsoleKey.X);
        // 宿主会先送一条 Shift 按下(无键名无字符), 随后 '+' 在 Unix/pty 上只带字符不带键名
        chat.HandleKey(new ConsoleKeyInfo('\0', ConsoleKey.NoName, true, false, false));
        chat.HandleKey(new ConsoleKeyInfo('+', ConsoleKey.NoName, true, false, false));

        for (var attempt = 0; attempt < 500 && chat.PaneCount < 3; attempt++)
        {
            chat.DrainUi();
            await Task.Delay(5, TestContext.Current.CancellationToken);
        }

        Assert.Equal(3, chat.PaneCount);
        chat.Dispose();
    }

    [Fact]
    public async Task CtrlX_Plus_Splits_A_New_Session()
    {
        var (chat, _, _, first, _) = await CreateChatWithTwoAgents();
        var layout = LayoutEngine.Calculate(120, 40);
        _ = DrawFrameReturningGrid(chat, layout);

        PressCtrl(chat, ConsoleKey.X);
        Press(chat, ConsoleKey.OemPlus);

        for (var attempt = 0; attempt < 500 && chat.PaneCount < 3; attempt++)
        {
            chat.DrainUi();
            await Task.Delay(5, TestContext.Current.CancellationToken);
        }

        Assert.Equal(3, chat.PaneCount);
        Assert.NotEqual(first.Id.ToString(), chat.PaneById(chat.FocusedPaneId).Agent.Id.ToString());
        chat.Dispose();
    }

    [Fact]
    public async Task Background_Pane_Event_Updates_Its_Renderer_Without_Stealing_Focus()
    {
        var (chat, _, _, _, second) = await CreateChatWithTwoAgents();
        var layout = LayoutEngine.Calculate(120, 40);
        _ = DrawFrameReturningGrid(chat, layout);

        second.Session.Append(new AssistantChunkPayload(1, 1, new StreamChunk.TextDelta(0, "后台消息")));
        chat.DrainUi();

        Assert.Equal(1, chat.FocusedPaneId);
        Assert.Contains("后台消息", DrawFrame(chat));
        chat.Dispose();
    }

    private async Task<(ChatWindow Chat, Context Ctx, AgentRegistry Agents, AgentLoopAgent First, AgentLoopAgent Second)> CreateChatWithTwoAgents()
    {
        var ctx = new Context();
        _ = new SessionStore(ctx);
        var agents = new AgentRegistry(ctx);
        _ = new AgentLoop(ctx);
        CommandsService.Register(ctx);
        var first = await CreateAgent(agents);
        var second = await CreateAgent(agents);
        var chat = new ChatWindow(ctx, first, HarnessHome.Resolve(_homeDir));
        chat.AddPane(second, SplitOrientation.Vertical);
        return (chat, ctx, agents, first, second);
    }

    private static async Task<AgentLoopAgent> CreateAgent(AgentRegistry agents)
    {
        var handle = await agents.Create(new CreateAgentOptions(
            SessionId.Create($"session-{Guid.NewGuid():N}"),
            null,
            new AgentOptions("deepseek-official", "deepseek-v4-flash")));
        var agent = (AgentLoopAgent)handle.Agent;
        await agent.WhenIdle();
        return agent;
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

    private static CellGrid DrawFrameReturningGrid(ChatWindow chat, UiLayout layout)
    {
        var grid = new CellGrid(120, 40);
        chat.Draw(grid, layout);
        return grid;
    }

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
}
