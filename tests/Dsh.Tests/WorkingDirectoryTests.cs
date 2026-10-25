using System.Diagnostics;
using System.Text.Json;
using Dsh.Core;
using Dsh.Interaction;
using Dsh.Jobs;
using Dsh.Llm;
using Dsh.Runtime;
using Dsh.Tools;

namespace Dsh.Tests;

/** working_directory / create_worktree: the session directory moves, the write permission root (header cwd) does not. */
[Collection(SerialProcessCollection.Name)]
public sealed class WorkingDirectoryTests : IDisposable
{
    private readonly Context _ctx = new();
    private readonly ToolRuntime _tools;
    private readonly string _root;
    private readonly string _outside;
    private readonly WorkingDirectoryAgent _agent;

    public WorkingDirectoryTests()
    {
        _ = new SystemPrompt(_ctx, new SystemPromptConfig());
        _tools = new ToolRuntime(_ctx);
        _ = new SubprocessService(_ctx);
        ReadTool.Register(_ctx);
        WriteTool.Register(_ctx);
        BashTool.Register(_ctx);
        PwshTool.Register(_ctx);
        WindowsPowerShellTool.Register(_ctx);
        WslTool.Register(_ctx);
        PersistentBashTool.Register(_ctx);
        PersistentPwshTool.Register(_ctx);
        WorkingDirectoryTool.Register(_ctx);
        WorktreeTool.Register(_ctx);
        _root = TempTree.CreateDirectory("wd-root");
        _outside = TempTree.CreateDirectory("wd-outside");
        _agent = new WorkingDirectoryAgent(_ctx, _root);
    }

    public void Dispose()
    {
        _ctx.Get<SubprocessService>(SubprocessService.ServiceName)!.Dispose();
        TempTree.Delete(_root);
        TempTree.Delete(_outside);
    }

    private Task<ToolExecutionResult> Execute(string name, object arguments)
        => _tools.Execute(new ToolExecutionInput
        {
            CallId = ToolCallId.Create($"call-{Guid.NewGuid():N}"),
            Name = name,
            Arguments = JsonSerializer.SerializeToElement(arguments, DshJson.Options),
            Agent = _agent,
            Signal = TestContext.Current.CancellationToken,
        });

    private static JsonElement ValueOf(ToolExecutionResult result)
        => Assert.IsType<ToolExecutionResult.Success>(result).Value;

    private static string TextOf(ToolExecutionResult result)
        => string.Concat(result.Content.OfType<TextBlock>().Select(block => block.Text));

    private List<ApprovalAskedPayload> Asked()
        => _agent.Session.SnapshotEvents().Select(sessionEvent => sessionEvent.Data).OfType<ApprovalAskedPayload>().ToList();

    private async Task<string> Cd(string path)
    {
        var result = await Execute(WorkingDirectoryTool.ToolName, new { cd = path });
        Assert.False(result.IsError, TextOf(result));
        return ValueOf(result).GetProperty("cwd").GetString()!;
    }

    [Fact]
    public async Task Read_ReturnsSessionDirectory_AndCdResolvesRelativeToCurrent()
    {
        Directory.CreateDirectory(Path.Combine(_root, "sub", "deeper"));

        var initial = await Execute(WorkingDirectoryTool.ToolName, new { });
        var sub = await Cd("sub");
        var deeper = await Cd("deeper");

        Assert.Equal(_root, ValueOf(initial).GetProperty("cwd").GetString());
        Assert.Equal(Path.Combine(_root, "sub"), sub);
        Assert.Equal(Path.Combine(_root, "sub", "deeper"), deeper);
        Assert.Equal(deeper, _agent.Session.WorkingDirectory);
        Assert.Equal(_root, _agent.Session.Header.Cwd);
    }

    [Fact]
    public async Task Cd_ToMissingDirectory_Fails_AndKeepsCurrent()
    {
        var result = await Execute(WorkingDirectoryTool.ToolName, new { cd = "missing" });

        Assert.True(result.IsError);
        Assert.Equal(_root, _agent.Session.WorkingDirectory);
    }

    [Fact]
    public async Task RelativeFilePaths_ResolveAgainstChangedDirectory()
    {
        Directory.CreateDirectory(Path.Combine(_root, "sub"));
        await Cd("sub");

        var write = await Execute(WriteTool.ToolName, new { file_path = "note.txt", content = "moved" });
        var read = await Execute("read", new { file_path = "note.txt" });

        Assert.False(write.IsError, TextOf(write));
        Assert.Equal("moved", await File.ReadAllTextAsync(Path.Combine(_root, "sub", "note.txt"), TestContext.Current.CancellationToken));
        Assert.Contains("1: moved", TextOf(read));
        Assert.Empty(Asked());
    }

    [Fact]
    public async Task NewBashProcess_StartsInChangedDirectory()
    {
        if (!ShellProbe.Bash)
            Assert.Skip("bash 不可用(WSL/Git Bash 无响应), 跳过依赖外部 shell 的用例");
        Directory.CreateDirectory(Path.Combine(_root, "sub"));
        await File.WriteAllTextAsync(Path.Combine(_root, "sub", "marker.txt"), "", TestContext.Current.CancellationToken);
        await Cd("sub");

        var result = await Execute(BashTool.ToolName, new { command = "ls" });

        Assert.False(result.IsError, TextOf(result));
        Assert.Contains("marker.txt", TextOf(result));
    }

    [Theory]
    [InlineData("pwsh")]
    [InlineData("powershell")]
    [InlineData("bash_persistent")]
    [InlineData("pwsh_persistent")]
    public async Task NewShell_StartsInChangedDirectory(string tool)
    {
        RequireShell(tool);
        Directory.CreateDirectory(Path.Combine(_root, "sub"));
        await File.WriteAllTextAsync(Path.Combine(_root, "sub", "marker.txt"), "", TestContext.Current.CancellationToken);
        await Cd("sub");

        var result = await Execute(tool, new { command = tool == "bash_persistent" ? "ls" : "Get-ChildItem -Name" });

        Assert.False(result.IsError, TextOf(result));
        Assert.Contains("marker.txt", TextOf(result));
        Assert.Equal(_root, _agent.Session.Header.Cwd);
    }

    [Theory]
    [InlineData("bash")]
    [InlineData("pwsh")]
    [InlineData("powershell")]
    public async Task RelativeShellWorkdir_ResolvesAgainstChangedDirectory(string tool)
    {
        RequireShell(tool);
        Directory.CreateDirectory(Path.Combine(_root, "sub", "deeper"));
        await File.WriteAllTextAsync(Path.Combine(_root, "sub", "deeper", "marker.txt"), "", TestContext.Current.CancellationToken);
        await Cd("sub");

        var result = await Execute(tool, new { command = tool == "bash" ? "ls" : "Get-ChildItem -Name", workdir = "deeper" });

        Assert.False(result.IsError, TextOf(result));
        Assert.Contains("marker.txt", TextOf(result));
    }

    [Theory]
    [InlineData("bash_persistent")]
    [InlineData("pwsh_persistent")]
    public async Task ExistingPersistentShell_KeepsItsDirectoryAfterCdOutsideRoot(string tool)
    {
        RequireShell(tool);
        _ = ApprovalService.Register(_ctx);
        using var answerer = ApprovalAnswerers.DenyAll(_ctx);
        var initial = await Execute(tool, new { command = "echo started" });
        Assert.False(initial.IsError, TextOf(initial));
        await Cd(_outside);

        var result = await Execute(tool, new { command = "echo kept > persistent.txt" });

        Assert.False(result.IsError, TextOf(result));
        Assert.True(File.Exists(Path.Combine(_root, "persistent.txt")));
        Assert.False(File.Exists(Path.Combine(_outside, "persistent.txt")));
        Assert.Empty(Asked());
    }

    [Theory]
    [InlineData("bash")]
    [InlineData("pwsh")]
    [InlineData("powershell")]
    [InlineData("wsl")]
    [InlineData("bash_persistent")]
    [InlineData("pwsh_persistent")]
    public async Task OutsideRootShell_RejectsWithoutExecution_ThenRunsAfterApproval(string tool)
    {
        RequireShell(tool);
        _ = ApprovalService.Register(_ctx);
        await Cd(_outside);
        using (ApprovalAnswerers.DenyAll(_ctx))
        {
            var denied = await Execute(tool, new { command = "echo allowed > shell.txt" });
            Assert.True(denied.IsError);
            Assert.False(File.Exists(Path.Combine(_outside, "shell.txt")));
            Assert.StartsWith(SessionWorkingDirectory.OutsideRootReason, Assert.Single(Asked()).Reason);
        }
        using (ApprovalAnswerers.AutoApprove(_ctx))
        {
            var approved = await Execute(tool, new { command = "echo allowed > shell.txt" });
            Assert.False(approved.IsError, TextOf(approved));
            if (tool == "wsl" && ValueOf(approved).GetProperty("exitCode").GetInt32() != 0)
                Assert.Skip($"No usable WSL distribution: {TextOf(approved)}");
            Assert.True(File.Exists(Path.Combine(_outside, "shell.txt")));
            Assert.Equal(2, Asked().Count);
            Assert.Equal(_root, _agent.Session.Header.Cwd);
        }
    }

    private static void RequireShell(string tool)
    {
        if (tool is "powershell" or "wsl")
        {
            if (!OperatingSystem.IsWindows())
                Assert.Skip("Windows-only shell; this host is not Windows");
            if (tool == "wsl" && !File.Exists(Path.Combine(Environment.SystemDirectory, "wsl.exe")))
                Assert.Skip("WSL is not installed on this Windows host");
            if (tool == "wsl" && !ShellProbe.Wsl)
                Assert.Skip("WSL/Linux is unavailable or unresponsive");
        }
        else if (tool.StartsWith("bash", StringComparison.Ordinal) ? !ShellProbe.Bash : !ShellProbe.Pwsh)
            Assert.Skip($"Required shell for {tool} is unavailable");
    }

    [Fact]
    public async Task OutsideRootWrite_AsksWithReason_AndRejectionWritesNothing()
    {
        _ = ApprovalService.Register(_ctx);
        using var answerer = ApprovalAnswerers.DenyAll(_ctx);
        await Cd(_outside);

        var result = await Execute(WriteTool.ToolName, new { file_path = "escaped.txt", content = "x" });

        Assert.True(result.IsError);
        Assert.False(File.Exists(Path.Combine(_outside, "escaped.txt")));
        var asked = Assert.Single(Asked());
        Assert.Equal(WriteTool.ToolName, asked.ToolName);
        Assert.StartsWith(SessionWorkingDirectory.OutsideRootReason, asked.Reason);
        Assert.Contains(Path.Combine(_outside, "escaped.txt"), asked.Reason);
        Assert.Contains(_root, asked.Reason);
    }

    [Fact]
    public async Task OutsideRootWrite_ProceedsAfterApproval()
    {
        _ = ApprovalService.Register(_ctx);
        using var answerer = ApprovalAnswerers.AutoApprove(_ctx);
        await Cd(_outside);

        var result = await Execute(WriteTool.ToolName, new { file_path = "approved.txt", content = "x" });

        Assert.False(result.IsError, TextOf(result));
        Assert.True(File.Exists(Path.Combine(_outside, "approved.txt")));
        Assert.Single(Asked());
    }

    [Fact]
    public async Task OutsideRootWrite_WithoutApprovalService_IsDenied()
    {
        await Cd(_outside);

        var result = await Execute(WriteTool.ToolName, new { file_path = "denied.txt", content = "x" });

        Assert.True(result.IsError);
        Assert.False(File.Exists(Path.Combine(_outside, "denied.txt")));
    }

    [Fact]
    public async Task InsideRootWrite_AfterCdOutside_DoesNotAsk()
    {
        _ = ApprovalService.Register(_ctx);
        using var answerer = ApprovalAnswerers.DenyAll(_ctx);
        await Cd(_outside);

        var result = await Execute(WriteTool.ToolName, new { file_path = Path.Combine(_root, "inside.txt"), content = "x" });

        Assert.False(result.IsError, TextOf(result));
        Assert.True(File.Exists(Path.Combine(_root, "inside.txt")));
        Assert.Empty(Asked());
    }

    [Fact]
    public async Task ShellStartedOutsideRoot_AsksBeforeRunning()
    {
        _ = ApprovalService.Register(_ctx);
        using var answerer = ApprovalAnswerers.DenyAll(_ctx);
        await Cd(_outside);

        var result = await Execute(BashTool.ToolName, new { command = "echo x > shell.txt" });

        Assert.True(result.IsError);
        Assert.False(File.Exists(Path.Combine(_outside, "shell.txt")));
        Assert.StartsWith(SessionWorkingDirectory.OutsideRootReason, Assert.Single(Asked()).Reason);
    }

    [Fact]
    public async Task RuntimeContext_ReportsTheChangedDirectory()
    {
        Directory.CreateDirectory(Path.Combine(_root, "sub"));
        var systemPrompt = _ctx.Get<SystemPrompt>(SystemPrompt.ServiceName)!;

        var before = await systemPrompt.Assemble(new AssembleContext(Agent: _agent));
        var sub = await Cd("sub");
        var after = await systemPrompt.Assemble(new AssembleContext(Agent: _agent));

        Assert.Equal("", before.Contexts.Single(context => context.Name == "working-directory").Text);
        var text = after.Contexts.Single(context => context.Name == "working-directory").Text;
        Assert.Contains(sub, text);
        Assert.Contains(_root, text);
    }

    [Fact]
    public async Task Truncation_RestoresTheEarlierDirectory()
    {
        Directory.CreateDirectory(Path.Combine(_root, "sub"));
        var before = _agent.Session.Seq;
        await Cd("sub");

        Assert.True(_agent.Session.TryTruncateTo(before));

        Assert.Equal(_root, _agent.Session.WorkingDirectory);
    }

    [Fact]
    public async Task CreateWorktree_BranchesFromHead_AndEntersCheckout()
    {
        _ = ApprovalService.Register(_ctx);
        using var answerer = ApprovalAnswerers.DenyAll(_ctx);
        var head = await InitRepository();

        var result = await Execute(WorktreeTool.ToolName, new { name = "feature-a" });

        Assert.False(result.IsError, TextOf(result));
        var value = ValueOf(result);
        var checkout = Path.Combine(_root, ".agents", "worktrees", "feature-a");
        Assert.Equal(checkout, value.GetProperty("path").GetString());
        Assert.Equal("feature-a", value.GetProperty("branch").GetString());
        Assert.Equal(head, value.GetProperty("baseCommit").GetString());
        Assert.Equal(_root, value.GetProperty("repositoryRoot").GetString());
        Assert.Equal(checkout, _agent.Session.WorkingDirectory);
        Assert.Equal("feature-a", await Git(checkout, "rev-parse", "--abbrev-ref", "HEAD"));
        Assert.Equal("*\n", await File.ReadAllTextAsync(Path.Combine(_root, ".agents", "worktrees", ".gitignore"), TestContext.Current.CancellationToken));
        var write = await Execute(WriteTool.ToolName, new { file_path = "inside-worktree.txt", content = "x" });
        Assert.False(write.IsError, TextOf(write));
        Assert.True(File.Exists(Path.Combine(checkout, "inside-worktree.txt")));
        Assert.Empty(Asked());
    }

    [Fact]
    public async Task CreateWorktree_FromRevision_PinsThatCommit()
    {
        var first = await InitRepository();
        await File.WriteAllTextAsync(Path.Combine(_root, "second.txt"), "2", TestContext.Current.CancellationToken);
        await Git(_root, "add", ".");
        await Git(_root, "commit", "-q", "-m", "second");

        var result = await Execute(WorktreeTool.ToolName, new { name = "from-first", from = first });

        Assert.False(result.IsError, TextOf(result));
        Assert.Equal(first, ValueOf(result).GetProperty("baseCommit").GetString());
        Assert.False(File.Exists(Path.Combine(_root, ".agents", "worktrees", "from-first", "second.txt")));
    }

    [Fact]
    public async Task CreateWorktree_ExistingBranch_Fails_AndKeepsDirectory()
    {
        await InitRepository();
        await Git(_root, "branch", "taken");

        var result = await Execute(WorktreeTool.ToolName, new { name = "taken" });

        Assert.True(result.IsError);
        Assert.Equal(_root, _agent.Session.WorkingDirectory);
    }

    [Fact]
    public async Task CreateWorktree_WithoutName_GeneratesOne()
    {
        await InitRepository();

        var result = await Execute(WorktreeTool.ToolName, new { });

        Assert.False(result.IsError, TextOf(result));
        var branch = ValueOf(result).GetProperty("branch").GetString()!;
        Assert.NotEmpty(branch);
        Assert.Equal(Path.Combine(_root, ".agents", "worktrees", branch), _agent.Session.WorkingDirectory);
    }

    /** Initializes the root as a repository with one commit and returns that commit. */
    private async Task<string> InitRepository()
    {
        await Git(_root, "init", "-q");
        await Git(_root, "config", "user.name", "test");
        await Git(_root, "config", "user.email", "test@example.com");
        await Git(_root, "config", "commit.gpgsign", "false");
        await File.WriteAllTextAsync(Path.Combine(_root, "first.txt"), "1", TestContext.Current.CancellationToken);
        await Git(_root, "add", ".");
        await Git(_root, "commit", "-q", "-m", "first");
        return await Git(_root, "rev-parse", "HEAD");
    }

    private static async Task<string> Git(string directory, params string[] arguments)
    {
        var info = new ProcessStartInfo("git")
        {
            WorkingDirectory = directory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var argument in arguments)
            info.ArgumentList.Add(argument);
        using var process = TestProcessScope.Start(info)!;
        var output = await process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
        var error = await process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
        await process.WaitForExitAsync(TestContext.Current.CancellationToken);
        Assert.True(process.ExitCode == 0, $"git {string.Join(' ', arguments)} failed: {error}");
        return output.Trim();
    }

    private sealed class WorkingDirectoryAgent : IAgent
    {
        public WorkingDirectoryAgent(Context ctx, string directory)
        {
            Ctx = ctx;
            var id = SessionId.Create($"session-{Guid.NewGuid():N}");
            Session = Session.Create(id, null, new SessionHeader
            {
                Version = SessionHeader.SessionFormatVersion,
                Id = id,
                CreatedAt = 0,
                Cwd = directory,
                IsSeeded = false,
            });
            Session.Append(new TurnStartPayload(1));
        }

        public SessionId Id => Session.Id;
        public Session Session { get; }
        public ScopeKey ScopeKey { get; } = new();
        public Context Ctx { get; }
        public AgentStatus Status => AgentStatus.Idle;
        public AgentOptions Options { get; } = new();

        public void Cancel(AgentCancelCause cause, bool keepInbox = false) { }
        public Task WhenIdle() => Task.CompletedTask;
        public void Send(UserMessage message, string target, bool wakeup) { }
        public void Followup(UserMessage message) { }
        public void Steer(UserMessage message) { }
        public void Inject(UserMessage message) { }
    }
}
