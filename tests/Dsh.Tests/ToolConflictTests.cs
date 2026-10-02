using System.Text.Json.Nodes;
using Dsh.Core;
using Dsh.Llm;
using Dsh.Runtime;
using Dsh.Runtime.Composition;

namespace Dsh.Tests;

/** 工具重名消解(§3.5): 冲突时后注册者改名为「包名末段-工具名」; 同插件重复注册仍是错误。 */
public sealed class ToolConflictTests
{
    private static ToolDefinition EchoTool(string name, string marker)
        => new()
        {
            Name = name,
            Description = marker,
            Parameters = new JsonObject { ["type"] = "object" },
            Output = new ToolOutputDefinition(new JsonObject(), (_, _) => [new TextBlock(marker)]),
            Execute = (_, _) => Task.FromResult<object?>(new JsonObject { ["marker"] = marker }),
        };

    private static (Context Ctx, ToolRuntime Tools) CreateRuntime()
    {
        var ctx = new Context();
        _ = new SystemPrompt(ctx, new SystemPromptConfig());
        return (ctx, new ToolRuntime(ctx));
    }

    [Fact]
    public async Task ConflictingToolFromAnotherPlugin_IsRenamedWithPackageSuffix()
    {
        var (ctx, tools) = CreateRuntime();
        var composition = await Composition.StartAsync(ctx,
        [
            new PluginEntry(PluginDefinition.From((pluginCtx, _) =>
            {
                pluginCtx.Get<ToolRuntime>(ToolRuntime.ServiceName)!.Register(EchoTool("save", "first"));
                return null;
            }, "@a/memory"), null),
            new PluginEntry(PluginDefinition.From((pluginCtx, _) =>
            {
                pluginCtx.Get<ToolRuntime>(ToolRuntime.ServiceName)!.Register(EchoTool("save", "second"));
                return null;
            }, "@b/dsh-memory"), null),
        ]);

        Assert.All(composition.Activations, activation => Assert.Equal(ActivationState.Active, activation.State));
        Assert.Equal("first", tools.Get("save")!.Description);
        Assert.Equal("second", tools.Get("dsh-memory-save")!.Description);
        Assert.Contains(ctx.Logger.Buffer, message =>
            message.Type == LoggerType.Warn
            && message.Text.Contains("\"save\"")
            && message.Text.Contains("dsh-memory-save"));
    }

    [Fact]
    public async Task SamePluginRegisteringSameNameTwice_StillFails()
    {
        var (ctx, _) = CreateRuntime();
        var composition = await Composition.StartAsync(ctx,
        [
            new PluginEntry(PluginDefinition.From((pluginCtx, _) =>
            {
                var tools = pluginCtx.Get<ToolRuntime>(ToolRuntime.ServiceName)!;
                tools.Register(EchoTool("save", "first"));
                tools.Register(EchoTool("save", "second"));
                return null;
            }, "@a/memory"), null),
        ]);

        Assert.Equal(ActivationState.Failed, composition.Find("@a/memory")!.State);
    }

    [Fact]
    public void ConflictOutsidePluginApply_KeepsThrowing()
    {
        var (_, tools) = CreateRuntime();
        tools.Register(EchoTool("save", "first"));

        Assert.Throws<InvalidOperationException>(() => tools.Register(EchoTool("save", "second")));
    }

    [Fact]
    public void NonIdentifierToolName_WarnsAboutPtcCallForm()
    {
        var (ctx, tools) = CreateRuntime();

        tools.Register(EchoTool("my-tool", "marker"));

        Assert.NotNull(tools.Get("my-tool"));
        Assert.Contains(ctx.Logger.Buffer, message =>
            message.Type == LoggerType.Warn
            && message.Text.Contains("my-tool")
            && message.Text.Contains("tools.call"));
    }
}
