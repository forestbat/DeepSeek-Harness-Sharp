using System.Text.Json;
using System.Text.Json.Nodes;
using Dsh.Agent;
using Dsh.Boot;
using Dsh.Core;
using Dsh.Interaction;
using Dsh.Llm;
using Dsh.Runtime;
using Dsh.Subprocess;
using Dsh.Tools;

namespace Dsh.Tests;

/** safety 闸门: autoApprove 覆盖权限根之外、never 只拦需要审批的调用、进程启动在宿主效果点授权。 */
public sealed class SafetyGateTests : IDisposable
{
    private readonly Context _context = new();
    private readonly HarnessHome _home;
    private readonly string _root;
    private readonly string _outside;
    private readonly Session _session;
    private readonly AgentLoopAgent _agent;
    private readonly ToolRuntime _tools;
    private readonly SubprocessService _subprocess;
    private readonly List<IDisposable> _registrations;

    public SafetyGateTests()
    {
        _root = TempTree.CreateDirectory("safety-gate");
        _outside = TempTree.CreateDirectory("safety-gate-outside");
        _home = HarnessHome.Resolve(Path.Combine(_root, "home"));
        Directory.CreateDirectory(_home.Root);
        Directory.CreateDirectory(Path.Combine(_root, "workspace"));
        File.WriteAllText(_home.SettingsFile, "safety:\n  autoApprove: false\n");
        _ = new SystemPrompt(_context, new SystemPromptConfig());
        _tools = new ToolRuntime(_context);
        _ = new LlmRuntime(_context);
        _subprocess = new SubprocessService(_context);
        _ = ApprovalService.Register(_context);
        _context.SetOwn("harnessOptions", new HarnessOptions(_home));
        _ = new SafetyPolicySource(_context, HarnessSettings.CreatePolicyReader(_home, HarnessSettings.Load(_home)));
        var id = SessionId.Create(Guid.NewGuid().ToString("N"));
        _session = Session.Create(id, null, new SessionHeader
        {
            Version = SessionHeader.SessionFormatVersion,
            Id = id,
            Cwd = Path.Combine(_root, "workspace"),
            CreatedAt = 0,
            IsSeeded = false,
        });
        _agent = new AgentLoopAgent(_context, id, new AgentOptions(), _session, _ => 0);
        _session.Append(new TurnStartPayload(1));
        _registrations = [WriteTool.Register(_context)];
    }

    public void Dispose()
    {
        foreach (var registration in _registrations)
            registration.Dispose();
        _subprocess.Dispose();
        _agent.Dispose();
        TempTree.Delete(_root);
        TempTree.Delete(_outside);
    }

    [Fact]
    public async Task AutoApprove_DoesNotAskForWritesOutsideThePermissionRoot()
    {
        ConfigureSafety("autoApprove: true");
        var target = Path.Combine(_outside, "outside.txt");

        var result = await Execute(WriteTool.ToolName, new JsonObject { ["file_path"] = target, ["content"] = "x" });

        Assert.False(result.IsError, TextOf(result));
        Assert.True(File.Exists(target));
        Assert.Empty(Asked());
        // 命中默认放行的决策只发通知, 不写会话日志: 这类"我允许了"占日志九成以上的事件量却不带信息。
        Assert.Empty(SafetyAudits());
    }

    [Fact]
    public async Task AutoApprove_DenialsStillPersistAnAuditEvent()
    {
        ConfigureSafety("autoApprove: true");
        var target = Path.Combine(_outside, "outside-denied.txt");
        HarnessSettings.Update(_home, settings =>
        {
            settings.Safety!.Blacklist["deny-outside-write"] = new() { Path = target, Intent = "write" };
            return true;
        });

        var result = await Execute(WriteTool.ToolName, new JsonObject { ["file_path"] = target, ["content"] = "x" });

        Assert.True(result.IsError);
        var audit = Assert.Single(SafetyAudits());
        Assert.Equal("deny", audit.Decision);
        Assert.Equal("deny-outside-write", audit.RuleId);
        Assert.Equal(_session.Id.Value, audit.SessionId);
    }

    [Fact]
    public async Task NotifyPolicy_StillAsksOutsideThePermissionRoot()
    {
        using var deny = ApprovalAnswerers.DenyAll(_context);
        var target = Path.Combine(_outside, "outside.txt");

        var result = await Execute(WriteTool.ToolName, new JsonObject { ["file_path"] = target, ["content"] = "x" });

        Assert.True(result.IsError);
        Assert.False(File.Exists(target));
        Assert.Contains(Asked(), asked => asked.Reason?.Contains(SessionWorkingDirectory.OutsideRootReason) == true);
    }

    [Fact]
    public async Task NeverPolicy_DoesNotRejectCallsThatNeedNoApproval()
    {
        ConfigureSafety("autoApprove: true");
        ApprovalService.SetApprovalPolicy(_session, ApprovalPolicy.Never);
        var target = Path.Combine(_session.WorkingDirectory!, "inside.txt");

        var result = await Execute(WriteTool.ToolName, new JsonObject { ["file_path"] = target, ["content"] = "x" });

        Assert.False(result.IsError, TextOf(result));
        Assert.True(File.Exists(target));
    }

    [Fact]
    public async Task NeverPolicy_StillRejectsCallsThatNeedApproval()
    {
        ApprovalService.SetApprovalPolicy(_session, ApprovalPolicy.Never);
        var target = Path.Combine(_session.WorkingDirectory!, "inside.txt");

        var result = await Execute(WriteTool.ToolName, new JsonObject { ["file_path"] = target, ["content"] = "x" });

        Assert.True(result.IsError);
        Assert.False(File.Exists(target));
        Assert.Contains("denies approval-requiring operations", TextOf(result));
    }

    [Fact]
    public async Task CallerRun_SpawnAsks_AndDenialStartsNoProcess()
    {
        using var deny = ApprovalAnswerers.DenyAll(_context);
        var run = CreateRun("bash", new { command = "exit 0" });

        var error = await Assert.ThrowsAnyAsync<UnauthorizedAccessException>(
            () => _subprocess.SpawnAsync(ExitSpec(), run));

        Assert.Contains("was not approved", error.Message);
        var asked = Assert.Single(Asked());
        Assert.Contains("approve starting", asked.Reason);
        Assert.Contains(SafetyAudits(), audit => audit.Tool == "bash" && audit.Intent == "spawn");
    }

    [Fact]
    public async Task CallerRun_SpawnProceedsWhenApproved()
    {
        using var allow = ApprovalAnswerers.AutoApprove(_context);
        var run = CreateRun("bash", new { command = "exit 0" });

        using var handle = await _subprocess.SpawnAsync(ExitSpec(), run);
        var outcome = await handle.Done;

        Assert.Equal(0, outcome.ExitCode);
        Assert.Single(Asked());
    }

    [Fact]
    public async Task InternalSpawn_WithoutCallerRun_IsNotAsked()
    {
        using var deny = ApprovalAnswerers.DenyAll(_context);

        using var handle = await _subprocess.SpawnAsync(ExitSpec());
        var outcome = await handle.Done;

        Assert.Equal(0, outcome.ExitCode);
        Assert.Empty(Asked());
    }

    private static SubprocessSpawnSpec ExitSpec()
        => OperatingSystem.IsWindows()
            ? new SubprocessSpawnSpec { Argv = ["cmd.exe", "/c", "exit 0"], Cwd = Environment.CurrentDirectory, Signal = TestContext.Current.CancellationToken }
            : new SubprocessSpawnSpec { Argv = ["/bin/sh", "-c", "exit 0"], Cwd = Environment.CurrentDirectory, Signal = TestContext.Current.CancellationToken };

    private ToolRunContext CreateRun(string tool, object arguments)
    {
        var callId = ToolCallId.Create($"call-{Guid.NewGuid():N}");
        return new ToolRunContext
        {
            CallId = callId,
            RootCallIdValue = callId,
            Name = tool,
            Arguments = JsonSerializer.SerializeToElement(arguments),
            Agent = _agent,
            Signal = TestContext.Current.CancellationToken,
        };
    }

    private void ConfigureSafety(string safetyBody)
    {
        var indented = string.Join('\n', safetyBody.Split('\n').Select(line => line.Length == 0 ? line : $"  {line}"));
        File.WriteAllText(_home.SettingsFile, $"safety:\n{indented}\n");
    }

    private IEnumerable<ApprovalAskedPayload> Asked()
        => _session.SnapshotEvents().Select(item => item.Data).OfType<ApprovalAskedPayload>();

    private IEnumerable<SafetyDecisionAuditPayload> SafetyAudits()
        => _session.SnapshotEvents().Select(item => item.Data).OfType<SafetyDecisionAuditPayload>();

    private Task<ToolExecutionResult> Execute(string name, JsonObject arguments)
        => _tools.Execute(new ToolExecutionInput
        {
            CallId = ToolCallId.Create($"call-{Guid.NewGuid():N}"),
            Name = name,
            Arguments = JsonSerializer.SerializeToElement(arguments),
            Agent = _agent,
            Signal = TestContext.Current.CancellationToken,
        });

    private static string TextOf(ToolExecutionResult result)
        => string.Concat(result.Content.OfType<TextBlock>().Select(block => block.Text));
}
