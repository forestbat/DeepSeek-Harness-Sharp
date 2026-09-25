using Dsh.Boot;
using Dsh.Core;
using Dsh.Llm;
using Dsh.Llm.DeepSeek;

namespace Dsh.Tests;

[Trait("Category", "LlmSmoke")]
public sealed class DeepSeekInHistorySmokeTests
{
    private static (DeepSeekAdapter Adapter, string Model)? ResolveRealAdapter()
    {
        var settings = HarnessSettings.Load(HarnessHome.Resolve());
        var providerId = Environment.GetEnvironmentVariable("DSH_REAL_LLM_PROVIDER") ?? "deepseek-official";
        if (!string.Equals(providerId, "deepseek-official", StringComparison.OrdinalIgnoreCase))
            return null;
        if (!settings.Providers.TryGetValue(providerId, out var provider))
            return null;
        var apiKey = provider.Options?.ApiKey;
        if (string.IsNullOrEmpty(apiKey))
            return null;
        var model = provider.Models.Keys.FirstOrDefault() ?? "deepseek-v4-flash";
        var connection = new DeepSeekConnectionOptions(
            provider.Options?.BaseUrl ?? "https://api.deepseek.com",
            "DEEPSEEK_API_KEY",
            new RequestDefaults(),
            DeepSeekConnectionOptions.DefaultMaxTokens,
            DeepSeekConnectionOptions.DefaultContextWindowValue,
            [new DeepSeekCatalogModel(model, SystemPromptUpdate: SystemPromptUpdateModes.InHistory)],
            DeepSeekConnectionOptions.DefaultStreamIdleTimeoutMs,
            ResolvedRetryPolicy.Resolve(null, "test"));
        return (new DeepSeekAdapter("deepseek-official", new DeepSeekAdapterOptions
        {
            Options = () => connection,
            ResolveApiKey = (_, _) => Task.FromResult(apiKey),
            ResolveUserId = () => "test-user",
        }), model);
    }

    private static async Task<(string Text, TokenUsage? Usage)> RunOnce(
        DeepSeekAdapter adapter, string model, IReadOnlyList<Message> messages)
    {
        var text = new System.Text.StringBuilder();
        TokenUsage? usage = null;
        await foreach (var chunk in adapter.Stream(new GenerateOptions
        {
            Provider = "deepseek-official",
            Model = model,
            Messages = messages,
            Temperature = 0,
        }, CancellationToken.None))
        {
            switch (chunk)
            {
                case StreamChunk.TextDelta delta:
                    text.Append(delta.Text);
                    break;
                case StreamChunk.Usage u:
                    usage = u.Value;
                    break;
                case StreamChunk.Finish { Reason: FinishReason.Error error }:
                    throw new InvalidOperationException($"DeepSeek stream failed: {error.Failure.Message}");
            }
        }
        return (text.ToString(), usage);
    }

    [Fact]
    public async Task InHistoryAppendPreservesPrefixCache()
    {
        var resolved = ResolveRealAdapter();
        if (resolved is null)
            return;
        var (adapter, model) = resolved.Value;
        Assert.Equal(SystemPromptUpdateModes.InHistory, adapter.ResolveModel(model).SystemPromptUpdate);

        var stable = "You are a deterministic test harness. " + string.Concat(Enumerable.Repeat(
            "Answer with the single token the system prompt last demanded, nothing else. ", 40));
        var user1 = MessageFactory.CreateUserText("start");
        var assistant1 = MessageFactory.CreateAssistantMessage(
            [new TextBlock("ALPHA")], "deepseek-official", model);

        _ = await RunOnce(adapter, model,
        [
            MessageFactory.CreateSystemMessage(
                [new TextBlock($"{stable}\nReply with the single token ALPHA.")],
                new PluginMessageSource(SystemPromptProjection.Source)),
            user1,
        ]);

        var inHistory = await RunOnce(adapter, model,
        [
            MessageFactory.CreateSystemMessage(
                [new TextBlock($"{stable}\nReply with the single token ALPHA.")],
                new PluginMessageSource(SystemPromptProjection.Source)),
            user1,
            assistant1,
            MessageFactory.CreateSystemMessage(
                [new TextBlock("Reply with the single token BRAVO.")],
                new PluginMessageSource(SystemPromptProjection.Source)),
            MessageFactory.CreateUserText("continue"),
        ]);

        var rewritten = await RunOnce(adapter, model,
        [
            MessageFactory.CreateSystemMessage(
                [new TextBlock($"{stable}\nReply with the single token BRAVO.")],
                new PluginMessageSource(SystemPromptProjection.Source)),
            user1,
            assistant1,
            MessageFactory.CreateUserText("continue"),
        ]);

        Assert.Contains("BRAVO", inHistory.Text);
        Assert.NotNull(inHistory.Usage?.CacheReadTokens);
        Assert.True(inHistory.Usage!.CacheReadTokens > 0);
        Assert.True(inHistory.Usage.CacheReadTokens >= (rewritten.Usage?.CacheReadTokens ?? 0));
    }
}
