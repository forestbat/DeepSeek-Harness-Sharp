using Dsh.Boot;
using Dsh.Core;
using Dsh.Llm;
using Dsh.Llm.Anthropic;
using Dsh.Llm.OpenAi;
using Dsh.Runtime;
using Dsh.Agent;

namespace Dsh.Tests;

/**
 * 真机跑多轮 agent, 量每条通道的缓存命中率。
 * 系统提示里放一段固定的长文本(约一万 token), 另有一段每轮都变的运行期上下文:
 * 修复后增量只剩尾部几十个 token, 第三轮起命中率应该到 99% 以上。
 */
[Trait("Category", "LlmSmoke")]
public sealed class PromptCacheSmokeTests
{
    private const string Provider = "deepseek-official";
    private const string Model = "deepseek-flash";
    private const int Rounds = 5;
    private const double ExpectedHitRate = 0.99;

    private static (string ApiKey, string BaseUrl)? ResolveCredentials()
    {
        var settings = HarnessSettings.Load(HarnessHome.Resolve());
        if (!settings.Providers.TryGetValue(Provider, out var provider))
            return null;
        var apiKey = provider.Options?.ApiKey;
        return string.IsNullOrEmpty(apiKey)
            ? null
            : (apiKey, (provider.Options?.BaseUrl ?? "https://api.deepseek.com").TrimEnd('/'));
    }

    private static string Corpus(string channel)
        => string.Concat(Enumerable.Repeat(
            "The harness keeps a deterministic corpus in the system prompt so every request shares a long stable prefix. "
                + $"(channel {channel}) ",
            1800));

    [Theory]
    [InlineData("deepseek")]
    [InlineData("openai-chat")]
    [InlineData("openai-responses")]
    [InlineData("anthropic")]
    public async Task CacheHitRate_ReachesNinetyNinePercentAfterAFewRounds(string channel)
    {
        if (ResolveCredentials() is not { } credentials)
            return;
        var adapter = BuildAdapter(channel, credentials);
        var rates = await RunRounds(adapter);

        var report = $"{channel} ({adapter.ProviderInfo.Id}) hit rates: {string.Join(", ", rates.Select(rate => rate.ToString("P2")))}";
        await Console.Out.WriteLineAsync(report);
        Assert.True(rates.Length == Rounds, $"{report} — expected {Rounds} measured requests");
        Assert.True(rates[2..].All(rate => rate >= ExpectedHitRate), report);
    }

    /** 真实 AgentLoop + 真实适配器, 每轮换一次运行期上下文文本, 收集每次请求的缓存命中率。 */
    private static async Task<double[]> RunRounds(LlmAdapter adapter)
    {
        var ctx = new Context();
        _ = new SessionStore(ctx);
        var prompt = new SystemPrompt(ctx, new SystemPromptConfig());
        _ = new ToolRuntime(ctx);
        var llm = new LlmRuntime(ctx);
        var agents = new AgentRegistry(ctx);
        _ = new AgentLoop(ctx);
        _ = prompt.Section(PromptSection.Literal("test:corpus", Corpus(adapter.ProviderInfo.Id)));
        var round = 0;
        _ = prompt.Context(new PromptContext("test:runtime", _ => $"Runtime marker: round {round}."));
        llm.RegisterAdapter([adapter.ProviderInfo.Id], adapter);
        var handle = await agents.Create(new CreateAgentOptions(
            SessionId.Create($"session-cache-{Guid.NewGuid():N}"),
            null,
            new AgentOptions(adapter.ProviderInfo.Id, Model)), TestContext.Current.CancellationToken);
        var agent = (AgentLoopAgent)handle.Agent;

        for (round = 1; round <= Rounds; round++)
        {
            agent.Followup(MessageFactory.CreateUserText($"round {round}"));
            await agent.WhenIdle();
        }

        return
        [
            .. agent.Session.SnapshotEvents()
                .Select(sessionEvent => sessionEvent.Data)
                .OfType<AssistantMessagePayload>()
                .Select(payload => payload.Usage)
                .Where(usage => usage is { InputTokens: > 0 })
                .Select(usage => (double)usage!.CacheReadTokens / usage.InputTokens),
        ];
    }

    private static LlmAdapter BuildAdapter(string channel, (string ApiKey, string BaseUrl) credentials)
        => channel switch
        {
            "deepseek" => DeepSeekAdapter(credentials),
            "openai-chat" => new OpenAiCompatibleAdapter(
                channel, $"{credentials.BaseUrl}/v1", credentials.ApiKey, [new ProviderModelSpec(Model)]),
            "openai-responses" => new OpenAiCompatibleAdapter(
                channel, $"{credentials.BaseUrl}/v1", credentials.ApiKey, [new ProviderModelSpec(Model)], useResponses: true),
            _ => new AnthropicAdapter("anthropic", $"{credentials.BaseUrl}/anthropic", credentials.ApiKey, [Model]),
        };

    private static Dsh.Llm.DeepSeek.DeepSeekAdapter DeepSeekAdapter((string ApiKey, string BaseUrl) credentials)
    {
        var connection = new Dsh.Llm.DeepSeek.DeepSeekConnectionOptions(
            credentials.BaseUrl,
            "DEEPSEEK_API_KEY",
            new Dsh.Llm.DeepSeek.RequestDefaults(),
            Dsh.Llm.DeepSeek.DeepSeekConnectionOptions.DefaultMaxTokens,
            Dsh.Llm.DeepSeek.DeepSeekConnectionOptions.DefaultContextWindowValue,
            [new Dsh.Llm.DeepSeek.DeepSeekCatalogModel(Model, SystemPromptUpdate: SystemPromptUpdateModes.InHistory)],
            Dsh.Llm.DeepSeek.DeepSeekConnectionOptions.DefaultStreamIdleTimeoutMs,
            ResolvedRetryPolicy.Resolve(null, "test"));
        return new Dsh.Llm.DeepSeek.DeepSeekAdapter(Provider, new Dsh.Llm.DeepSeek.DeepSeekAdapterOptions
        {
            Options = () => connection,
            ResolveApiKey = (_, _) => Task.FromResult(credentials.ApiKey),
            ResolveUserId = () => "test-user",
        });
    }
}



