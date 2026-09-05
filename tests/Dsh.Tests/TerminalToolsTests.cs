using Cordis;
using Dsh.Core;
using Dsh.Jobs;
using Dsh.Llm;
using Dsh.Terminal;

namespace Dsh.Tests;

public class TerminalToolsTests
{
    private sealed class Harness : IDisposable
    {
        public Harness(bool jobs, TerminalToolsConfig? config = null)
        {
            Ctx = new Context();
            _ = new SystemPrompt(Ctx, new SystemPromptConfig());
            Tools = new ToolRuntime(Ctx);
            Agents = new AgentRegistry(Ctx);
            Terminals = new TerminalSessionService(Ctx);
            Backend = new StubTerminalBackend("stub");
            Terminals.RegisterBackend(Backend);
            TempDir = Path.Combine(Path.GetTempPath(), $"dsh-terminal-tools-{Guid.NewGuid():N}");
            Directory.CreateDirectory(TempDir);
            if (jobs)
            {
                _ = new LocalJobsService(Ctx);
                ToolJobs.Register(Ctx);
            }
            TerminalTools.Register(Ctx, config);
            Agent = new TerminalFakeAgent(Ctx, Agents, TempDir);
            Agent.Register();
        }

        public Context Ctx { get; }
        public ToolRuntime Tools { get; }
        public AgentRegistry Agents { get; }
        public TerminalSessionService Terminals { get; }
        public StubTerminalBackend Backend { get; }
        public TerminalFakeAgent Agent { get; }
        public string TempDir { get; }

        public Task<ToolExecutionResult> Execute(string name, object arguments, IAgent? agent = null)
            => TerminalTestTools.Execute(Tools, name, arguments, agent ?? Agent);

        public static string TextOf(ToolExecutionResult result) => TerminalTestTools.TextOf(result);

        public void Dispose()
        {
            try
            {
                Directory.Delete(TempDir, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    [Fact]
    public async Task RegistersSixSchemasAndDrivesFullOwnerScopedLifecycle()
    {
        using var h = new Harness(false);
        foreach (var name in new[] { "terminal_open", "terminal_send", "terminal_read", "terminal_signal", "terminal_close", "terminal_list" })
            Assert.NotNull(h.Tools.Get(name));

        var spawned = await h.Execute("terminal_open", new { type = "stub", name = "main" });
        Assert.False(spawned.IsError);
        Assert.Contains("started terminal session pty-1 (main)", Harness.TextOf(spawned));
        var spawnValue = Assert.IsType<ToolExecutionResult.Success>(spawned).Value;
        Assert.Equal("pty-1", spawnValue.GetProperty("sessionId").GetString());
        Assert.Equal("stub", spawnValue.GetProperty("type").GetString());
        Assert.Equal("running", spawnValue.GetProperty("status").GetProperty("kind").GetString());

        var listed = await h.Execute("terminal_list", new { });
        Assert.Contains("pty-1 (main) [stub] running pid=42", Harness.TextOf(listed));

        var read = await h.Execute("terminal_read", new { sessionId = "pty-1" });
        Assert.Contains("history\n[lines: 0-1 of 1]", Harness.TextOf(read));

        var signal = await h.Execute("terminal_signal", new { sessionId = "pty-1", signal = "SIGINT" });
        Assert.Equal("delivered SIGINT to foreground process group 10", Harness.TextOf(signal));

        var sent = await h.Execute("terminal_send", new { sessionId = "pty-1", text = "echo hi" });
        Assert.False(sent.IsError);
        var sendValue = Assert.IsType<ToolExecutionResult.Success>(sent).Value;
        Assert.Equal("foreground", sendValue.GetProperty("kind").GetString());
        Assert.Equal("command output", sendValue.GetProperty("viewport").GetString());
        Assert.Equal("stdin_read", sendValue.GetProperty("waitReason").GetString());
        Assert.Contains("[wait: stdin_read]\n[session: running]", Harness.TextOf(sent));

        var closed = await h.Execute("terminal_close", new { sessionId = "pty-1" });
        Assert.Equal("closed terminal session pty-1", Harness.TextOf(closed));
        Assert.Equal("(no terminal sessions)", Harness.TextOf(await h.Execute("terminal_list", new { })));
    }

    [Fact]
    public async Task FailsWithoutAgentAndRejectsBackgroundWhenDisabled()
    {
        using var h = new Harness(false, new TerminalToolsConfig { EnableRunInBackground = false });
        Assert.True((await TerminalTestTools.Execute(h.Tools, "terminal_open", new { type = "stub" }, null)).IsError);
        await h.Execute("terminal_open", new { type = "stub" });
        var background = await h.Execute("terminal_send", new { sessionId = "pty-1", text = "sleep 1", run_in_background = true });
        Assert.True(background.IsError);
        Assert.Null(h.Backend.Sessions[0].Operation);
    }

    [Fact]
    public async Task ValidatesRequiredValuesAndForwardsOptionalArguments()
    {
        using var h = new Harness(false);
        Assert.True((await h.Execute("terminal_open", new { type = "" })).IsError);
        Assert.True((await h.Execute("terminal_send", new { sessionId = "", text = "x" })).IsError);
        Assert.True((await h.Execute("terminal_send", new { sessionId = 1, text = "x" })).IsError);
        await h.Execute("terminal_open", new { type = "stub", name = "named", cwd = h.TempDir });
        var read = await h.Execute("terminal_read", new { sessionId = "pty-1", offset = 2, count = 3 });
        Assert.Contains("history", Harness.TextOf(read));
    }

    [Fact]
    public async Task ConfigurationGatesBackgroundSendsAndValidatesFinalResultBound()
    {
        using var h = new Harness(true, new TerminalToolsConfig { EnableRunInBackground = false });
        var definition = h.Tools.Get("terminal_send")!;
        Assert.False(definition.Parameters["properties"]!.AsObject().ContainsKey("run_in_background"));
        Assert.DoesNotContain("Background mode", definition.Description);
        await h.Execute("terminal_open", new { type = "stub" });
        Assert.True((await h.Execute("terminal_send", new { sessionId = "pty-1", text = "work", run_in_background = true })).IsError);

        var invalidCtx = new Context();
        _ = new SystemPrompt(invalidCtx, new SystemPromptConfig());
        _ = new ToolRuntime(invalidCtx);
        _ = new TerminalSessionService(invalidCtx);
        Assert.Throws<InvalidOperationException>(() => TerminalTools.Register(invalidCtx, new TerminalToolsConfig { MaxResultBytes = 0 }));
        Assert.Throws<InvalidOperationException>(() => TerminalTools.Register(invalidCtx, new TerminalToolsConfig { MaxResultBytes = 63 }));
    }

    [Fact]
    public async Task Background_RegistersJobAndExposesIncrementalOutput()
    {
        using var h = new Harness(true);
        await h.Execute("terminal_open", new { type = "stub" });
        var started = await h.Execute("terminal_send", new { sessionId = "pty-1", text = "build", run_in_background = true });
        Assert.False(started.IsError);
        Assert.Equal("started background job pty-send-1", Harness.TextOf(started));
        var startValue = Assert.IsType<ToolExecutionResult.Success>(started).Value;
        Assert.Equal("background", startValue.GetProperty("kind").GetString());
        Assert.Equal("pty-send-1", startValue.GetProperty("jobId").GetString());

        var output = await h.Execute("job_output", new { job_id = "pty-send-1", wait = true });
        Assert.Contains("live output", Harness.TextOf(output));
        Assert.Contains("[status: completed, wait: stdin_read]", Harness.TextOf(output));
    }

    [Fact]
    public async Task BoundsForegroundAndBackgroundResultsAfterTerminalAndTaskMetadata()
    {
        using var h = new Harness(true, new TerminalToolsConfig { MaxResultBytes = 64 });
        await h.Execute("terminal_open", new { type = "stub" });
        h.Backend.Sessions[0].Viewport = new string('界', 100);
        var foreground = await h.Execute("terminal_send", new { sessionId = "pty-1", text = "foreground" });
        Assert.True(System.Text.Encoding.UTF8.GetByteCount(Harness.TextOf(foreground)) <= 64);

        h.Backend.Sessions[0].Delta = new string('界', 100);
        h.Backend.Sessions[0].DeltaTruncated = true;
        await h.Execute("terminal_send", new { sessionId = "pty-1", text = "background", run_in_background = true });
        var background = await h.Execute("job_output", new { job_id = "pty-send-1", wait = true });
        Assert.True(System.Text.Encoding.UTF8.GetByteCount(Harness.TextOf(background)) <= 64);
        Assert.Contains("[status: completed", Harness.TextOf(background));
    }

    [Fact]
    public async Task RendersAlreadyClosingKillResult()
    {
        using var h = new Harness(false);
        await h.Execute("terminal_open", new { type = "stub" });
        h.Backend.Sessions[0].CloseGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = h.Terminals.Kill(h.Agent, new TerminalSessionId("pty-1"));
        var second = h.Execute("terminal_close", new { sessionId = "pty-1" });
        h.Backend.Sessions[0].CloseGate.SetResult();
        await first;
        var result = await second;
        Assert.Equal("terminal session pty-1 was already closing", Harness.TextOf(result));
        var value = Assert.IsType<ToolExecutionResult.Success>(result).Value;
        Assert.Equal("already-closing", value.GetProperty("outcome").GetString());
    }
}
