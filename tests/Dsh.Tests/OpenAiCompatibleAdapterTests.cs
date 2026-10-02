using Dsh.Llm;
using Dsh.Llm.OpenAi;

namespace Dsh.Tests;

/** openai-compatible 适配器的推理强度解析: 端点实时元数据优先, 其次内置 models.dev 快照。 */
public class OpenAiCompatibleAdapterTests
{
    [Fact]
    public void ResolveModel_Uses_ModelsDev_For_Known_Provider()
    {
        var adapter = new OpenAiCompatibleAdapter(
            "deepseek",
            "https://api.deepseek.com",
            "sk-test",
            [new ProviderModelSpec("deepseek-flash")]);

        var info = adapter.ResolveModel("deepseek-flash");

        Assert.Equal(["off", "low", "high", "max"], info.Reasoning!.Efforts.Select(effort => effort.Id.Value));
        Assert.Equal("high", info.Reasoning.DefaultEffort?.ToString());
        Assert.Null(adapter.ResolveModel("unknown-model").Reasoning);
    }

    [Fact]
    public void ResolveModel_Unknown_Provider_Has_No_Reasoning()
    {
        var adapter = new OpenAiCompatibleAdapter(
            "boomfirst",
            "https://example.invalid/v1",
            "sk-test",
            [new ProviderModelSpec("gpt-6-luna")]);

        Assert.Null(adapter.ResolveModel("gpt-6-luna").Reasoning);
    }

    [Fact]
    public void ResolveModel_Uses_Online_Metadata_Before_ModelsDev()
    {
        var online = new LlmModelReasoningInfo(
            [
                new LlmReasoningEffortInfo(ReasoningEffortId.Create("low"), "Low"),
                new LlmReasoningEffortInfo(ReasoningEffortId.Create("max"), "Max"),
            ],
            ReasoningEffortId.Create("max"));
        var adapter = new OpenAiCompatibleAdapter(
            "deepseek",
            "https://api.deepseek.com",
            "sk-test",
            [new ProviderModelSpec("deepseek-flash")],
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
