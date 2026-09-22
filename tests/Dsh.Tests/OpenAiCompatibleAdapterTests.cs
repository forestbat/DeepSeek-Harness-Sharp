using Dsh.Llm;
using Dsh.Llm.OpenAi;

namespace Dsh.Tests;

/** openai-compatible 适配器的推理强度解析: 专属表优先, settings 的 reasoning: true 标记作为回退。 */
public class OpenAiCompatibleAdapterTests
{
    [Fact]
    public void ResolveModel_Uses_Reasoning_Flag_When_No_Provider_Table()
    {
        var adapter = new OpenAiCompatibleAdapter(
            "boomfirst",
            "https://example.invalid/v1",
            "sk-test",
            [new ProviderModelSpec("deepseek-v4-flash", Reasoning: true), new ProviderModelSpec("plain-model")]);

        var flagged = adapter.ResolveModel("deepseek-v4-flash");
        var plain = adapter.ResolveModel("plain-model");

        Assert.Equal(["off", "low", "high", "max"], flagged.Reasoning!.Efforts.Select(effort => effort.Id.Value));
        Assert.Equal("high", flagged.Reasoning.DefaultEffort?.ToString());
        Assert.Null(plain.Reasoning);
    }

    [Fact]
    public void ResolveModel_Prefers_Provider_Table_Over_Flag()
    {
        var adapter = new OpenAiCompatibleAdapter(
            "deepseek-official",
            "https://example.invalid/v1",
            "sk-test",
            [new ProviderModelSpec("deepseek-flash")]);

        var info = adapter.ResolveModel("deepseek-flash");

        Assert.Equal(4, info.Reasoning!.Efforts.Count);
        Assert.Equal("high", info.Reasoning.DefaultEffort?.ToString());
    }

    [Fact]
    public void ResolveModel_Uses_Online_Metadata_Before_Table_And_Flag()
    {
        var online = new LlmModelReasoningInfo(
            [
                new LlmReasoningEffortInfo(ReasoningEffortId.Create("low"), "Low"),
                new LlmReasoningEffortInfo(ReasoningEffortId.Create("max"), "Max"),
            ],
            ReasoningEffortId.Create("max"));
        var adapter = new OpenAiCompatibleAdapter(
            "pa",
            "https://example.invalid/v1",
            "sk-test",
            [new ProviderModelSpec("deepseek-flash", Reasoning: true)],
            metadataSource: new StubReasoningSource("deepseek-flash", online));

        var info = adapter.ResolveModel("deepseek-flash");

        Assert.Equal(["low", "max"], info.Reasoning!.Efforts.Select(effort => effort.Id.Value));
        Assert.Equal("max", info.Reasoning.DefaultEffort?.ToString());
        Assert.Null(adapter.ResolveModel("other-model").Reasoning);
    }

    [Fact]
    public void Parse_Reads_Variants_And_Supported_Parameters()
    {
        const string json = """
            {"data":[
              {"id":"kilo/stealth-a","opencode":{"variants":"@{none=; low=; medium=; high=; xhigh=; max=}"},"supported_parameters":["tools","reasoning"]},
              {"id":"kilo/generic-effort","opencode":{},"supported_parameters":["reasoning","reasoning_effort"]},
              {"id":"kilo/plain"},
              {"id":"kilo/empty-variants","opencode":{"variants":""}},
              {"id":"kilo/object-variants","opencode":{"variants":{"low":"","high":""}}}
            ]}
            """;

        var parsed = ModelMetadataReasoningSource.Parse(json);

        Assert.Equal(["none", "low", "medium", "high", "xhigh", "max"], parsed["kilo/stealth-a"].Efforts.Select(effort => effort.Id.Value));
        Assert.Equal("medium", parsed["kilo/stealth-a"].DefaultEffort?.ToString());
        Assert.Equal(["low", "medium", "high"], parsed["kilo/generic-effort"].Efforts.Select(effort => effort.Id.Value));
        Assert.Equal(["low", "high"], parsed["kilo/object-variants"].Efforts.Select(effort => effort.Id.Value));
        Assert.False(parsed.ContainsKey("kilo/plain"));
        Assert.False(parsed.ContainsKey("kilo/empty-variants"));
    }

    private sealed class StubReasoningSource(string model, LlmModelReasoningInfo reasoning) : IModelReasoningSource
    {
        public LlmModelReasoningInfo? ReasoningFor(string candidate)
            => candidate == model ? reasoning : null;
    }
}
