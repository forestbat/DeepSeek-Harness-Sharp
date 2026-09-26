using System.Text.Json;
using Dsh.Core;
using Dsh.Interaction;
using Dsh.Llm;
using Dsh.Presets;
using Dsh.Runtime;
using static Dsh.Inspection.Plugin;

namespace Dsh.Tests;

public sealed class PluginManagerToolTests
{
    [Fact]
    public async Task List_WithoutApprovalChannel_IsDenied()
    {
        using var harness = new Harness();
        await harness.ComposeAsync();
        var agent = harness.OpenCreative();

        var result = await harness.Execute("""{"action":"list"}""", agent);

        var failure = Assert.IsType<ToolExecutionResult.Failure>(result);
        Assert.Contains("no approval channel is available", string.Concat(failure.Content.OfType<TextBlock>().Select(block => block.Text)));
        Assert.Empty(harness.Manager.Calls);
    }

    [Fact]
    public async Task Write_WithoutApprovalChannel_IsDenied()
    {
        using var harness = new Harness();
        await harness.ComposeAsync();
        var agent = harness.OpenCreative();

        var result = await harness.Execute("""{"action":"disable","target":"sample-plugin"}""", agent);

        Assert.IsType<ToolExecutionResult.Failure>(result);
        Assert.Empty(harness.Manager.Calls);
    }

    [Fact]
    public async Task List_WithApprovalChannel_ReturnsPlugins()
    {
        using var harness = new Harness();
        await harness.ComposeAsync();
        using var answerer = ApprovalAnswerers.AutoApprove(harness.Ctx);
        var agent = harness.OpenCreative();

        var success = Assert.IsType<ToolExecutionResult.Success>(await harness.Execute("""{"action":"list"}""", agent));

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
        var agent = harness.OpenCreative();

        var success = Assert.IsType<ToolExecutionResult.Success>(
            await harness.Execute("""{"action":"disable","target":"sample-plugin"}""", agent));

        Assert.Equal("disable", success.Value.GetProperty("action").GetString());
        Assert.Contains("disable:sample-plugin", harness.Manager.Calls);
    }

    private sealed class Harness : IDisposable
    {
        public Harness()
        {
            _ = new SystemPrompt(Ctx, new SystemPromptConfig());
            Tools = new ToolRuntime(Ctx);
            ApprovalService.Register(Ctx);
            Ctx.Provide("pluginManager", Manager);
            Presets = PresetController.Register(Ctx);
        }

        public Context Ctx { get; } = new();
        public ToolRuntime Tools { get; }
        public RecordingPluginManager Manager { get; } = new();
        public PresetController Presets { get; }

        public InspectionTestAgent OpenCreative()
        {
            var agent = InspectionTestAgent.Open(Ctx);
            Presets.Set(agent, "creative");
            return agent;
        }

        public async Task ComposeAsync()
        {
            Ctx.Plugin(InspectionTestPlugin.Definition(PluginManager));
            await Ctx.Scheduler.SettleAsync();
        }

        public Task<ToolExecutionResult> Execute(string arguments, IAgent agent)
            => Tools.Execute(new ToolExecutionInput
            {
                CallId = ToolCallId.Create($"call-{Guid.NewGuid():N}"),
                Name = "plugin_manager",
                Arguments = JsonDocument.Parse(arguments).RootElement,
                Agent = agent,
                Signal = default,
            });

        public void Dispose()
        {
        }
    }
}
