using Cordis;
using Dsh.Core;
using Dsh.Terminal;
using Dsh.Tools;

namespace Dsh.Tests;

public class TerminalBashLocalTests : IDisposable
{
    private readonly Context _ctx = new();
    private readonly SubprocessService _subprocess;
    private readonly AgentRegistry _agents;
    private readonly TerminalSessionService _terminals;
    private readonly string _tempDir;
    private readonly bool _bashAvailable;

    public TerminalBashLocalTests()
    {
        _subprocess = new SubprocessService(_ctx);
        _agents = new AgentRegistry(_ctx);
        _terminals = new TerminalSessionService(_ctx);
        _tempDir = Path.Combine(Path.GetTempPath(), $"dsh-terminal-bash-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        _bashAvailable = IsBashAvailable();
    }

    public void Dispose()
    {
        _subprocess.Dispose();
        try
        {
            Directory.Delete(_tempDir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private bool IsBashAvailable()
    {
        try
        {
            var path = _subprocess.ResolveExecutable("bash", signal: default).GetAwaiter().GetResult();
            if (OperatingSystem.IsWindows() && path.StartsWith(Environment.SystemDirectory, StringComparison.OrdinalIgnoreCase))
                return false;
            return true;
        }
        catch (FileNotFoundException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    [Fact]
    public async Task PipeBash_PreservesStateAndCloses()
    {
        if (!_bashAvailable)
            return;
        TerminalBash.Register(_ctx, new TerminalBashConfig
        {
            BackendType = "shell",
            ShellDialect = ShellDialect.Bash,
            Rows = 24,
            Cols = 80,
            PollIntervalMs = 10,
            ExactProbeAfterMs = 20,
            IdleSilenceMs = 100,
            HandoffGraceMs = 100,
            TimeoutMs = 2_000,
            DisposeGraceMs = 500,
            ScrollbackLines = 100,
            ScrollbackMaxBytes = 32_768,
            MaxReadBytes = 16_384,
        });
        var agent = new TerminalFakeAgent(_ctx, _agents, _tempDir);
        agent.Register();
        var created = await _terminals.Spawn(agent, new TerminalSpawnRequest("shell", "main", _tempDir));
        Assert.Equal("shell", created.Type);

        var first = _terminals.StartSend(agent, created.SessionId, new TerminalSendRequest("export KEEP=ok; cd /", true));
        await first.Done;
        var second = _terminals.StartSend(agent, created.SessionId, new TerminalSendRequest("printf \"cwd=%s keep=%s\\n\" \"$PWD\" \"$KEEP\"", true));
        var result = await second.Done;
        Assert.Contains("cwd=/ keep=ok", result.Viewport);

        Assert.True(await _terminals.Kill(agent, created.SessionId));
        Assert.Empty(_terminals.List(agent));
    }
}
