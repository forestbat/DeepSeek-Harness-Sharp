using System.Text.Json;
using Dsh.Core;
using Dsh.Interaction;
using Dsh.Llm;
using Dsh.Plugins;
using Dsh.Runtime;
using Dsh.Runtime.Composition;
using Dsh.Runtime.Events;
using Dsh.Boot;
using static Dsh.Inspection.Plugin;
using Dsh.Agent;

namespace Dsh.Tests;

public sealed class PluginManagerToolTests
{
    [Fact]
    public async Task List_WithoutApprovalChannel_IsDenied()
    {
        using var harness = new Harness();
        await harness.ComposeAsync();
        var agent = InspectionTestAgent.Open(harness.Ctx);

        var result = await harness.Execute("""{"action":"list"}""", agent, TestContext.Current.CancellationToken);

        var failure = Assert.IsType<ToolExecutionResult.Failure>(result);
        Assert.Contains("no approval channel is available", string.Concat(failure.Content.OfType<TextBlock>().Select(block => block.Text)));
    }

    [Theory]
    [InlineData("list")]
    [InlineData("describe")]
    public async Task RejectedApproval_RecordsRejectedDecision(string action)
    {
        using var harness = new Harness();
        await harness.ComposeAsync();
        using var answerer = ApprovalAnswerers.DenyAll(harness.Ctx);
        var agent = InspectionTestAgent.Open(harness.Ctx);

        var failure = Assert.IsType<ToolExecutionResult.Failure>(await harness.Execute(
            JsonSerializer.Serialize(new { action, target = "sample-plugin", force = true }), agent, TestContext.Current.CancellationToken));

        Assert.Contains("rejected", failure.Error.Message);
        Assert.Equal("plugin_manager",
            Assert.Single(agent.Session.SnapshotEvents().Select(item => item.Data).OfType<ApprovalAskedPayload>()).ToolName);
        Assert.Equal(ApprovalOutcome.Rejected,
            Assert.Single(agent.Session.SnapshotEvents().Select(item => item.Data).OfType<ApprovalDecidedPayload>()).Outcome);
    }

    [Fact(Timeout = 5000)]
    public async Task CancellationWhileAwaitingApproval_RecordsCancelledDecision()
    {
        using var harness = new Harness();
        await harness.ComposeAsync();
        var agent = InspectionTestAgent.Open(harness.Ctx);
        using var cancellation = new CancellationTokenSource();
        var asked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var answer = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var removeAnswerer = harness.Ctx.OnWaterfall<ApprovalRequestNotification>(
            (_, _) =>
            {
                asked.TrySetResult();
                return new ValueTask<object?>(answer.Task);
            },
            new EventOptions { Global = true });
        try
        {
            var pending = harness.Execute(
                """{"action":"list"}""", agent, cancellation.Token);
            await asked.Task.WaitAsync(TestContext.Current.CancellationToken);

            cancellation.Cancel();
            var failure = Assert.IsType<ToolExecutionResult.Failure>(
                await pending.WaitAsync(TestContext.Current.CancellationToken));

            Assert.Equal(ToolErrorCodes.AbortedBeforeDispatch, failure.Error.Info?.Code);
            Assert.Equal(ApprovalOutcome.Cancelled,
                Assert.Single(agent.Session.SnapshotEvents().Select(item => item.Data).OfType<ApprovalDecidedPayload>()).Outcome);
        }
        finally
        {
            answer.TrySetResult(ApprovalOutcome.Cancelled);
            removeAnswerer();
        }
    }

    [Fact]
    public async Task PluginManagerPlugin_DoesNotEnableInspection()
    {
        using var harness = new Harness();
        await harness.ComposeAsync();
        using var answerer = ApprovalAnswerers.AutoApprove(harness.Ctx);
        var agent = InspectionTestAgent.Open(harness.Ctx);
        Assert.IsType<ToolExecutionResult.Success>(await harness.Execute("""{"action":"list"}""", agent, TestContext.Current.CancellationToken));

        foreach (var name in new[] { "creative_inspect_list", "creative_inspect_query" })
        {
            var failure = Assert.IsType<ToolExecutionResult.Failure>(
                await harness.ExecuteTool(name, "{}", agent, TestContext.Current.CancellationToken));
            Assert.Equal(ToolErrorCodes.UnknownTool, failure.Error.Info?.Code);
        }
    }

    [Fact]
    public async Task DeactivatingInspection_PreservesPluginManagerApproval_AndManagerUnloadRemovesIt()
    {
        using var harness = new Harness();
        var manager = await harness.ComposeAsync();
        var inspection = harness.Ctx.Plugin(InspectionTestPlugin.Definition(InspectTools));
        await inspection.WaitAsync();
        var agent = InspectionTestAgent.Open(harness.Ctx);
        using var answerer = ApprovalAnswerers.DenyAll(harness.Ctx);
        Assert.IsType<ToolExecutionResult.Success>(
            await harness.ExecuteTool("creative_inspect_list", "{}", agent, TestContext.Current.CancellationToken));

        await inspection.DeactivateAsync();

        var rejected = Assert.IsType<ToolExecutionResult.Failure>(
            await harness.Execute("""{"action":"list"}""", agent, TestContext.Current.CancellationToken));
        Assert.Contains("rejected", rejected.Error.Message);

        await manager.DeactivateAsync();

        var missing = Assert.IsType<ToolExecutionResult.Failure>(
            await harness.Execute("""{"action":"list"}""", agent, TestContext.Current.CancellationToken));
        Assert.Equal(ToolErrorCodes.UnknownTool, missing.Error.Info?.Code);
        Assert.Single(agent.Session.SnapshotEvents().Select(item => item.Data).OfType<ApprovalAskedPayload>());
    }

    [Theory]
    [InlineData("enable", false)]
    [InlineData("enable", true)]
    [InlineData("disable", false)]
    [InlineData("disable", true)]
    [InlineData("remove", false)]
    [InlineData("remove", true)]
    public async Task RealManagerMutation_UsesOneSettingsDiffDecision_AndOnlyApprovedChangesAffectActivation(string action, bool allowed)
    {
        var root = TempTree.CreateDirectory("plugin-settings-boundary");
        try
        {
            var home = HarnessHome.Resolve(Path.Combine(root, "home"));
            Directory.CreateDirectory(home.Root);
            var initiallyEnabled = action != "enable";
            var original = $"plugins:\n  \"@deepseek-ai/dsh-tool-todo\": {initiallyEnabled.ToString().ToLowerInvariant()}\n  \"@deepseek-ai/dsh-plugin-manager\": true\nsafety:\n  autoApprove: false\n";
            await File.WriteAllTextAsync(home.SettingsFile, original, TestContext.Current.CancellationToken);
            using var app = await ConfigBoot.Compose(new HarnessOptions(home, Cwd: root, PluginsDirectory: Path.Combine(root, "plugins")), global::Dsh.Plugins.Generated.DshPluginCatalog.CreatePluginRuntime);
            using var answerer = ApprovalAssert.AnswerSettingsDiff(app.Ctx, allowed);
            var id = SessionId.Create(Guid.NewGuid().ToString("N"));
            var session = Session.Create(id, null, new SessionHeader { Version = SessionHeader.SessionFormatVersion, Id = id, Cwd = root, CreatedAt = 0, IsSeeded = false });
            using var agent = new AgentLoopAgent(app.Ctx, id, new AgentOptions(), session, _ => 0);
            session.Append(new TurnStartPayload(1));
            var tools = app.Ctx.Get<ToolRuntime>(ToolRuntime.ServiceName)!;
            var result = await Task.Run(() => tools.Execute(new ToolExecutionInput
            {
                CallId = ToolCallId.Create(Guid.NewGuid().ToString("N")), Name = "plugin_manager",
                Arguments = JsonSerializer.SerializeToElement(new { action, target = "@deepseek-ai/dsh-tool-todo" }),
                Agent = agent, Signal = TestContext.Current.CancellationToken,
            }));

            Assert.Equal(!allowed, result.IsError);
            var asked = Assert.Single(session.SnapshotEvents().Select(item => item.Data).OfType<ApprovalAskedPayload>(),
                approval => ApprovalAssert.IsSettingsDiff(approval.Arguments));
            Assert.NotEmpty(ApprovalHints.SettingsDiff(asked.Arguments));
            Assert.Equal(allowed ? "committed" : "rejected", Assert.Single(session.SnapshotEvents().Select(item => item.Data).OfType<SettingsMutationAuditPayload>()).Outcome);
            Assert.Equal(allowed ? !initiallyEnabled : initiallyEnabled, tools.Get("todo_write") is not null);
            if (!allowed)
                Assert.Equal(original, await File.ReadAllTextAsync(home.SettingsFile, TestContext.Current.CancellationToken));
        }
        finally
        {
            TempTree.Delete(root);
        }
    }

    [Fact]
    public async Task RejectedInstall_LeavesDeploymentAndRealActivationUntouched()
    {
        var root = TempTree.CreateDirectory("plugin-install-boundary");
        try
        {
            var home = HarnessHome.Resolve(Path.Combine(root, "home"));
            var plugins = Path.Combine(root, "plugins");
            using var app = await ConfigBoot.Compose(new HarnessOptions(home, Cwd: root, PluginsDirectory: plugins), global::Dsh.Plugins.Generated.DshPluginCatalog.CreatePluginRuntime);
            var manager = app.Ctx.Get<HarnessPluginManager>("pluginManager")!;
            await manager.RemoveAsync("test/local");
            var before = await File.ReadAllTextAsync(home.SettingsFile, TestContext.Current.CancellationToken);
            using var answerer = ApprovalAssert.AnswerSettingsDiff(app.Ctx, allowed: false);
            var id = SessionId.Create(Guid.NewGuid().ToString("N"));
            var session = Session.Create(id, null, new SessionHeader
            {
                Version = SessionHeader.SessionFormatVersion, Id = id, Cwd = root, CreatedAt = 0, IsSeeded = false,
            });
            using var agent = new AgentLoopAgent(app.Ctx, id, new AgentOptions(), session, _ => 0);
            session.Append(new TurnStartPayload(1));
            var tools = app.Ctx.Get<ToolRuntime>(ToolRuntime.ServiceName)!;
            var source = Path.Combine(AppContext.BaseDirectory, "Dsh.ManagedPluginSample.dll");
            Assert.True(File.Exists(source));

            var result = await Task.Run(() => tools.Execute(new ToolExecutionInput
            {
                CallId = ToolCallId.Create(Guid.NewGuid().ToString("N")),
                Name = "plugin_manager",
                Arguments = JsonSerializer.SerializeToElement(new { action = "add", target = source }),
                Agent = agent,
                Signal = TestContext.Current.CancellationToken,
            }));

            Assert.True(result.IsError);
            Assert.Equal(before, await File.ReadAllTextAsync(home.SettingsFile, TestContext.Current.CancellationToken));
            Assert.False(Directory.Exists(Path.Combine(plugins, "Dsh.ManagedPluginSample")));
            Assert.Null(app.Composition!.Find("test/local"));
            Assert.Equal("rejected", Assert.Single(session.SnapshotEvents().Select(item => item.Data)
                .OfType<SettingsMutationAuditPayload>()).Outcome);
            Assert.Single(session.SnapshotEvents().Select(item => item.Data).OfType<ApprovalAskedPayload>(),
                approval => ApprovalAssert.IsSettingsDiff(approval.Arguments));
        }
        finally
        {
            TempTree.Delete(root);
        }
    }

    private sealed class Harness : IDisposable
    {
        private readonly string _root = TempTree.CreateDirectory("plugin-manager-approval");
        private Composition? _composition;
        private HarnessPluginManager? _manager;

        public Harness()
        {
            _ = new SystemPrompt(Ctx, new SystemPromptConfig());
            Tools = new ToolRuntime(Ctx);
            ApprovalService.Register(Ctx);
        }

        public Context Ctx { get; } = new();
        public ToolRuntime Tools { get; }

        public async Task<PluginActivation> ComposeAsync()
        {
            _composition = await Composition.StartAsync(Ctx, []);
            _manager = new HarnessPluginManager(new PluginHost(), _composition,
                new HarnessHome(_root), new HarnessSettings(), [], [], [], null,
                Path.Combine(_root, "plugins"), null);
            Ctx.Provide("pluginManager", _manager);
            var activation = Ctx.Plugin(InspectionTestPlugin.Definition(PluginManager));
            await Ctx.Scheduler.SettleAsync();
            return activation;
        }

        public Task<ToolExecutionResult> Execute(string arguments, IAgent agent, CancellationToken signal = default)
            => ExecuteTool("plugin_manager", arguments, agent, signal);

        public Task<ToolExecutionResult> ExecuteTool(
            string name, string arguments, IAgent agent, CancellationToken signal = default)
            => Tools.Execute(new ToolExecutionInput
            {
                CallId = ToolCallId.Create($"call-{Guid.NewGuid():N}"),
                Name = name,
                Arguments = JsonDocument.Parse(arguments).RootElement,
                Agent = agent,
                Signal = signal,
            });

        public void Dispose()
        {
            _composition?.DeactivateAll();
            _manager?.Dispose();
            TempTree.Delete(_root);
        }
    }
}
