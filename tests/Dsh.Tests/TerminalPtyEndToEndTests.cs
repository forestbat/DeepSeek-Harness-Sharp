using Dsh.Core;
using Dsh.PtyTerminal;
using Dsh.Runtime;

namespace Dsh.Tests;

/**
 * 真 PTY 端到端: 不 mock, 走真 shell + 真 PTY, 只通过 terminal_* 工具驱动。
 * 覆盖: open(type=shell) → send 执行命令 → read 读到输出 → signal interrupt 后仍可用 → close 正常关闭。
 * 归入串行集合: 并发启动 ConPTY 会触发 Dsh.Pty 的全局 std 句柄竞争, 单独跑稳定通过。
 */
[Collection(SerialProcessCollection.Name)]
public class TerminalPtyEndToEndTests
{
    private static readonly PtyTerminalConfig FastConfig = new()
    {
        Rows = 24,
        Cols = 80,
        PollIntervalMs = 25,
        IdleSilenceMs = 500,
        HandoffGraceMs = 100,
        TimeoutMs = 20_000,
        DisposeGraceMs = 2_000,
        ScrollbackLines = 1_000,
        ScrollbackMaxBytes = 1_048_576,
        MaxReadBytes = 256 * 1024,
    };

    [Fact]
    public async Task ShellToolLoop_ExecutesCommandSurvivesInterruptAndCloses()
    {
        var resolved = PtyTerminalConfigResolver.Resolve(FastConfig);
        if (!ShellAvailable(resolved.Command))
            Assert.Skip($"no usable shell on this platform: {resolved.Command}");

        var ctx = new Context();
        _ = new SystemPrompt(ctx, new SystemPromptConfig());
        var agents = new AgentRegistry(ctx);
        var tools = new ToolRuntime(ctx);
        _ = new TerminalSessionService(ctx);
        using var registration = PtyTerminalPlugin.Register(ctx, FastConfig);
        TerminalTools.Register(ctx);
        var agent = new TerminalFakeAgent(ctx, agents, Path.GetTempPath());
        agent.Register();

        var opened = await TerminalTestTools.Execute(tools, "terminal_open", new { type = "shell" }, agent);
        Assert.False(opened.IsError, TerminalTestTools.TextOf(opened));
        var openValue = Assert.IsType<ToolExecutionResult.Success>(opened).Value;
        Assert.Equal("shell", openValue.GetProperty("type").GetString());
        Assert.Contains("real PTY", openValue.GetProperty("motd").GetString());
        var sessionId = openValue.GetProperty("sessionId").GetString()!;

        var sent = await TerminalTestTools.Execute(tools, "terminal_send", new { sessionId, text = "echo dsh-pty-probe" }, agent);
        Assert.False(sent.IsError, TerminalTestTools.TextOf(sent));
        var probe = await ReadUntilAsync(tools, agent, sessionId, "dsh-pty-probe", TimeSpan.FromSeconds(20));
        Assert.Contains("dsh-pty-probe", probe);

        var signalled = await TerminalTestTools.Execute(tools, "terminal_signal", new { sessionId, signal = "SIGINT" }, agent);
        Assert.False(signalled.IsError, TerminalTestTools.TextOf(signalled));
        await Task.Delay(500, TestContext.Current.CancellationToken);

        var afterInterrupt = await SendUntilAsync(tools, agent, sessionId, "echo dsh-pty-after-interrupt", "dsh-pty-after-interrupt", TimeSpan.FromSeconds(8));
        Assert.Contains("dsh-pty-after-interrupt", afterInterrupt);

        var closed = await TerminalTestTools.Execute(tools, "terminal_close", new { sessionId }, agent);
        Assert.False(closed.IsError, TerminalTestTools.TextOf(closed));
        var closedValue = Assert.IsType<ToolExecutionResult.Success>(closed).Value;
        Assert.Equal("closed", closedValue.GetProperty("outcome").GetString());
        Assert.Equal("(no terminal sessions)", TerminalTestTools.TextOf(await TerminalTestTools.Execute(tools, "terminal_list", new { }, agent)));
    }

    private static async Task<string> ReadUntilAsync(ToolRuntime tools, IAgent agent, string sessionId, string marker, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        var text = "";
        while (DateTime.UtcNow < deadline)
        {
            var read = await TerminalTestTools.Execute(tools, "terminal_read", new { sessionId, count = 500 }, agent);
            text = TerminalTestTools.TextOf(read);
            if (text.Contains(marker, StringComparison.Ordinal))
                return text;
            await Task.Delay(50, TestContext.Current.CancellationToken);
        }
        return text;
    }

    /** interrupt 后 shell 可能正忙, 同一条命令最多重发三次, 任一发送被回显即视为会话仍可用。 */
    private static async Task<string> SendUntilAsync(ToolRuntime tools, IAgent agent, string sessionId, string command, string marker, TimeSpan timeout)
    {
        var text = "";
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var sent = await TerminalTestTools.Execute(tools, "terminal_send", new { sessionId, text = command }, agent);
            Assert.False(sent.IsError, TerminalTestTools.TextOf(sent));
            text = await ReadUntilAsync(tools, agent, sessionId, marker, timeout);
            if (text.Contains(marker, StringComparison.Ordinal))
                return text;
            await Task.Delay(200, TestContext.Current.CancellationToken);
        }
        return text;
    }

    private static bool ShellAvailable(string command)
        => OperatingSystem.IsWindows() || !Path.IsPathRooted(command) || File.Exists(command);
}
