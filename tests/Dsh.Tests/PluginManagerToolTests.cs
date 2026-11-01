using System.Text.Json;
using Dsh.Core;
using Dsh.Interaction;
using Dsh.Llm;
using Dsh.Runtime;
using Dsh.Runtime.Events;
using static Dsh.Inspection.Plugin;

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
        Assert.Empty(harness.Manager.Calls);
    }

    [Fact]
    public async Task Write_WithoutApprovalChannel_IsDenied()
    {
        using var harness = new Harness();
        await harness.ComposeAsync();
        var agent = InspectionTestAgent.Open(harness.Ctx);

        var result = await harness.Execute("""{"action":"disable","target":"sample-plugin"}""", agent, TestContext.Current.CancellationToken);

        Assert.IsType<ToolExecutionResult.Failure>(result);
        Assert.Empty(harness.Manager.Calls);
    }

    [Fact]
    public async Task List_WithApprovalChannel_ReturnsPlugins()
    {
        using var harness = new Harness();
        await harness.ComposeAsync();
        using var answerer = ApprovalAnswerers.AutoApprove(harness.Ctx);
        var agent = InspectionTestAgent.Open(harness.Ctx);

        var success = Assert.IsType<ToolExecutionResult.Success>(await harness.Execute("""{"action":"list"}""", agent, TestContext.Current.CancellationToken));

        var result = success.Value.GetProperty("result").GetString();
        Assert.Contains("sample-plugin", result);
        Assert.Contains("describe:sample-plugin", harness.Manager.Calls);
    }

    [Fact]
    public async Task Write_WithApprovalChannel_Executes()
    {
        using var harness = new Harness();
        await harness.ComposeAsync();
        using var answerer = ApprovalAnswerers.AutoApprove(harness.Ctx);
        var agent = InspectionTestAgent.Open(harness.Ctx);

        Assert.IsType<ToolExecutionResult.Success>(
            await harness.Execute("""{"action":"disable","target":"sample-plugin"}""", agent, TestContext.Current.CancellationToken));

        Assert.Contains("disable:sample-plugin", harness.Manager.Calls);
        Assert.Equal(ApprovalOutcome.AllowedOnce,
            Assert.Single(agent.Session.SnapshotEvents().Select(item => item.Data).OfType<ApprovalDecidedPayload>()).Outcome);
    }

    [Theory]
    [InlineData("list")]
    [InlineData("describe")]
    [InlineData("enable")]
    [InlineData("disable")]
    [InlineData("add")]
    [InlineData("remove")]
    public async Task RejectedApproval_DoesNotInvokePluginManager(string action)
    {
        using var harness = new Harness();
        await harness.ComposeAsync();
        using var answerer = ApprovalAnswerers.DenyAll(harness.Ctx);
        var agent = InspectionTestAgent.Open(harness.Ctx);

        var failure = Assert.IsType<ToolExecutionResult.Failure>(await harness.Execute(
            JsonSerializer.Serialize(new { action, target = "sample-plugin", force = true }), agent, TestContext.Current.CancellationToken));

        Assert.Contains("rejected", failure.Error.Message);
        Assert.Empty(harness.Manager.Calls);
        Assert.Equal("plugin_manager",
            Assert.Single(agent.Session.SnapshotEvents().Select(item => item.Data).OfType<ApprovalAskedPayload>()).ToolName);
        Assert.Equal(ApprovalOutcome.Rejected,
            Assert.Single(agent.Session.SnapshotEvents().Select(item => item.Data).OfType<ApprovalDecidedPayload>()).Outcome);
    }

    [Fact(Timeout = 5000)]
    public async Task CancellationWhileAwaitingApproval_DoesNotInvokePluginManager()
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
                """{"action":"disable","target":"sample-plugin"}""", agent, cancellation.Token);
            await asked.Task.WaitAsync(TestContext.Current.CancellationToken);

            cancellation.Cancel();
            var failure = Assert.IsType<ToolExecutionResult.Failure>(
                await pending.WaitAsync(TestContext.Current.CancellationToken));

            Assert.Equal(ToolErrorCodes.AbortedBeforeDispatch, failure.Error.Info?.Code);
            Assert.Empty(harness.Manager.Calls);
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
            await harness.Execute("""{"action":"disable","target":"sample-plugin"}""", agent, TestContext.Current.CancellationToken));
        Assert.Contains("rejected", rejected.Error.Message);
        Assert.Empty(harness.Manager.Calls);

        await manager.DeactivateAsync();

        var missing = Assert.IsType<ToolExecutionResult.Failure>(
            await harness.Execute("""{"action":"list"}""", agent, TestContext.Current.CancellationToken));
        Assert.Equal(ToolErrorCodes.UnknownTool, missing.Error.Info?.Code);
        Assert.Single(agent.Session.SnapshotEvents().Select(item => item.Data).OfType<ApprovalAskedPayload>());
    }

    private sealed class Harness : IDisposable
    {
        public Harness()
        {
            _ = new SystemPrompt(Ctx, new SystemPromptConfig());
            Tools = new ToolRuntime(Ctx);
            ApprovalService.Register(Ctx);
            Ctx.Provide("pluginManager", Manager);
        }

        public Context Ctx { get; } = new();
        public ToolRuntime Tools { get; }
        public RecordingPluginManager Manager { get; } = new();

        public async Task<PluginActivation> ComposeAsync()
        {
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
        }
    }
}
