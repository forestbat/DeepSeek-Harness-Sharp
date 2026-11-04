using Dsh.Boot;
using Dsh.Core;
using Dsh.Llm;
using Dsh.Llm.Anthropic;
using Dsh.Llm.OpenAi;

namespace Dsh.Tests;

/**
 * 真机验证前缀缓存: 同一条稳定前缀在 deepseek / openai chat / openai responses / anthropic 四条通道上都应命中。
 * 各家回报缓存的字段不同, 统一走 TokenUsage.CacheReadTokens。
 */
[Trait("Category", "LlmSmoke")]
public sealed class PromptCacheSmokeTests
{
    private const string Model = "deepseek-flash";
    private const string Provider = "deepseek-official";

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

    private static string Stable()
        => "You are a deterministic probe harness. " + string.Concat(Enumerable.Repeat(
            "Answer with the single token the system prompt last demanded, nothing else. ", 40));

    private static IReadOnlyList<Message> FirstTurn()
    {
        var system = MessageFactory.CreateSystemMessage(
            [new TextBlock($"{Stable()}\nReply with the single token ALPHA.")],
            new PluginMessageSource(SystemPromptProjection.Source));
        return [system, MessageFactory.CreateUserText("start")];
    }

    private static IReadOnlyList<Message> SecondTurn()
        =>
        [
            .. FirstTurn(),
            MessageFactory.CreateAssistantMessage([new TextBlock("ALPHA")], Provider, Model),
            MessageFactory.CreateUserText("again"),
        ];

    private static async Task<TokenUsage?> RunOnce(LlmAdapter adapter, IReadOnlyList<Message> messages)
    {
        TokenUsage? usage = null;
        await foreach (var chunk in adapter.Stream(new GenerateOptions
        {
            Provider = adapter.ProviderInfo.Id,
            Model = Model,
            Messages = messages,
            Temperature = 0,
        }, CancellationToken.None))
        {
            switch (chunk)
            {
                case StreamChunk.Usage measured:
                    usage = measured.Value;
                    break;
                case StreamChunk.Finish { Reason: FinishReason.Error error }:
                    throw new InvalidOperationException($"{adapter.ProviderInfo.Id} stream failed: {error.Failure.Message}");
            }
        }
        return usage;
    }

    [Theory]
    [InlineData("openai-chat", false)]
    [InlineData("openai-responses", true)]
    public async Task OpenAiFamily_ReusesThePrefixCache(string channel, bool useResponses)
    {
        if (ResolveCredentials() is not { } credentials)
            return;
        var adapter = new OpenAiCompatibleAdapter(
            channel,
            $"{credentials.BaseUrl}/v1",
            credentials.ApiKey,
            [new ProviderModelSpec(Model)],
            useResponses: useResponses);

        _ = await RunOnce(adapter, FirstTurn());
        var second = await RunOnce(adapter, SecondTurn());

        Assert.NotNull(second);
        Assert.True(second.CacheReadTokens > 0, $"{channel} reported no cache read: {second.InputTokens} in");
    }

    [Fact]
    public async Task Anthropic_ReusesThePrefixCache()
    {
        if (ResolveCredentials() is not { } credentials)
            return;
        var adapter = new AnthropicAdapter("anthropic", $"{credentials.BaseUrl}/anthropic", credentials.ApiKey, [Model]);

        _ = await RunOnce(adapter, FirstTurn());
        var second = await RunOnce(adapter, SecondTurn());

        Assert.NotNull(second);
        Assert.True(second.CacheReadTokens > 0, $"anthropic reported no cache read: {second.InputTokens} in");
    }

    /** DeepSeek 原生通道: 变化后的系统提示叠在尾部时, 前缀缓存必须仍然命中。 */
    [Fact]
    public async Task DeepSeekInHistory_KeepsThePrefixCached()
    {
        if (ResolveCredentials() is not { } credentials)
            return;
        var connection = new Dsh.Llm.DeepSeek.DeepSeekConnectionOptions(
            credentials.BaseUrl,
            "DEEPSEEK_API_KEY",
            new Dsh.Llm.DeepSeek.RequestDefaults(),
            Dsh.Llm.DeepSeek.DeepSeekConnectionOptions.DefaultMaxTokens,
            Dsh.Llm.DeepSeek.DeepSeekConnectionOptions.DefaultContextWindowValue,
            [new Dsh.Llm.DeepSeek.DeepSeekCatalogModel(Model, SystemPromptUpdate: SystemPromptUpdateModes.InHistory)],
            Dsh.Llm.DeepSeek.DeepSeekConnectionOptions.DefaultStreamIdleTimeoutMs,
            ResolvedRetryPolicy.Resolve(null, "test"));
        var adapter = new Dsh.Llm.DeepSeek.DeepSeekAdapter(Provider, new Dsh.Llm.DeepSeek.DeepSeekAdapterOptions
        {
            Options = () => connection,
            ResolveApiKey = (_, _) => Task.FromResult(credentials.ApiKey),
            ResolveUserId = () => "test-user",
        });

        _ = await RunOnce(adapter, FirstTurn());
        var appended = await RunOnce(adapter,
        [
            .. SecondTurn(),
            MessageFactory.CreateSystemMessage(
                [new TextBlock("Reply with the single token BRAVO.")],
                new PluginMessageSource(SystemPromptProjection.Source)),
        ]);

        Assert.NotNull(appended);
        Assert.True(appended.CacheReadTokens > 0, $"deepseek reported no cache read: {appended.InputTokens} in");
    }
}
