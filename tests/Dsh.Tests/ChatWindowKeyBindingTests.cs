using Dsh.Boot;
using Dsh.Core;
using Dsh.Interaction;
using Dsh.Llm;
using Dsh.Runtime;
using Dsh.Tui;
using Dsh.Tui.Services;
using System.Text;
using Dsh.Agent;

namespace Dsh.Tests;

/** 快捷键分发与提示文本: 默认键位下的行为与显示, 以及 settings.yaml 自定义键位生效。 */
[Collection("TuiTheme")]
public sealed class ChatWindowKeyBindingTests : IDisposable
{
    private const string ChatPrefixHint = "Ctrl+X: N 新会话 - S 会话 - D detach - K 删会话 - A 子代理 - W 总览 - T 开 shell - + 分屏 - - 关窗格 - 方向键/O 切窗格 - ,/. 切会话 - Q 退出";
    private const string ShellPrefixHint = "Ctrl+X: N 新会话 - S 会话 - D detach - A 子代理 - W 总览 - T 开 shell - + 分屏 - - 关窗格 - ,/. 切会话 - Q 退出";
    private const string ReadyStatus = "ready - Enter to send, ^ history, Esc cancels a running turn, Ctrl+C×2 quits";
    private const string OverviewTitle = "总览 Pty";

    private readonly string _homeDir = Path.Combine(
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../artifacts020/test-homes")),
        Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_homeDir))
            Directory.Delete(_homeDir, recursive: true);
    }

    [Fact]
    public async Task CtrlX_Shows_Prefix_Hint_In_Chat()
    {
        var chat = await CreateChat();

        PressCtrl(chat, ConsoleKey.X);

        Assert.Equal(ChatPrefixHint, chat.InputPane.StatusText);
        chat.Dispose();
    }

    [Fact]
    public async Task CtrlX_Shows_Prefix_Hint_In_Shell_Pane()
    {
        var chat = await CreateChat();
        var (shell, _) = AddFakeShell(chat);

        PressCtrl(chat, ConsoleKey.X);

        Assert.Equal(ShellPrefixHint, shell.StatusText);
        chat.Dispose();
    }

    [Fact]
    public async Task Static_Hints_Show_Default_Keys()
    {
        var chat = await CreateChat();

        var frame = DrawFrame(chat, 140, 60);

        Assert.Equal(ReadyStatus, chat.InputPane.StatusText);
        Assert.Contains("Enter 发送 - / 命令 - @ 引用 - Tab 下一个参数 - Ctrl+X 会话", frame);
        Assert.Contains("Ctrl+C×2 退出", frame);
        Assert.Contains("Ctrl+X N 新会话 - S 会话", frame);
        Assert.Contains("Ctrl+X D detach - K 删会话", frame);
        Assert.Contains("Ctrl+X W 总览 - + 分屏 - - 关闭", frame);
        Assert.Contains("Ctrl+X 方向键/O 切窗格", frame);
        PressCtrl(chat, ConsoleKey.C);
        Assert.Equal("再按一次 Ctrl+C 退出", chat.InputPane.StatusText);
        PressCtrl(chat, ConsoleKey.X);
        Press(chat, ConsoleKey.A);
        Assert.Contains("子代理 (Ctrl+X A)", DrawFrame(chat, 140, 60));
        chat.Dispose();
    }

    [Fact]
    public async Task Shell_Pane_Status_Lists_Close_And_Focus_Keys()
    {
        var chat = await CreateChat();
        var (shell, _) = AddFakeShell(chat);
        var layer = new ShellPane(chat, 9, null, 80, 24, "pty-k");

        PressCtrl(chat, ConsoleKey.X);
        Press(chat, ConsoleKey.O);
        PressCtrl(chat, ConsoleKey.X);
        Press(chat, ConsoleKey.O);

        Assert.Equal(shell.Id, chat.FocusedPaneId);
        Assert.Equal("shell 窗格: 按键直达 shell - Ctrl+X - 关闭 - Ctrl+X 方向键/O 切窗格", shell.StatusText);
        Assert.Equal("[pty pty-k] 层 - Ctrl+X W 总览 - Ctrl+X ,/. 切该 pty 的会话 - Ctrl+X D 离开 DSH", layer.StatusText);
        layer.Dispose();
        chat.Dispose();
    }

    [Fact]
    public async Task CtrlX_S_And_K_Prefill_Session_Commands()
    {
        var chat = await CreateChat();

        PressCtrl(chat, ConsoleKey.X);
        Press(chat, ConsoleKey.S);
        Assert.Equal("/session ", chat.InputPane.Input);

        chat.InputPane.Input = "";
        PressCtrl(chat, ConsoleKey.X);
        Press(chat, ConsoleKey.K);
        Assert.Equal("/session delete ", chat.InputPane.Input);
        chat.Dispose();
    }

    [Fact]
    public async Task CtrlX_Q_Requests_Exit()
    {
        var chat = await CreateChat();

        PressCtrl(chat, ConsoleKey.X);
        Press(chat, ConsoleKey.Q);

        Assert.True(chat.ExitRequested);
        chat.Dispose();
    }

    [Fact]
    public async Task CtrlX_N_Starts_A_New_Session_In_The_Input_Pane()
    {
        var chat = await CreateChat();
        var before = chat.InputPane.Agent.Id;

        PressCtrl(chat, ConsoleKey.X);
        Press(chat, ConsoleKey.N);
        for (var attempt = 0; attempt < 500 && chat.InputPane.Agent.Id == before; attempt++)
        {
            chat.DrainUi();
            await Task.Delay(5, TestContext.Current.CancellationToken);
        }

        Assert.NotEqual(before, chat.InputPane.Agent.Id);
        chat.Dispose();
    }

    /** 第二键带着 Ctrl(按住 Ctrl 连按)也认: Ctrl+X Ctrl+W 与 Ctrl+X W 同义。 */
    [Fact]
    public async Task CtrlX_Then_Ctrl_Letter_Matches_The_Plain_Letter()
    {
        var chat = await CreateChat();

        PressCtrl(chat, ConsoleKey.X);
        PressCtrl(chat, ConsoleKey.W);

        Assert.Contains(OverviewTitle, DrawFrame(chat));
        chat.Dispose();
    }

    /** Unix 终端的原始字节输入里字母只带字符(Key=NoName): Ctrl+X 之后的字母键也要认出来。 */
    [Fact]
    public async Task CtrlX_Then_Character_Only_Letter_Opens_Overview()
    {
        var chat = await CreateChat();

        PressCtrl(chat, ConsoleKey.X);
        Type(chat, "w");

        Assert.Contains(OverviewTitle, DrawFrame(chat));
        chat.Dispose();
    }

    /** 前缀之后的未绑定键取消前缀且不进输入行; 取消后的下一个键照常输入。 */
    [Fact]
    public async Task CtrlX_Then_Unbound_Key_Cancels_Prefix()
    {
        var chat = await CreateChat();

        PressCtrl(chat, ConsoleKey.X);
        Type(chat, "z");
        Assert.Equal("", chat.InputPane.Input);
        Assert.Equal(ReadyStatus, chat.InputPane.StatusText);

        Type(chat, "z");
        Assert.Equal("z", chat.InputPane.Input);
        chat.Dispose();
    }

    /** shell 窗格: Ctrl+C / Ctrl+P 直达 shell, 只有 Ctrl+X 序列归窗口。 */
    [Fact]
    public async Task Shell_Pane_Gets_Chat_Only_Keys_But_Not_Window_Sequences()
    {
        var chat = await CreateChat();
        var (_, backing) = AddFakeShell(chat);

        PressCtrl(chat, ConsoleKey.C);
        PressCtrl(chat, ConsoleKey.P);
        PressCtrl(chat, ConsoleKey.X);
        Press(chat, ConsoleKey.W);
        for (var attempt = 0; attempt < 200 && backing.Written.Count < 2; attempt++)
            await Task.Delay(5, TestContext.Current.CancellationToken);

        Assert.Equal(["\u0003", "\u0010"], backing.Written);
        Assert.Contains(OverviewTitle, DrawFrame(chat));
        Assert.False(chat.ExitRequested);
        chat.Dispose();
    }

    /** settings.yaml 改键: 新键生效、旧键失效, 提示文本与 /keys 跟着表走。 */
    [Fact]
    public async Task Configured_Keys_Drive_Dispatch_Hints_And_Keys_Command()
    {
        var chat = await CreateChat(WriteSettings("""
            keys:
              overview: F5
              newSession: "Ctrl+X W"
            """));

        PressCtrl(chat, ConsoleKey.X);
        Assert.Equal("Ctrl+X: W 新会话 - S 会话 - D detach - K 删会话 - A 子代理 - T 开 shell - + 分屏 - - 关窗格 - 方向键/O 切窗格 - ,/. 切会话 - Q 退出", chat.InputPane.StatusText);
        Press(chat, ConsoleKey.Escape);
        Press(chat, ConsoleKey.F5);
        Assert.Contains(OverviewTitle, DrawFrame(chat));
        Press(chat, ConsoleKey.Escape);

        var frame = DrawFrame(chat, 140, 60);
        Assert.Contains("Ctrl+X W 新会话 - S 会话", frame);
        Assert.Contains("F5 总览 - Ctrl+X + 分屏 - Ctrl+X - 关闭", frame);
        chat.RunSlashCommand(chat.InputPane, "/keys");
        frame = DrawFrame(chat, 140, 100);
        Assert.Contains("overview           F5  (自定义)", frame);
        Assert.Contains("newSession         Ctrl+X W  (自定义)", frame);
        Assert.Contains("quit               Ctrl+X Q", frame);
        chat.Dispose();
    }

    /** 窗口级动作改成单键后, shell 窗格里也由窗口接住(不写进 shell)。 */
    [Fact]
    public async Task Configured_Window_Key_Works_From_Shell_Pane()
    {
        var chat = await CreateChat(WriteSettings("""
            keys:
              overview: F5
            """));
        var (_, backing) = AddFakeShell(chat);

        Press(chat, ConsoleKey.F5);

        Assert.Contains(OverviewTitle, DrawFrame(chat));
        Assert.Empty(backing.Written);
        chat.Dispose();
    }

    /** 前缀等待超时(keySequenceTimeoutMs)后收起提示, 下一个键照常输入, 不被前缀吞掉。 */
    [Fact]
    public async Task Prefix_Times_Out_And_Next_Key_Is_Typed()
    {
        var chat = await CreateChat(WriteSettings("""
            keySequenceTimeoutMs: 50
            """));

        PressCtrl(chat, ConsoleKey.X);
        Assert.Equal(ChatPrefixHint, chat.InputPane.StatusText);
        await Task.Delay(200, TestContext.Current.CancellationToken);
        chat.ExpireKeySequence();
        Assert.Equal(ReadyStatus, chat.InputPane.StatusText);

        PressCtrl(chat, ConsoleKey.X);
        await Task.Delay(200, TestContext.Current.CancellationToken);
        Type(chat, "w");
        Assert.Equal("w", chat.InputPane.Input);
        Assert.DoesNotContain(OverviewTitle, DrawFrame(chat));
        chat.Dispose();
    }

    /** 把 TUI 插件参数(yaml 片段)写进 settings.yaml 的 plugins."@deepseek-ai/dsh-tui" 段。 */
    private TuiSettings WriteSettings(string tuiParameters)
    {
        Directory.CreateDirectory(_homeDir);
        var body = string.Join('\n', tuiParameters.Split('\n').Select(line => $"    {line}"));
        File.WriteAllText(Path.Combine(_homeDir, "settings.yaml"), $"plugins:\n  \"{TuiSettings.Package}\":\n{body}\n");
        return new TuiSettings(HarnessHome.Resolve(_homeDir));
    }

    private async Task<ChatWindow> CreateChat(TuiSettings? settings = null)
    {
        var ctx = new Context();
        _ = new SessionStore(ctx);
        var agents = new AgentRegistry(ctx);
        _ = new AgentLoop(ctx);
        CommandsService.Register(ctx);
        var handle = await agents.Create(new CreateAgentOptions(
            SessionId.Create($"session-{Guid.NewGuid():N}"),
            null,
            new AgentOptions("deepseek-official", "deepseek-v4-flash")));
        var agent = (AgentLoopAgent)handle.Agent;
        await agent.WhenIdle();
        var chat = new ChatWindow(ctx, agent, HarnessHome.Resolve(_homeDir), null, settings);
        _ = DrawFrame(chat);
        return chat;
    }

    private static (ShellPane Shell, RecordingBacking Backing) AddFakeShell(ChatWindow chat)
    {
        var backing = new RecordingBacking();
        var shell = new ShellPane(chat, 7, backing, 80, 24);
        chat.AddPane(shell, SplitOrientation.Vertical);
        return (shell, backing);
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

    private static string DrawFrame(ChatWindow chat, int width = 120, int height = 40)
    {
        var layout = LayoutEngine.Calculate(width, height);
        var grid = new CellGrid(width, height);
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

    /** 记录写入的终端 backing: 没有输出, 只用来断言按键是否直达 shell。 */
    private sealed class RecordingBacking : ITerminalBacking
    {
        private readonly TaskCompletionSource _closed = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public List<string> Written { get; } = [];

        public string Command => "sh";

        public int? ProcessId => null;

        public string PtyId => "";

        public bool Exited => _closed.Task.IsCompleted;

        public async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await _closed.Task.WaitAsync(cancellationToken);
            return 0;
        }

        public ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
        {
            lock (Written)
                Written.Add(Encoding.UTF8.GetString(data.Span));
            return ValueTask.CompletedTask;
        }

        public void Resize(int rows, int columns)
        {
        }

        public void Dispose() => _closed.TrySetResult();
    }
}
