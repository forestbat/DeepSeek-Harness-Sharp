using Dsh.Boot;
using Dsh.Core;
using Dsh.Interaction;
using Dsh.Llm;
using Dsh.Pty;
using Dsh.Runtime;
using Dsh.Tui;
using Dsh.Tui.Services;
using System.Diagnostics;
using System.Text;
using System.Threading.Channels;

namespace Dsh.Tests;

/**
 * 改/依赖全局 `TuiTheme` 的用例所在 collection: 禁用并行, 避免与 TuiRunner(带 settings → `TuiTheme.Load`)
 * 之类的用例互相改主题色。
 */
[CollectionDefinition("TuiTheme", DisableParallelization = true)]
public sealed class TuiThemeCollection;

/** TUI 分屏: pane 增删/焦点、鼠标与键盘路由、Ctrl+X W 总览。 */
[Collection("TuiTheme")]
public sealed class ChatWindowPaneTests : IDisposable
{
    /** Upper bound for a real shell to draw its prompt or the output of one command. */
    private static readonly TimeSpan ShellOutputTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan PanePollInterval = TimeSpan.FromMilliseconds(10);

    private readonly string _homeDir = Path.Combine(
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../artifacts020/test-homes")),
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task AddPane_Splits_And_Draws_Both_Headers()
    {
        var (chat, _, _, first, second) = await CreateChatWithTwoAgents();

        Assert.Equal(2, chat.PaneCount);
        // 200 列: 分屏后每个窗格仍够宽, 两个会话 id 都能完整出现在各自窗格头里(窄屏下窗格头会被分隔线截断)。
        var frame = DrawFrame(chat, 200);
        Assert.Contains(first.Id.ToString(), frame);
        Assert.Contains(second.Id.ToString(), frame);
        Assert.Contains("> ", frame);
        chat.Dispose();
    }

    /** 分屏不能把右侧边栏挤掉: 多窗格时右栏仍按 layout.RightPanel 绘制, 窗格只在正文区里再分。 */
    [Fact]
    public async Task Split_Keeps_RightPanel_Visible()
    {
        var (chat, _, _, _, _) = await CreateChatWithTwoAgents();
        var layout = LayoutEngine.Calculate(120, 40);
        Assert.True(layout.RightPanel.Width > 0, "120 列下应当存在右栏");
        var grid = DrawFrameReturningGrid(chat, layout);

        var panel = new StringBuilder();
        for (var y = 0; y < layout.RightPanel.Height && y < grid.Height; y++)
        {
            for (var x = layout.RightPanel.X; x < layout.RightPanel.Right && x < grid.Width; x++)
            {
                // 分屏的窗格内容/分隔线都不许落到右栏里(否则就是"分屏把侧栏挤掉")。
                Assert.NotEqual('│', grid[x, y].Character);
                panel.Append(grid[x, y].Character);
            }
        }

        Assert.Contains("上下文", panel.ToString());
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
    public async Task ApplyPaneInput_Focus_SwitchesFocusedPane()
    {
        var (chat, _, _, _, _) = await CreateChatWithTwoAgents();
        var layout = LayoutEngine.Calculate(120, 40);
        _ = DrawFrameReturningGrid(chat, layout);

        var result = chat.ApplyPaneInput("focus", 1, "");

        Assert.Equal(1, chat.FocusedPaneId);
        Assert.Contains("focused pane 1", result);
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
        Assert.Contains("总览 Pty", frame);
        Assert.Contains("pane 0", frame);
        Assert.Contains(first.Id.ToString(), frame);
        Assert.Contains(second.Id.ToString(), frame);

        Press(chat, ConsoleKey.Escape);
        Assert.DoesNotContain("总览 Pty", DrawFrame(chat));
        chat.Dispose();
    }

    /** 单一树: 会话直接列在树里(没有 pty 归属时不再有旧的“pty ~”归属行, 也没有“PTY/会话”两段标题)。 */
    [Fact]
    public async Task CtrlX_W_Overview_Is_Single_Tree()
    {
        var (chat, _, _, first, second) = await CreateChatWithTwoAgents();
        var layout = LayoutEngine.Calculate(120, 40);
        _ = DrawFrameReturningGrid(chat, layout);

        PressCtrl(chat, ConsoleKey.X);
        Press(chat, ConsoleKey.W);

        var lines = DrawFrame(chat).Split('\n');
        Assert.Contains(lines, line => line.Contains(first.Id.ToString()));
        Assert.Contains(lines, line => line.Contains(second.Id.ToString()));
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
        Press(chat, ConsoleKey.DownArrow);
        Press(chat, ConsoleKey.DownArrow);
        Press(chat, ConsoleKey.Enter);

        // 方向键只在可选行间移动: Enter 必然激活某个窗格/会话并关闭总览(None 行会被跳过)。
        Assert.Contains(chat.FocusedPaneId, new[] { 0, 1 });
        Assert.DoesNotContain("总览 Pty", DrawFrame(chat));
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
    public async Task CtrlX_Comma_Period_Cycles_Sessions_In_Same_Pty()
    {
        var (chat, _, _, _, _) = await CreateChatWithTwoAgents();
        var layout = LayoutEngine.Calculate(120, 40);
        _ = DrawFrameReturningGrid(chat, layout);
        Assert.Equal(1, chat.FocusedPaneId);

        PressCtrl(chat, ConsoleKey.X);
        Press(chat, ConsoleKey.OemComma);
        Assert.Equal(0, chat.FocusedPaneId);

        PressCtrl(chat, ConsoleKey.X);
        Press(chat, ConsoleKey.OemPeriod);
        Assert.Equal(1, chat.FocusedPaneId);

        // Unix/pty 宿主只送字符不带键名: 归一化后仍应切换。
        PressCtrl(chat, ConsoleKey.X);
        chat.HandleKey(new ConsoleKeyInfo(',', ConsoleKey.NoName, false, false, false));
        Assert.Equal(0, chat.FocusedPaneId);
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

    /**
     * §19 阶段 2: 总览里 Enter 一个 daemon pty → 开一个订阅其字节流的终端窗格(挂在同一棵树下), 关闭只退订。
     */
    [Fact]
    public async Task Overview_Enter_Daemon_Pty_Opens_Remote_Terminal_Pane()
    {
        var (chat, _, _, _, _) = await CreateChatWithTwoAgents();
        var backing = new FakeRemoteBacking("pty-remote");
        // daemon 里程序就是 shell 的 pty: 进这一层 = 用它的前台 shell(订阅 + 按键写回)。
        var access = new FakeDaemonAccess("pty-remote", backing, command: "pwsh.exe -NoLogo");
        chat.DaemonPtyAccess = access;
        var layout = LayoutEngine.Calculate(120, 40);
        _ = DrawFrameReturningGrid(chat, layout);

        PressCtrl(chat, ConsoleKey.X);
        Press(chat, ConsoleKey.W);
        // daemon pty 列表是异步刷新的: 等它进总览。
        for (var attempt = 0; attempt < 500 && !DrawFrame(chat).Contains("pty-remote"); attempt++)
        {
            chat.DrainUi();
            await Task.Delay(5, TestContext.Current.CancellationToken);
        }
        Assert.Contains("pty-remote", DrawFrame(chat));

        // 选中行不一定是 pty 行(初始选中当前焦点窗格所在行): 向上遍历可选行直到停在 pty 行。
        for (var attempt = 0; attempt < 20 && !SelectedOverviewLine(chat).Contains("pty-remote"); attempt++)
            Press(chat, ConsoleKey.UpArrow);
        Assert.Contains("pty-remote", SelectedOverviewLine(chat));

        Press(chat, ConsoleKey.Enter);
        for (var attempt = 0; attempt < 500 && chat.PaneCount < 3; attempt++)
        {
            chat.DrainUi();
            await Task.Delay(5, TestContext.Current.CancellationToken);
        }

        // 整屏换层: 不是新增一个窗格和会话并排 —— 这一层也不是窗格目录里的一员。
        Assert.True(chat.InPtyLayer);
        Assert.NotNull(access.Entered);
        Assert.Equal("pty-remote", access.Entered.Value.PtyId);
        var paneId = chat.FocusedPaneId;
        var panes = await DispatchAsync(chat, chat.SnapshotPanesAsync());
        Assert.DoesNotContain(panes, entry => entry.PtyId == "pty-remote");

        // 订阅的输出渲染进这一层; 键入写回该 pty。
        backing.Push("remote-ok\r\n");
        Assert.Contains("remote-ok", await WaitForPaneTextAsync(chat, paneId, text => text.Contains("remote-ok")));

        chat.ApplyPaneInput("text", paneId, "ls\r");
        for (var attempt = 0; attempt < 200 && !backing.Written.Any(entry => entry.Contains("ls")); attempt++)
            await Task.Delay(5, TestContext.Current.CancellationToken);
        Assert.Contains(backing.Written, entry => entry.Contains("ls"));

        // 退出这一层 = 退订, 不杀远端会话。
        PressCtrl(chat, ConsoleKey.X);
        Press(chat, ConsoleKey.OemMinus);
        Assert.False(chat.InPtyLayer);
        Assert.Equal(2, chat.PaneCount);
        Assert.True(backing.Disposed);
        chat.Dispose();
    }

    /**
     * 进入同一个 daemon 下**别的** pty: 这一层同样是一个终端(它的程序不是 shell → DSH 另托一个),
     * 但"看到它的会话 + 响应 Ctrl+X"与任何 pty 一致 —— 横幅列出它的会话, Ctrl+X . 请对面切会话,
     * 绝不把别人的对话界面镜像进来当这一层的内容。
     */
    [Fact(Timeout = 5000)]
    public async Task Overview_Enter_Foreign_Dsh_Pty_Hosts_Terminal_And_Switches_Its_Sessions()
    {
        var (chat, _, _, _, _) = await CreateChatWithTwoAgents();
        var access = new FakeDaemonAccess("pty-other", new FakeRemoteBacking("pty-other"), panes:
        [
            new PtyPaneSnapshotDto { Id = 0, Kind = "chat", Title = "会话一", SessionId = "session-remote-a", Focused = true },
            new PtyPaneSnapshotDto { Id = 1, Kind = "chat", Title = "会话二", SessionId = "session-remote-b" },
        ]);
        chat.DaemonPtyAccess = access;
        var layout = LayoutEngine.Calculate(120, 40);
        _ = DrawFrameReturningGrid(chat, layout);

        PressCtrl(chat, ConsoleKey.X);
        Press(chat, ConsoleKey.W);
        for (var attempt = 0; attempt < 500 && !DrawFrame(chat).Contains("pty-other"); attempt++)
        {
            chat.DrainUi();
            await Task.Delay(5, TestContext.Current.CancellationToken);
        }
        for (var attempt = 0; attempt < 20 && !SelectedOverviewLine(chat).Contains("pty-other"); attempt++)
            Press(chat, ConsoleKey.UpArrow);

        Press(chat, ConsoleKey.Enter);
        for (var attempt = 0; attempt < 500 && !chat.InPtyLayer; attempt++)
        {
            chat.DrainUi();
            await Task.Delay(5, TestContext.Current.CancellationToken);
        }

        Assert.True(chat.InPtyLayer);
        Assert.False(chat.DetachRequested);
        // 别人的 pty 也不订阅镜像(它是 DSH, 不是 shell): 这一层的内容由 DSH 托的终端提供。
        Assert.Null(access.Entered);
        // 横幅把该 pty 的会话列表画出来(两个会话)。
        var frame = DrawFrame(chat);
        Assert.Contains("[pty pty-other] 层", frame);
        Assert.Contains("会话: 1:", frame);
        Assert.Contains("2:", frame);

        // Ctrl+X . 切到该 pty 的另一个会话: 请对面聚焦那个会话的窗格(既有的 focus 控制消息)。
        PressCtrl(chat, ConsoleKey.X);
        Press(chat, ConsoleKey.OemPeriod);
        for (var attempt = 0; attempt < 200 && !access.ControlSends.Any(send => send.Kind == "focus"); attempt++)
        {
            chat.DrainUi();
            await Task.Delay(5, TestContext.Current.CancellationToken);
        }
        Assert.Contains(access.ControlSends, send => send.PtyId == "pty-other" && send.Kind == "focus" && send.PaneId == 1);

        // 这一层仍然整屏响应 Ctrl+X: W 开总览。
        PressCtrl(chat, ConsoleKey.X);
        Press(chat, ConsoleKey.W);
        chat.DrainUi();
        Assert.Contains("总览 Pty", DrawFrame(chat));
        Press(chat, ConsoleKey.Escape);

        // Ctrl+X - 退出这一层。
        PressCtrl(chat, ConsoleKey.X);
        Press(chat, ConsoleKey.OemMinus);
        Assert.False(chat.InPtyLayer);
        chat.Dispose();
    }

    /**
     * 进入"本 TUI 自己所在的 pty"= 打开这一层的视图: **不 detach**, 由 DSH 为这一层托一个本地 shell
     * (该 pty 的程序是常驻 DSH, 没有 shell 可用), 底部状态行写明当前在 pty 层, Ctrl+X 仍归 DSH。
     */
    [Fact(Timeout = 5000)]
    public async Task Overview_Enter_Own_Pty_Opens_Pty_Layer_Without_Detaching()
    {
        var (chat, _, _, _, _) = await CreateChatWithTwoAgents();
        var access = new FakeDaemonAccess("pty-self", new FakeRemoteBacking("pty-self"), pid: Environment.ProcessId);
        chat.DaemonPtyAccess = access;
        var layout = LayoutEngine.Calculate(120, 40);
        _ = DrawFrameReturningGrid(chat, layout);

        PressCtrl(chat, ConsoleKey.X);
        Press(chat, ConsoleKey.W);
        for (var attempt = 0; attempt < 500 && !DrawFrame(chat).Contains("pty-self"); attempt++)
        {
            chat.DrainUi();
            await Task.Delay(5, TestContext.Current.CancellationToken);
        }
        for (var attempt = 0; attempt < 20 && !SelectedOverviewLine(chat).Contains("pty-self"); attempt++)
            Press(chat, ConsoleKey.UpArrow);
        Assert.Contains("pty-self", SelectedOverviewLine(chat));

        Press(chat, ConsoleKey.Enter);
        for (var attempt = 0; attempt < 500 && !chat.InPtyLayer; attempt++)
        {
            chat.DrainUi();
            await Task.Delay(5, TestContext.Current.CancellationToken);
        }

        // 关键: 不是 detach, 而是整屏换到"这一层"(内容由 DSH 托的本地 shell 提供), 且不与会话并排。
        Assert.False(chat.DetachRequested);
        Assert.Null(access.Entered);
        Assert.True(chat.InPtyLayer);
        Assert.Equal(3, chat.PaneCount);
        var paneId = chat.FocusedPaneId;
        var panes = await DispatchAsync(chat, chat.SnapshotPanesAsync());
        Assert.DoesNotContain(panes, entry => entry.PtyId == "pty-self");
        // 状态行(底部横幅)标明当前在 pty 层。
        Assert.Contains("[pty pty-self] 层", DrawFrame(chat));

        // 这一层仍然响应 Ctrl+X(与 tmux 的前缀键同形): 按 Ctrl+X W 能开总览。
        PressCtrl(chat, ConsoleKey.X);
        Press(chat, ConsoleKey.W);
        chat.DrainUi();
        Assert.Contains("总览 Pty", DrawFrame(chat));
        Press(chat, ConsoleKey.Escape);

        // Ctrl+X - 退出这一层, 回到会话视图(窗格数不变, 只是不再整屏显示这一层)。
        PressCtrl(chat, ConsoleKey.X);
        Press(chat, ConsoleKey.OemMinus);
        Assert.False(chat.InPtyLayer);
        Assert.Equal(2, chat.PaneCount);
        chat.Dispose();
    }

    /**
     * pty 层拉不起 shell(受限环境/GPU 模式等): 仍要开出这一层的窗格并让 Ctrl+X 快捷键可用。
     */
    [Fact(Timeout = 5000)]
    public async Task Overview_Enter_Pty_Layer_Without_Shell_Still_Handles_CtrlX()
    {
        var (chat, _, _, _, _) = await CreateChatWithTwoAgents();
        // 程序是 shell 但订阅失败: 等价于"这一层起不了 shell"。
        chat.DaemonPtyAccess = new FakeDaemonAccess("pty-broken", new FakeRemoteBacking("pty-broken"), command: "pwsh.exe -NoLogo", enterFails: true);
        var layout = LayoutEngine.Calculate(120, 40);
        _ = DrawFrameReturningGrid(chat, layout);

        PressCtrl(chat, ConsoleKey.X);
        Press(chat, ConsoleKey.W);
        for (var attempt = 0; attempt < 500 && !DrawFrame(chat).Contains("pty-broken"); attempt++)
        {
            chat.DrainUi();
            await Task.Delay(5, TestContext.Current.CancellationToken);
        }
        for (var attempt = 0; attempt < 20 && !SelectedOverviewLine(chat).Contains("pty-broken"); attempt++)
            Press(chat, ConsoleKey.UpArrow);

        Press(chat, ConsoleKey.Enter);
        for (var attempt = 0; attempt < 500 && !chat.InPtyLayer; attempt++)
        {
            chat.DrainUi();
            await Task.Delay(5, TestContext.Current.CancellationToken);
        }

        Assert.False(chat.DetachRequested);
        Assert.True(chat.InPtyLayer);
        Assert.Contains("[pty pty-broken] 层", DrawFrame(chat));

        // 没有 shell 也照样响应 Ctrl+X。
        PressCtrl(chat, ConsoleKey.X);
        Press(chat, ConsoleKey.W);
        chat.DrainUi();
        Assert.Contains("总览 Pty", DrawFrame(chat));
        chat.Dispose();
    }

    /**
     * §19 阶段 2 端到端: 真 daemon 里的真 pty → RemoteTerminalBacking → 真 ShellPane。
     * 键入必须被前台进程执行并把输出渲染进窗格(证明"进入 pty 后能跑命令", 不是只铺了张静态快照)。
     */
    [Fact(Timeout = 5000)]
    public async Task Remote_Pane_Runs_Command_Through_Real_Daemon_Pty()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var root = TempTree.CreateSocketDirectory("rpe");
        try
        {
            var socketPath = Path.Combine(root, "dsh.sock");
            string? portFile = OperatingSystem.IsWindows() ? Path.Combine(root, "dsh.port") : null;
            await using var daemon = new PtyDaemon(new PtyHost(), socketPath, portFile);
            await daemon.StartAsync(cancellationToken);
            var (fileName, arguments) = OperatingSystem.IsWindows()
                ? ("cmd.exe", (string[])["/v:on"])
                : ("/bin/sh", []);
            var started = await PtyDaemonClient.StartAsync(new PtyDaemonStartParams
            {
                FileName = fileName,
                Arguments = [.. arguments],
                Rows = 24,
                Columns = 80,
            }, socketPath, portFile, cancellationToken);

            await using var remote = await PtyDaemonClient.SubscribeAsync(started.Id, socketPath, portFile, cancellationToken);
            var (chat, _, _, _, _) = await CreateChatWithTwoAgents();
            chat.DaemonPtyAccess = FakeDaemonAccess.None;
            var paneId = 2;
            chat.AddPane(new ShellPane(chat, paneId, new RemoteTerminalBacking(remote), 80, 24), SplitOrientation.Vertical);
            chat.DrainUi();
            await WaitForPaneTextAsync(chat, paneId, HasPrompt);

            // 只在"进程真的执行"时才出现的输出(输入回显里不含最终值)。
            var (input, expected) = OperatingSystem.IsWindows()
                ? ("set /a m=6*7 & echo E2E-!m!\r", "E2E-42")
                : ("echo E2E-$((6*7))\r", "E2E-42");
            chat.ApplyPaneInput("text", paneId, input);

            Assert.Contains(expected, await WaitForPaneTextAsync(chat, paneId, text => text.Contains(expected)));
            chat.Dispose();
        }
        finally
        {
            TempTree.Delete(root);
        }
    }

    /**
     * 总览必须列出 daemon 下所有 pty, 以及每个 pty 托管的 session 与 pane(三级树 pty > session > pane)。
     * 远程 pty 的会话由它发布的窗格反推(常驻 TUI 在别的进程)。
     */
    [Fact]
    public async Task Overview_Lists_Daemon_Pty_Sessions_And_Panes()
    {
        var (chat, _, _, _, _) = await CreateChatWithTwoAgents();
        chat.DaemonPtyAccess = new FakeDaemonAccess("pty-remote", new FakeRemoteBacking("pty-remote"), panes:
        [
            new PtyPaneSnapshotDto { Id = 0, Kind = "chat", Title = "主窗格", SessionId = "session-remote-a", Focused = true },
            new PtyPaneSnapshotDto { Id = 1, Kind = "chat", Title = "副窗格", SessionId = "session-remote-a" },
            new PtyPaneSnapshotDto { Id = 2, Kind = "shell", Title = "bash", SessionId = "session-remote-b" },
        ]);
        var layout = LayoutEngine.Calculate(120, 40);
        _ = DrawFrameReturningGrid(chat, layout);

        PressCtrl(chat, ConsoleKey.X);
        Press(chat, ConsoleKey.W);
        for (var attempt = 0; attempt < 500 && !DrawFrame(chat).Contains("pty-remote"); attempt++)
        {
            chat.DrainUi();
            await Task.Delay(5, TestContext.Current.CancellationToken);
        }

        var lines = DrawFrame(chat).Split('\n').Select(line => line.TrimEnd()).ToList();
        Assert.Contains(lines, line => line.Contains("> pty-remote (daemon)"));
        Assert.Contains(lines, line => line.Contains("  session-remote-a"));
        Assert.Contains(lines, line => line.Contains("    pane 0 - session-remote-a (remote)"));
        Assert.Contains(lines, line => line.Contains("    pane 1 - session-remote-a (remote)"));
        Assert.Contains(lines, line => line.Contains("  session-remote-b"));
        Assert.Contains(lines, line => line.Contains("    pane 2 - session-remote-b (remote)"));
        chat.Dispose();
    }

    /**
     * §19 阶段 1: 总览里 Enter 一个本进程 host shell pty(尚无窗格) → 绑一个真终端窗格, 命令能跑通。
     */
    [Fact]
    public async Task Overview_Enter_Host_Shell_Pty_Opens_Terminal_Pane()
    {
        var (chat, _, _, _, _) = await CreateChatWithTwoAgents();
        chat.DaemonPtyAccess = FakeDaemonAccess.None;
        var shell = PtyShell.Resolve();
        if (!PtyShell.Exists(shell))
            return;
        var session = await PtyHost.Default.StartAsync(new PtyStartInfo
        {
            FileName = shell,
            Arguments = PtyShell.Arguments(shell),
            Rows = 24,
            Columns = 80,
        }, cancellationToken: TestContext.Current.CancellationToken);
        try
        {
            var layout = LayoutEngine.Calculate(120, 40);
            _ = DrawFrameReturningGrid(chat, layout);
            PressCtrl(chat, ConsoleKey.X);
            Press(chat, ConsoleKey.W);
            for (var attempt = 0; attempt < 20 && !SelectedOverviewLine(chat).Contains(session.Id.Value); attempt++)
                Press(chat, ConsoleKey.UpArrow);
            Assert.Contains(session.Id.Value, SelectedOverviewLine(chat));

            Press(chat, ConsoleKey.Enter);
            chat.DrainUi();
            Assert.True(chat.InPtyLayer);
            var paneId = chat.FocusedPaneId;
            // pty 层是"一层", 不进窗格目录; 但它绑定的就是这个真 shell。
            var panes = await DispatchAsync(chat, chat.SnapshotPanesAsync());
            Assert.DoesNotContain(panes, entry => entry.PtyId == session.Id.Value);

            // 键入直达这个真 shell: 输出里回显出命令即证明往返已通。
            await WaitForPaneTextAsync(chat, paneId, HasPrompt);
            chat.ApplyPaneInput("text", paneId, "echo STAGE1-MARK\r");
            Assert.Contains("STAGE1-MARK", await WaitForPaneTextAsync(chat, paneId, text => text.Contains("STAGE1-MARK")));
        }
        finally
        {
            await PtyHost.Default.StopAsync(session.Id.Value);
            chat.Dispose();
        }
    }

    /** `/session` 浮层里每一项都要能看到带时区的时间戳(参数面板分支也必须把解说词画出来)。 */
    [Fact]
    public async Task Session_Menu_Shows_Offset_Timestamp_Next_To_Title()
    {
        var (chat, _, _, _, _) = await CreateChatWithTwoAgents();
        // 单窗格: 浮层才能拿到整幅宽度, 时间戳(含时区偏移)才不会被截断。
        Assert.True(chat.CloseFocusedPane());
        var layout = LayoutEngine.Calculate(160, 40);
        _ = DrawFrameReturningGrid(chat, layout);

        Type(chat, "/session ");
        chat.DrainUi();
        var frame = DrawFrame(chat);

        Assert.Contains("/session", frame);
        Assert.Matches(@"\d{4}-\d{2}-\d{2} \d{2}:\d{2} [+-]\d{2}:\d{2}", frame);
        chat.Dispose();
    }

    /** `/session` 浮层候选: 显示"会话标题 + 带时区的时间戳", 写入值仍是会话 id(标题可能为空或重复)。 */
    [Fact]
    public async Task Session_Candidates_Show_Title_And_Offset_Timestamp()
    {
        var (chat, _, _, first, _) = await CreateChatWithTwoAgents();
        var candidates = chat.SessionCandidates();
        Assert.NotEmpty(candidates);
        var candidate = Assert.Single(candidates, entry => entry.Value == first.Id.Value);
        // 无标题时用 id 兜底显示; 时间戳必须带时区偏移。
        Assert.False(string.IsNullOrWhiteSpace(candidate.Label));
        Assert.Matches(@"^\d{4}-\d{2}-\d{2} \d{2}:\d{2} [+-]\d{2}:\d{2}$", candidate.Description);
        // 未知时间(旧日志没有 createdAt)时不显示, 免得出现 1970-01-01。
        Assert.Matches(
            @"^\d{4}-\d{2}-\d{2} \d{2}:\d{2} [+-]\d{2}:\d{2}$",
            ChatWindow.SessionTimestamp(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));
        Assert.Null(ChatWindow.SessionTimestamp(0));
        chat.Dispose();
    }

    /** 总览里被选中那一行的文本(PopupList 用 Highlight 背景标记选中项)。 */
    private static string SelectedOverviewLine(ChatWindow chat)
    {
        var layout = LayoutEngine.Calculate(120, 40);
        var grid = new CellGrid(120, 40);
        chat.Draw(grid, layout);
        for (var y = 0; y < grid.Height; y++)
        {
            var selected = false;
            var text = new StringBuilder();
            for (var x = 0; x < grid.Width; x++)
            {
                var cell = grid[x, y];
                text.Append(cell.Character);
                selected |= cell.Background == TuiTheme.Highlight;
            }

            if (selected)
                return text.ToString();
        }

        return "";
    }

    private static async Task<T> DispatchAsync<T>(ChatWindow chat, Task<T> task)
    {
        for (var attempt = 0; attempt < 500 && !task.IsCompleted; attempt++)
        {
            chat.DrainUi();
            await Task.Delay(5, TestContext.Current.CancellationToken);
        }
        return await task;
    }

    private static async Task<string> PaneTextAsync(ChatWindow chat, int paneId)
    {
        var lines = await DispatchAsync(chat, chat.SnapshotPaneLinesAsync(paneId));
        return lines is null ? "" : string.Join('\n', lines);
    }

    /**
     * Polls the pane until the condition holds or ShellOutputTimeout passes, and returns the last text for the assertion message.
     * The bound is wall-clock time: an attempt-count loop ran far longer on Windows, where each short delay rounds up to the 15.6 ms timer tick.
     */
    private static async Task<string> WaitForPaneTextAsync(ChatWindow chat, int paneId, Func<string, bool> condition)
    {
        var deadline = Stopwatch.StartNew();
        var text = await PaneTextAsync(chat, paneId);
        while (!condition(text) && deadline.Elapsed < ShellOutputTimeout)
        {
            await Task.Delay(PanePollInterval, TestContext.Current.CancellationToken);
            text = await PaneTextAsync(chat, paneId);
        }

        return text;
    }

    /** A shell reads typed input only after it has drawn its prompt: pwsh dropped keys typed earlier (CI: STAGE1-MARK never echoed). */
    private static bool HasPrompt(string paneText) => paneText.Trim().Length > 0;

    /**
     * Ctrl+X ,/. 是"在当前 pty 的会话列表里切换"(pty 是 session 的上一层容器, 一个 pty 可挂多个会话):
     * 目标会话没有窗格时, 把它装进当前窗格(而不是只能在没有窗格时才失败/跳过)。
     */
    [Fact]
    public async Task CtrlX_Period_Loads_Session_Without_Pane_Into_Current_Pane()
    {
        var ctx = new Context();
        _ = new SessionStore(ctx);
        var agents = new AgentRegistry(ctx);
        _ = new AgentLoop(ctx);
        CommandsService.Register(ctx);
        var first = await CreateAgent(agents);
        var second = await CreateAgent(agents);
        var chat = new ChatWindow(ctx, first, HarnessHome.Resolve(_homeDir));
        Assert.Equal(1, chat.PaneCount);
        var layout = LayoutEngine.Calculate(120, 40);
        _ = DrawFrameReturningGrid(chat, layout);

        PressCtrl(chat, ConsoleKey.X);
        Press(chat, ConsoleKey.OemPeriod);

        for (var attempt = 0; attempt < 500 && chat.InputPane.Agent.Id != second.Id; attempt++)
        {
            chat.DrainUi();
            await Task.Delay(5, TestContext.Current.CancellationToken);
        }

        Assert.Equal(second.Id, chat.InputPane.Agent.Id);
        Assert.Equal(1, chat.PaneCount);
        chat.Dispose();
    }


    /** 主题色接进渲染: 箭头/分隔线/状态栏取 TuiTheme 的可配值, 窗格标题仍取 Highlight(§8)。 */
    [Fact]
    public async Task Theme_Colors_Reach_Arrow_Divider_And_Status()
    {
        var (chat, _, _, _, _) = await CreateChatWithTwoAgents();
        var (arrow, divider, status) = (TuiTheme.Arrow, TuiTheme.Divider, TuiTheme.Status);
        var configuredArrow = CellColor.FromRgb(0x0a, 0x0b, 0x0c);
        var configuredDivider = CellColor.FromRgb(0x1a, 0x1b, 0x1c);
        var configuredStatus = CellColor.FromRgb(0x2a, 0x2b, 0x2c);
        try
        {
            TuiTheme.Arrow = configuredArrow;
            TuiTheme.Divider = configuredDivider;
            TuiTheme.Status = configuredStatus;
            var layout = LayoutEngine.Calculate(120, 40);
            var grid = DrawFrameReturningGrid(chat, layout);

            // 聚焦窗格头: 行首箭头用 arrowColor, 标题用 Highlight(分屏时聚焦窗格不在第 0 列, 故按 ">" 定位)。
            var headerX = -1;
            for (var x = 0; x < grid.Width; x++)
            {
                if (grid[x, 0].Character == '>')
                {
                    headerX = x;
                    break;
                }
            }

            Assert.True(headerX >= 0, "聚焦窗格头应当画了 > 箭头");
            Assert.Equal(configuredArrow, grid[headerX, 0].Foreground);
            Assert.Equal(TuiTheme.Highlight, grid[headerX + 2, 0].Foreground);
            // 输入栏上方的横分隔线整行用 dividerColor。
            var dividerRow = layout.Input.Y - 1;
            Assert.Equal(configuredDivider, grid[0, dividerRow].Foreground);
            Assert.Equal(configuredDivider, grid[grid.Width - 1, dividerRow].Foreground);
            // 状态栏文本用 statusColor。
            Assert.Equal(configuredStatus, grid[0, layout.Status.Y].Foreground);
        }
        finally
        {
            TuiTheme.Arrow = arrow;
            TuiTheme.Divider = divider;
            TuiTheme.Status = status;
        }

        chat.Dispose();
    }

    /** 失败的工具结果整行按 TuiTheme.Error 着色(§8): 新建窗口时回放既有历史即可看到。 */
    [Fact]
    public async Task Failed_Tool_Result_Uses_Error_Theme_Color()
    {
        var ctx = new Context();
        _ = new SessionStore(ctx);
        var agents = new AgentRegistry(ctx);
        _ = new AgentLoop(ctx);
        CommandsService.Register(ctx);
        var handle = await agents.Create(new CreateAgentOptions(
            SessionId.Create($"session-{Guid.NewGuid():N}"),
            null,
            new AgentOptions("deepseek-official", "deepseek-v4-flash")), TestContext.Current.CancellationToken);
        var agent = (AgentLoopAgent)handle.Agent;
        await agent.WhenIdle();
        var callId = ToolCallId.Create("call-error");
        agent.Session.Append(new TurnStartPayload(1));
        agent.Session.Append(new ToolCallPayload(1, 1, callId, "bash", """{"command":"false"}"""));
        agent.Session.Append(
            new ToolResultPayload(
                1,
                1,
                MessageFactory.CreateToolResultMessage(callId, [new TextBlock("bash 失败输出")], true),
                new ToolResultErrorInfo("bash", "TOOL_FAILED")),
            new SurfaceOp.Append());
        agent.Session.Append(new TurnEndPayload(1, new TurnEndReason.Completed()));

        // 窗口后建 → 走回放路径(用户消息只在回放时渲染, 工具结果两条路都渲染)。
        using var chat = new ChatWindow(ctx, agent, HarnessHome.Resolve(_homeDir));
        var layout = LayoutEngine.Calculate(120, 40);
        var grid = DrawFrameReturningGrid(chat, layout);
        var rows = new List<string>();
        for (var y = 0; y < grid.Height; y++)
        {
            var chars = new char[grid.Width];
            for (var x = 0; x < grid.Width; x++)
                chars[x] = grid[x, y].Character;
            rows.Add(new string(chars).Replace("\0", ""));
        }

        var row = rows.FindIndex(line => line.Contains("bash 失败输出", StringComparison.Ordinal));
        Assert.True(row >= 0, "失败的工具结果应当出现在转录里");
        var column = rows[row].IndexOf("bash 失败输出", StringComparison.Ordinal);
        Assert.Equal(TuiTheme.Error, grid[column, row].Foreground);
    }

    private async Task<(ChatWindow Chat, Context Ctx, AgentRegistry Agents, AgentLoopAgent First, AgentLoopAgent Second)> CreateChatWithTwoAgents(
        TuiSettings? settings = null)
    {
        var ctx = new Context();
        _ = new SessionStore(ctx);
        var agents = new AgentRegistry(ctx);
        _ = new AgentLoop(ctx);
        CommandsService.Register(ctx);
        var first = await CreateAgent(agents);
        var second = await CreateAgent(agents);
        var chat = new ChatWindow(ctx, first, HarnessHome.Resolve(_homeDir), null, settings);
        chat.AddPane(second, SplitOrientation.Vertical);
        return (chat, ctx, agents, first, second);
    }

    /** settings.yaml 的 tui 配色参数要真的生效(settings → TuiTheme → 渲染): 写 highlightColor/arrowColor 后标题与箭头用它。 */
    [Fact]
    public async Task TuiSettings_Colors_Reach_Rendered_Title_And_Arrow()
    {
        Directory.CreateDirectory(_homeDir);
        File.WriteAllText(
            Path.Combine(_homeDir, "settings.yaml"),
            "plugins:\n  \"@deepseek-ai/dsh-tui\":\n    highlightColor: \"#0a0b0c\"\n    arrowColor: \"#010203\"\n");
        var (arrow, highlight) = (TuiTheme.Arrow, TuiTheme.Highlight);
        try
        {
            var (chat, _, _, _, _) = await CreateChatWithTwoAgents(new TuiSettings(HarnessHome.Resolve(_homeDir)));
            var grid = DrawFrameReturningGrid(chat, LayoutEngine.Calculate(120, 40));
            var headerX = -1;
            for (var x = 0; x < grid.Width; x++)
            {
                if (grid[x, 0].Character == '>')
                {
                    headerX = x;
                    break;
                }
            }

            Assert.True(headerX >= 0, "聚焦窗格头应当画了 > 箭头");
            Assert.Equal(CellColor.FromRgb(0x01, 0x02, 0x03), grid[headerX, 0].Foreground);
            Assert.Equal(CellColor.FromRgb(0x0a, 0x0b, 0x0c), grid[headerX + 2, 0].Foreground);
            chat.Dispose();
        }
        finally
        {
            TuiTheme.Arrow = arrow;
            TuiTheme.Highlight = highlight;
        }
    }

    /** 假的 daemon pty 访问: 发布一个远端 pty(ptyId 为 null 时列表为空), 进入时交出记录用的假终端 backing。 */
    private sealed class FakeDaemonAccess(
        string? ptyId,
        FakeRemoteBacking? backing,
        int? pid = null,
        IReadOnlyList<PtyPaneSnapshotDto>? panes = null,
        string command = "dsh tui",
        bool enterFails = false) : IDaemonPtyAccess
    {
        /** 没有远端 pty: 让总览只反映本进程的 host pty。 */
        public static FakeDaemonAccess None { get; } = new(null, null);

        public (string PtyId, int Rows, int Columns)? Entered { get; private set; }

        public List<(string PtyId, string Kind, int PaneId)> ControlSends { get; } = [];

        public Task<IReadOnlyList<PtyDaemonSessionDto>> ListAsync()
            => Task.FromResult<IReadOnlyList<PtyDaemonSessionDto>>(ptyId is null
                ? []
                :
                [
                    new PtyDaemonSessionDto
                    {
                        Id = ptyId,
                        Command = command,
                        Status = nameof(PtySessionStatus.Running),
                        Columns = 80,
                        Rows = 24,
                        Pid = pid,
                        Panes = panes is null ? null : [.. panes],
                    },
                ]);

        public Task ControlSendAsync(string ptyId, string kind, int paneId, string payload)
        {
            ControlSends.Add((ptyId, kind, paneId));
            return Task.CompletedTask;
        }

        public Task<ITerminalBacking> EnterAsync(string id, int rows, int columns)
        {
            Entered = (id, rows, columns);
            if (enterFails)
                return Task.FromException<ITerminalBacking>(new InvalidOperationException("subscribe failed"));
            return Task.FromResult<ITerminalBacking>(backing
                ?? throw new NotSupportedException("this fake publishes no daemon pty"));
        }
    }

    /** 假终端 backing: 手动喂输出、记录写入, 用于验证远程窗格的渲染与输入路径。 */
    private sealed class FakeRemoteBacking(string ptyId) : ITerminalBacking
    {
        private readonly Channel<byte[]> _output = Channel.CreateUnbounded<byte[]>();
        private PtyOutputReader? _reader;

        public List<string> Written { get; } = [];

        public string Command => "dsh tui";

        public int? ProcessId => null;

        public string PtyId => ptyId;

        public bool Exited { get; private set; }

        public bool Disposed { get; private set; }

        public void Push(string text) => _output.Writer.TryWrite(Encoding.UTF8.GetBytes(text));

        public ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            => (_reader ??= new PtyOutputReader(_output.Reader)).ReadAsync(buffer, cancellationToken);

        public ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
        {
            Written.Add(Encoding.UTF8.GetString(data.Span));
            return ValueTask.CompletedTask;
        }

        public void Resize(int rows, int columns)
        {
        }

        public void Dispose()
        {
            Disposed = true;
            Exited = true;
            _output.Writer.TryComplete();
        }
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

    /** §6 D3: /timestamp revert 就地截断会话并把被选消息回填输入区。 */
    [Fact]
    public async Task Timestamp_Revert_Truncates_Session_And_Refills_Input()
    {
        var (chat, _, _, _, _) = await CreateChatWithTwoAgents();
        var agent = chat.InputPane.Agent;
        agent.Session.Append(new UserMessagePayload(MessageFactory.CreateUserText("第一条")), new SurfaceOp.Append());
        agent.Session.Append(new UserMessagePayload(MessageFactory.CreateUserText("第二条")), new SurfaceOp.Append());
        var targetSeq = agent.Session.SnapshotEvents().Last(entry => entry.Data is UserMessagePayload).Seq;

        chat.RunSlashCommand(chat.InputPane, $"/timestamp revert {targetSeq}");
        for (var attempt = 0; attempt < 200 && agent.Session.Seq != targetSeq; attempt++)
        {
            chat.DrainUi();
            await Task.Delay(5, TestContext.Current.CancellationToken);
        }

        Assert.Equal("第二条", chat.InputPane.Input);
        Assert.Equal(targetSeq, agent.Session.Seq);
        chat.Dispose();
    }

    /**
     * 终端把东亚宽度 Ambiguous 的字符(分隔线/浮层边框/几何符号)按两格渲染时(简中控制台常见),
     * 渲染输出必须仍与网格一一对齐 —— 布局按一格、终端按两格会让整行右移并在分屏里撕裂。
     * 见 TerminalSafeGlyphs: 落屏前把这些字符换成等宽 ASCII。
     */
    [Fact]
    public async Task Split_Menu_Renders_Aligned_On_Ambiguous_Wide_Terminal()
    {
        var (chat, _, _, first, _) = await CreateChatWithTwoAgents();
        first.Session.Append(new UserMessagePayload(MessageFactory.CreateUserText("检查中文宽度渲染")), new SurfaceOp.Append());

        chat.HandleKey(new ConsoleKeyInfo('/', ConsoleKey.NoName, false, false, false));
        var layout = LayoutEngine.Calculate(120, 40);
        var grid = new CellGrid(120, 40);
        chat.Draw(grid, layout);
        var output = new AnsiRenderer()
            .RenderToBuffer(grid, chat.CursorScreenX, chat.CursorScreenY, forceFull: true)
            .ToString();

        var screen = new TerminalScreenSim(120, 40, ambiguousWide: true);
        screen.Feed(output);
        Assert.True(screen.Matches(grid), screen.Explain(grid));
        chat.Dispose();
    }

    public void Dispose() => TempTree.Delete(_homeDir);
}
