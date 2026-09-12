using Cordis;
using Dsh.Boot;
using Dsh.Core;
using Dsh.Interaction;
using Dsh.Llm;
using Dsh.Tui;

namespace Dsh.Tests;

public class ChatWindowMenuTests : IDisposable
{
    private readonly string _homeDir = Path.Combine(
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../artifacts/test-homes")),
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Slash_Opens_Command_Popup_With_All_Commands()
    {
        using var chat = await CreateChat();

        Type(chat, "/");

        var frame = DrawFrame(chat);
        Assert.Contains("model", frame);
        Assert.Contains("mcp", frame);
        Assert.Contains("session", frame);
        Assert.Contains("Commands", frame);
    }

    [Fact]
    public async Task Slash_m_Filters_Popup_To_Matching_Commands()
    {
        using var chat = await CreateChat();

        Type(chat, "/m");

        var frame = DrawFrame(chat);
        Assert.Contains("› mcp", frame);
        Assert.Contains("model", frame);
        Assert.DoesNotContain("› session", frame);
    }

    [Fact]
    public async Task Tab_On_Model_Opens_Argument_Menu_With_Provider_Models()
    {
        using var chat = await CreateChat();

        Type(chat, "/model");
        Press(chat, ConsoleKey.Tab);

        var frame = DrawFrame(chat);
        Assert.Contains("deepseek-official/deepseek-v4-flash", frame);

        Type(chat, "pro");
        frame = DrawFrame(chat);
        Assert.Contains("› deepseek-official/deepseek-v4-pro", frame);
        Assert.DoesNotContain("› deepseek-official/deepseek-v4-flash", frame);
    }

    [Fact]
    public async Task Esc_From_Argument_Returns_To_Command_List()
    {
        using var chat = await CreateChat();

        Type(chat, "/model");
        Press(chat, ConsoleKey.Tab);
        Press(chat, ConsoleKey.Escape);

        var frame = DrawFrame(chat);
        Assert.Contains("Commands", frame);
        Assert.Contains("mcp", frame);
    }

    [Fact]
    public async Task Enter_Submits_Incomplete_Command_And_Reports_Error()
    {
        using var chat = await CreateChat();

        Type(chat, "/xyz");
        Press(chat, ConsoleKey.Enter);
        await Task.Delay(300);
        chat.DrainUi();

        var frame = DrawFrame(chat);
        Assert.Contains("unknown command: /xyz", frame);
    }

    [Fact]
    public async Task CtrlC_Twice_Requests_Exit_With_Status_Hint()
    {
        using var chat = await CreateChat();

        PressCtrl(chat, ConsoleKey.C);
        var frame = DrawFrame(chat);
        Assert.Contains("再按一次 Ctrl+C 退出", frame);
        Assert.False(chat.ExitRequested);

        PressCtrl(chat, ConsoleKey.C);
        Assert.True(chat.ExitRequested);
    }

    [Fact]
    public async Task CtrlX_Then_S_Opens_Session_Candidates()
    {
        using var chat = await CreateChat();

        PressCtrl(chat, ConsoleKey.X);
        Press(chat, ConsoleKey.S);

        var frame = DrawFrame(chat);
        Assert.Contains("Session id or title", frame);
    }

    private async Task<ChatWindow> CreateChat()
    {
        var ctx = new Context();
        _ = new SessionStore(ctx);
        var agents = new AgentRegistry(ctx);
        _ = new AgentLoop(ctx);
        var commands = CommandsService.Register(ctx);
        RegisterCommand(commands, "model", "List or switch model");
        RegisterCommand(commands, "mcp", "Manage MCP servers");
        RegisterCommand(commands, "session", "Manage sessions");
        var handle = await agents.Create(new CreateAgentOptions(
            SessionId.Create($"session-{Guid.NewGuid():N}"),
            null,
            new AgentOptions("deepseek-official", "deepseek-v4-flash")));
        var agent = (AgentLoopAgent)handle.Agent;
        await agent.WhenIdle();
        var home = HarnessHome.Resolve(_homeDir);
        return new ChatWindow(ctx, agent, home);
    }

    private static void RegisterCommand(CommandsService commands, string name, string description)
        => commands.Register(new CommandDefinition
        {
            Name = name,
            Description = description,
            Handler = _ => Task.FromResult<CommandResult>(new CommandResult.Success()),
        });

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
        var layout = LayoutEngine.Calculate(100, 30);
        var grid = new CellGrid(100, 30);
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
