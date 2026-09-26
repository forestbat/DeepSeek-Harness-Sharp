using System.Text.Json;
using System.Text.Json.Nodes;
using Dsh.Core;
using Dsh.Llm;
using Dsh.Presets;
using Dsh.Runtime;
using static Dsh.Inspection.Plugin;

namespace Dsh.Tests;

public sealed class InspectionToolsTests
{
    [Fact]
    public async Task List_ReportsFourSources_WithExplicitEventDegradation()
    {
        using var harness = new Harness();
        await harness.ComposeAsync();
        var agent = harness.OpenCreative();

        var success = Assert.IsType<ToolExecutionResult.Success>(
            await harness.Execute("cordis_inspect_list", "{}", agent));
        var categories = JsonNode.Parse(success.Value.GetRawText())!["categories"]!.AsArray();

        Assert.Equal(
            new[] { "service", "event", "config", "tool" },
            categories.Select(category => category!["category"]!.GetValue<string>()));

        var service = Category(categories, "service");
        Assert.True(service["available"]!.GetValue<bool>());
        Assert.Contains(service["items"]!.AsArray(), item => item!["name"]!.GetValue<string>() == "seed-service");
        Assert.Contains(service["items"]!.AsArray(), item => item!["name"]!.GetValue<string>() == "seed-provided");

        var events = Category(categories, "event");
        Assert.False(events["available"]!.GetValue<bool>());
        Assert.False(string.IsNullOrWhiteSpace(events["reason"]!.GetValue<string>()));

        var config = Category(categories, "config");
        Assert.True(config["available"]!.GetValue<bool>());
        Assert.Contains(config["items"]!.AsArray(), item => item!["name"]!.GetValue<string>() == "global_default_model");
        Assert.Contains(config["items"]!.AsArray(), item => item!["name"]!.GetValue<string>() == "plugins");

        var tool = Category(categories, "tool");
        Assert.True(tool["available"]!.GetValue<bool>());
        Assert.Contains(tool["items"]!.AsArray(), item => item!["name"]!.GetValue<string>() == "cordis_inspect_list");
        Assert.Contains(tool["items"]!.AsArray(), item => item!["name"]!.GetValue<string>() == "cordis_inspect_query");
    }

    [Fact]
    public async Task Query_Service_ReturnsProviderAndState()
    {
        using var harness = new Harness();
        await harness.ComposeAsync();
        var agent = harness.OpenCreative();

        var data = await Query(harness, agent, """{"category":"service","name":"seed-provided"}""");

        Assert.Equal("seed-provider", data["provider"]!.GetValue<string>());
        Assert.Equal("active", data["providerState"]!.GetValue<string>());
        Assert.Contains(data["pluginServices"]!.AsArray(), item => item!.GetValue<string>() == "seed-provided");
    }

    [Fact]
    public async Task Query_Tool_ReturnsSchema()
    {
        using var harness = new Harness();
        await harness.ComposeAsync();
        var agent = harness.OpenCreative();

        var data = await Query(harness, agent, """{"category":"tool","name":"cordis_inspect_query"}""");

        Assert.Equal("cordis_inspect_query", data["name"]!.GetValue<string>());
        Assert.False(string.IsNullOrWhiteSpace(data["description"]!.GetValue<string>()));
        Assert.NotNull(data["parameters"]);
    }

    [Fact]
    public async Task Query_Config_ReturnsValue()
    {
        using var harness = new Harness();
        await harness.ComposeAsync();
        var agent = harness.OpenCreative();

        var data = await Query(harness, agent, """{"category":"config","name":"global_default_model"}""");

        Assert.Equal("my-provider/some-model", data.GetValue<string>());
    }

    [Fact]
    public async Task Query_Event_IsExplicitlyUnavailable()
    {
        using var harness = new Harness();
        await harness.ComposeAsync();
        var agent = harness.OpenCreative();

        var result = await harness.Execute("cordis_inspect_query", """{"category":"event","name":"anything"}""", agent);

        var failure = Assert.IsType<ToolExecutionResult.Failure>(result);
        Assert.Contains("event table is internal", string.Concat(failure.Content.OfType<TextBlock>().Select(block => block.Text)));
    }

    [Fact]
    public async Task Query_UnknownName_Fails()
    {
        using var harness = new Harness();
        await harness.ComposeAsync();
        var agent = harness.OpenCreative();

        var result = await harness.Execute("cordis_inspect_query", """{"category":"service","name":"no-such-service"}""", agent);

        Assert.IsType<ToolExecutionResult.Failure>(result);
    }

    private static async Task<JsonNode> Query(Harness harness, IAgent agent, string arguments)
    {
        var success = Assert.IsType<ToolExecutionResult.Success>(
            await harness.Execute("cordis_inspect_query", arguments, agent));
        return JsonNode.Parse(success.Value.GetRawText())!["data"]!;
    }

    private static JsonNode Category(JsonArray categories, string name)
        => categories.Single(category => category!["category"]!.GetValue<string>() == name)!;

    private sealed class Harness : IDisposable
    {
        public Harness()
        {
            Root = Path.Combine(Path.GetTempPath(), $"dsh-inspection-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Root);
            File.WriteAllText(Path.Combine(Root, "settings.yaml"), """
                global_default_model: my-provider/some-model
                providers:
                  my-provider:
                    type: openai-compatible
                    options:
                      baseUrl: https://example.invalid
                plugins:
                  "@deepseek-ai/dsh-tool-cordis":
                    enabled: true
                """);
            Ctx = new Context();
            Ctx.SetOwn("dshHomePath", Root);
            _ = new SystemPrompt(Ctx, new SystemPromptConfig());
            Tools = new ToolRuntime(Ctx);
            Presets = PresetController.Register(Ctx);
            Ctx.Provide("seed-service", new object());
        }

        public string Root { get; }
        public Context Ctx { get; }
        public ToolRuntime Tools { get; }
        public PresetController Presets { get; }

        public InspectionTestAgent OpenCreative()
        {
            var agent = InspectionTestAgent.Open(Ctx);
            Presets.Set(agent, "creative");
            return agent;
        }

        public async Task ComposeAsync()
        {
            Ctx.Plugin(new PluginDefinition
            {
                Name = "seed-provider",
                Apply = (ctx, _) =>
                {
                    ctx.Provide("seed-provided", new object());
                    return null;
                },
            });
            Ctx.Plugin(InspectionTestPlugin.Definition(InspectTools));
            await Ctx.Scheduler.SettleAsync();
        }

        public Task<ToolExecutionResult> Execute(string name, string arguments, IAgent? agent = null)
            => Tools.Execute(new ToolExecutionInput
            {
                CallId = ToolCallId.Create($"call-{Guid.NewGuid():N}"),
                Name = name,
                Arguments = JsonDocument.Parse(arguments).RootElement,
                Agent = agent,
                Signal = default,
            });

        public void Dispose()
        {
            if (Directory.Exists(Root))
                Directory.Delete(Root, true);
        }
    }
}
