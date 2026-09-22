using Dsh.Core;
using Dsh.Llm;
using Dsh.Llm.Anthropic;

namespace Dsh.Tests;

/** Anthropic 推理强度: /v1/models 的 capabilities.effort 解析、回退链与 output_config.effort 请求体。 */
public class AnthropicReasoningTests
{
    [Fact]
    public void Parse_Reads_Capabilities_Effort_Levels()
    {
        const string json = """
            {"data":[
              {"id":"claude-opus-5","capabilities":{"effort":{"low":{"supported":true},"medium":{"supported":true},"high":{"supported":true},"max":{"supported":true},"xhigh":{"supported":true},"supported":true}}},
              {"id":"claude-sonnet-5","capabilities":{"effort":{"low":{"supported":true},"medium":{"supported":true},"high":{"supported":false},"supported":true}}},
              {"id":"old-model","capabilities":{"thinking":{"supported":true,"types":{"adaptive":{"supported":false},"enabled":{"supported":true}}}}},
              {"id":"plain"}
            ]}
            """;

        var parsed = AnthropicModelMetadataSource.Parse(json);

        Assert.Equal(["low", "medium", "high", "max", "xhigh"], parsed["claude-opus-5"].Efforts.Select(effort => effort.Id.Value));
        Assert.Equal("high", parsed["claude-opus-5"].DefaultEffort?.ToString());
        Assert.Equal(["low", "medium"], parsed["claude-sonnet-5"].Efforts.Select(effort => effort.Id.Value));
        Assert.Equal("medium", parsed["claude-sonnet-5"].DefaultEffort?.ToString());
        Assert.False(parsed.ContainsKey("old-model"));
        Assert.False(parsed.ContainsKey("plain"));
    }

    [Fact]
    public void ResolveModel_Prefers_Online_Metadata_Over_Table()
    {
        var online = new LlmModelReasoningInfo(
            [
                new LlmReasoningEffortInfo(ReasoningEffortId.Create("low"), "Low"),
                new LlmReasoningEffortInfo(ReasoningEffortId.Create("max"), "Max"),
            ],
            ReasoningEffortId.Create("max"));
        var adapter = new AnthropicAdapter(
            "anthropic",
            "https://example.invalid",
            "sk-test",
            ["claude-opus-5"],
            metadataSource: new StubReasoningSource("claude-opus-5", online));

        var info = adapter.ResolveModel("claude-opus-5");

        Assert.Equal(["low", "max"], info.Reasoning!.Efforts.Select(effort => effort.Id.Value));
        Assert.Equal("max", info.Reasoning.DefaultEffort?.ToString());
    }

    [Fact]
    public void ResolveModel_Falls_Back_To_Builtin_Table()
    {
        var adapter = new AnthropicAdapter("anthropic", "https://example.invalid", "sk-test", ["claude-opus-5"]);

        var info = adapter.ResolveModel("claude-opus-5");

        Assert.Equal(["low", "medium", "high"], info.Reasoning!.Efforts.Select(effort => effort.Id.Value));
        Assert.Equal("high", info.Reasoning.DefaultEffort?.ToString());
    }

    [Fact]
    public async Task Stream_Sends_Output_Config_Effort()
    {
        var handler = new CapturingHandler();
        var adapter = new AnthropicAdapter("anthropic", "https://example.invalid", "sk-test", ["claude-opus-5"], new HttpClient(handler));
        var options = new GenerateOptions
        {
            Provider = "anthropic",
            Model = "claude-opus-5",
            ReasoningEffort = ReasoningEffortId.Create("max"),
            Messages = [MessageFactory.CreateUserText("hi")],
        };

        await Assert.ThrowsAnyAsync<Exception>(async () =>
        {
            await foreach (var _ in adapter.Stream(options, CancellationToken.None))
            {
            }
        });

        Assert.Contains("\"output_config\":{\"effort\":\"max\"}", handler.LastBody);
    }

    private sealed class StubReasoningSource(string model, LlmModelReasoningInfo reasoning) : IModelReasoningSource
    {
        public LlmModelReasoningInfo? ReasoningFor(string candidate)
            => candidate == model ? reasoning : null;
    }

    private sealed class CapturingHandler : HttpMessageHandler
    {
        public string? LastBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Content is not null)
                LastBody = await request.Content.ReadAsStringAsync(cancellationToken);
            throw new HttpRequestException("captured");
        }
    }
}
