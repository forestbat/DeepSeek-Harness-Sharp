using Dsh.AgentAdapters;
using Dsh.Acp;
using Dsh.AgentAdapters.Acp;

namespace Dsh.Tests;

/**
 * Smoke tests against real native ACP agents installed on this machine (skipped when missing).
 * Handshake tests make no model call; LlmSmoke tests send real prompts through OpenCode's configured model.
 * Starting OpenCode is CPU heavy for several seconds, so these run serially instead of skewing timing-sensitive tests in parallel.
 */
public sealed class AcpAgentProcessSmokeTests : IDisposable
{
    private static readonly TimeSpan HandshakeTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan TurnTimeout = TimeSpan.FromSeconds(5);

    /** Child-process-only OpenCode configuration: keep its configured model and ask before running tools. */
    private static readonly IReadOnlyDictionary<string, string> PermissionEnvironment = new Dictionary<string, string>
    {
        ["OPENCODE_CONFIG_CONTENT"] = """{"permission":{"bash":"ask","edit":"ask"}}""",
    };

    private readonly string _workspace = Path.GetFullPath(Path.Combine(
        AppContext.BaseDirectory, "../../../../../artifacts020/test-tmp", $"acp-{Guid.NewGuid():N}"));

    public AcpAgentProcessSmokeTests() => Directory.CreateDirectory(_workspace);

    public void Dispose()
    {
        if (Directory.Exists(_workspace))
            Directory.Delete(_workspace, recursive: true);
    }

    [Theory]
    [InlineData(Dsh.RemoteHost.AgentKinds.OpenCode)]
    [InlineData(Dsh.RemoteHost.AgentKinds.KiloCode)]
    public async Task BuiltInAcp_HandshakeAndSessionLifecycle(string agentKind)
    {
        var adapter = Assert.IsType<AgentAdapter>(AgentAdapterCatalog.Find(agentKind));
        if (AgentExecutableLocator.Resolve(adapter.Acp!.Executable) is null)
            Assert.Skip($"{adapter.DisplayName} is not installed on this machine");
        await using var agent = AcpAgentProcess.Start(adapter.Acp!, _workspace);
        var info = await agent.Client.InitializeAsync(new AcpImplementationInfo("dsh-tests", "0"), TestContext.Current.CancellationToken)
            .WaitAsync(HandshakeTimeout, TestContext.Current.CancellationToken);
        var session = await agent.Client.NewSessionAsync(_workspace, TestContext.Current.CancellationToken)
            .WaitAsync(HandshakeTimeout, TestContext.Current.CancellationToken);
        await agent.Client.CancelAsync(session.SessionId!, TestContext.Current.CancellationToken);

        Assert.Equal(AcpMethods.ProtocolVersion, info.ProtocolVersion);
        Assert.True(info.CanLoadSession);
        Assert.False(string.IsNullOrEmpty(session.SessionId));
        Assert.False(agent.HasExited);
    }

    [Fact]
    [Trait("Category", "LlmSmoke")]
    public async Task OpenCode_PromptStreamsAgentMessageChunks()
    {
        await using var agent = StartOpenCode();
        var chunks = new List<string>();
        agent.Client.SessionUpdate += update =>
        {
            if (update is { Kind: AcpMethods.UpdateAgentMessageChunk, Text: { } text })
                chunks.Add(text);
        };
        var sessionId = await OpenSessionAsync(agent);

        var stopReason = await agent.Client.PromptAsync(sessionId, "Reply with exactly the word pong and nothing else. Do not use any tools.", TestContext.Current.CancellationToken)
            .WaitAsync(TurnTimeout, TestContext.Current.CancellationToken);

        Assert.Equal(AcpMethods.StopEndTurn, stopReason);
        Assert.Contains("pong", string.Concat(chunks), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    [Trait("Category", "LlmSmoke")]
    public async Task OpenCode_PermissionRequestIsAnsweredByTheCaller()
    {
        await using var agent = StartOpenCode(PermissionEnvironment);
        var sessionId = await OpenSessionAsync(agent);
        var prompt = agent.Client.PromptAsync(sessionId, "Run exactly this shell command once: touch acp-probe.txt", TestContext.Current.CancellationToken);

        var permission = await agent.Client.PermissionRequests.ReadAsync(TestContext.Current.CancellationToken).AsTask()
            .WaitAsync(TurnTimeout, TestContext.Current.CancellationToken);
        var reject = permission.Options.First(option => option.Kind is AcpMethods.PermissionRejectOnce or AcpMethods.PermissionRejectAlways);
        Assert.True(permission.TrySelect(reject.OptionId));
        var stopReason = await prompt.WaitAsync(TurnTimeout, TestContext.Current.CancellationToken);

        Assert.Equal(sessionId, permission.SessionId);
        Assert.False(string.IsNullOrEmpty(permission.ToolCall.ToolCallId));
        Assert.Contains(stopReason, new[] { AcpMethods.StopEndTurn, AcpMethods.StopCancelled });
        Assert.False(File.Exists(Path.Combine(_workspace, "acp-probe.txt")));
    }

    [Fact]
    [Trait("Category", "LlmSmoke")]
    public async Task OpenCode_CancelDuringPermissionWaitEndsTurnWithoutRunningTheTool()
    {
        await using var agent = StartOpenCode(PermissionEnvironment);
        var sessionId = await OpenSessionAsync(agent);
        var prompt = agent.Client.PromptAsync(sessionId, "Run exactly this shell command once: touch acp-probe.txt", TestContext.Current.CancellationToken);
        var permission = await agent.Client.PermissionRequests.ReadAsync(TestContext.Current.CancellationToken).AsTask()
            .WaitAsync(TurnTimeout, TestContext.Current.CancellationToken);

        await agent.Client.CancelAsync(sessionId, TestContext.Current.CancellationToken);
        var stopReason = await prompt.WaitAsync(TurnTimeout, TestContext.Current.CancellationToken);

        Assert.Equal(AcpMethods.StopCancelled, stopReason);
        Assert.Equal(AcpMethods.OutcomeCancelled, (await permission.Outcome).Outcome);
        Assert.False(permission.TrySelect(permission.Options[0].OptionId));
        Assert.False(File.Exists(Path.Combine(_workspace, "acp-probe.txt")));
    }

    private AcpAgentProcess StartOpenCode(IReadOnlyDictionary<string, string>? environment = null)
    {
        if (AgentExecutableLocator.Resolve("opencode") is null)
            Assert.Skip("opencode is not installed on this machine");
        return AcpAgentProcess.Start(AgentAdapterCatalog.OpenCode.Acp!, _workspace, environment);
    }

    private async Task<string> OpenSessionAsync(AcpAgentProcess agent)
    {
        await agent.Client.InitializeAsync(cancellationToken: TestContext.Current.CancellationToken).WaitAsync(HandshakeTimeout, TestContext.Current.CancellationToken);
        var session = await agent.Client.NewSessionAsync(_workspace, TestContext.Current.CancellationToken).WaitAsync(HandshakeTimeout, TestContext.Current.CancellationToken);
        return session.SessionId!;
    }
}
