using System.Text.Json;
using System.Text.Json.Nodes;
using Dsh.Core;
using Dsh.Llm;
using Dsh.Plugins;
using Dsh.Runtime;

namespace Dsh.Tests;

/** TOON 插件(§4 G14): 注册真实派发工具 `toon`(TOON 参数); 输入侧白名单结果转 TOON。 */
public sealed class ToonPluginTests
{
    private const string Package = "@deepseek-ai/dsh-toon";

    private static (Context Ctx, ToolRuntime Tools) CreateHost(object? pluginConfig = null)
    {
        var ctx = new Context();
        _ = new SystemPrompt(ctx, new SystemPromptConfig());
        var tools = new ToolRuntime(ctx);
        _ = new LlmRuntime(ctx);
        var host = new PluginHost();
        host.RegisterCompiledIn();
        var definition = host.Catalog.CreateDefinition(Package);
        ctx.Plugin(definition, pluginConfig);
        return (ctx, tools);
    }

    private static ToolDefinition EchoTool(string name)
        => new()
        {
            Name = name,
            Description = "echo",
            Parameters = new JsonObject { ["type"] = "object" },
            Output = new ToolOutputDefinition(new JsonObject(), (_, value) => [new TextBlock(value.GetRawText())]),
            Execute = (arguments, _) => Task.FromResult<object?>(JsonNode.Parse(arguments.GetRawText())),
        };

    [Fact]
    public async Task InputWhitelist_ToolResultBecomesToon()
    {
        var (ctx, tools) = CreateHost(new Dictionary<string, object?> { ["input"] = "echo" });
        using var registration = tools.Register(EchoTool("echo"));

        var result = await tools.Execute(new ToolExecutionInput
        {
            CallId = ToolCallId.Create("call-1"),
            Name = "echo",
            Arguments = JsonDocument.Parse("""{"users":[{"id":1,"name":"Ada"},{"id":2,"name":"Bob"}]}""").RootElement,
            Signal = TestContext.Current.CancellationToken,
        });

        Assert.False(result.IsError);
        var text = Assert.IsType<TextBlock>(Assert.Single(result.Content)).Text;
        Assert.Contains("users[2]{id,name}:", text);
        await Task.CompletedTask;
    }

    [Fact]
    public async Task Output_RegistersDispatcher_AndStripsOriginalSchemas()
    {
        var (ctx, tools) = CreateHost();
        using var registration = tools.Register(EchoTool("echo"));
        var prompt = ctx.Get<SystemPrompt>(SystemPrompt.ServiceName)!;

        var assembly = await prompt.Assemble(new AssembleContext());

        Assert.DoesNotContain(assembly.Tools, schema => schema.Name == "echo");
        Assert.Contains(assembly.Tools, schema => schema.Name == Dsh.Toon.Plugin.ToolName);
        Assert.Contains(assembly.Sections, section => section.Name == "tool:toon");
    }

    [Fact]
    public async Task Dispatcher_InvokesTarget_WithToonArguments()
    {
        var (ctx, tools) = CreateHost();
        using var registration = tools.Register(EchoTool("echo"));

        var result = await tools.Execute(new ToolExecutionInput
        {
            CallId = ToolCallId.Create("call-1"),
            Name = Dsh.Toon.Plugin.ToolName,
            Arguments = JsonDocument.Parse("""{"name":"echo","arguments":"command: ls -la"}""").RootElement,
            Signal = TestContext.Current.CancellationToken,
        });

        Assert.False(result.IsError);
        var text = Assert.IsType<TextBlock>(Assert.Single(result.Content)).Text;
        Assert.Contains("ls -la", text);
        await Task.CompletedTask;
    }

    [Fact]
    public async Task Dispatcher_AcceptsJsonObjectArguments()
    {
        var (_, tools) = CreateHost();
        using var registration = tools.Register(EchoTool("echo"));

        // 模型偶尔把目标参数直接写成 JSON 对象而非 TOON 文本: 不应报 "given key was not present"。
        var result = await tools.Execute(new ToolExecutionInput
        {
            CallId = ToolCallId.Create("call-obj"),
            Name = Dsh.Toon.Plugin.ToolName,
            Arguments = JsonDocument.Parse("""{"name":"echo","arguments":{"command":"ls -la","description":"List files"}}""").RootElement,
            Signal = TestContext.Current.CancellationToken,
        });

        Assert.False(result.IsError);
        var text = Assert.IsType<TextBlock>(Assert.Single(result.Content)).Text;
        Assert.Contains("ls -la", text);
        Assert.Contains("List files", text);
        await Task.CompletedTask;
    }

    [Fact]
    public async Task Dispatcher_RejectsSelfDispatch()
    {
        var (ctx, tools) = CreateHost();

        var result = await tools.Execute(new ToolExecutionInput
        {
            CallId = ToolCallId.Create("call-1"),
            Name = Dsh.Toon.Plugin.ToolName,
            Arguments = JsonDocument.Parse("""{"name":"toon","arguments":"command: ls"}""").RootElement,
            Signal = TestContext.Current.CancellationToken,
        });

        var text = Assert.IsType<TextBlock>(Assert.Single(result.Content)).Text;
        Assert.Contains("cannot call itself", text);
        await Task.CompletedTask;
    }

    [Fact]
    public async Task Dispatcher_TreatsNullArgumentsAsEmptyObject()
    {
        var (_, tools) = CreateHost();
        using var registration = tools.Register(EchoTool("echo"));

        // 无参工具把 arguments 写成 null(或省略): 目标参数应归一为 {}, 不能变成 JSON Null 元素。
        var result = await tools.Execute(new ToolExecutionInput
        {
            CallId = ToolCallId.Create("call-null"),
            Name = Dsh.Toon.Plugin.ToolName,
            Arguments = JsonDocument.Parse("""{"name":"echo","arguments":"null"}""").RootElement,
            Signal = TestContext.Current.CancellationToken,
        });

        Assert.False(result.IsError);
        var text = Assert.IsType<TextBlock>(Assert.Single(result.Content)).Text;
        Assert.Contains("{}", text);
        await Task.CompletedTask;
    }
}
