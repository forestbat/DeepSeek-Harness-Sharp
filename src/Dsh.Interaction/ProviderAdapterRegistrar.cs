using Cordis;
using Dsh.Boot;
using Dsh.Core;
using Dsh.Llm;
using Dsh.Llm.Anthropic;
using Dsh.Llm.DeepSeek;
using Dsh.Llm.OpenAi;

namespace Dsh.Interaction;

internal static class ProviderAdapterRegistrar
{
    private static readonly IReadOnlyList<DeepSeekCatalogModel> DeepSeekCatalog =
    [
        new("deepseek-v4-flash", "DeepSeek-V4-Flash",
            "Fast, efficient, and economical; suited to focused, routine, or parallel tasks.",
            DeepSeekConnectionOptions.DefaultContextWindowValue),
        new("deepseek-v4-pro", "DeepSeek-V4-Pro",
            "Stronger agentic coding, knowledge, and difficult reasoning; suited to complex or quality-critical tasks at higher cost.",
            DeepSeekConnectionOptions.DefaultContextWindowValue),
        new("deepseek-v4-flash-vision-exp", "DeepSeek-V4-Flash-Vision-Exp",
            null,
            DeepSeekConnectionOptions.DefaultContextWindowValue,
            null,
            ["text", "image"]),
    ];

    public static AdapterRegistrationHandle RegisterProviderAdapter(
        Context ctx,
        string providerId,
        ProviderSettings? provider,
        string baseUrl,
        string? apiKeyEnv,
        string? apiKey,
        HarnessOptions options,
        ICredentials credentials,
        LlmRuntime llm)
    {
        if (string.Equals(provider?.Type, "anthropic", StringComparison.OrdinalIgnoreCase))
        {
            var resolvedApiKey = apiKey ?? credentials.Get(apiKeyEnv ?? "ANTHROPIC_API_KEY");
            var adapter = new AnthropicAdapter(providerId, baseUrl, resolvedApiKey, provider?.Models.Keys.ToList());
            return llm.RegisterAdapter([providerId], adapter);
        }
        if (provider?.Type is "openai-compatible" or "openai-responses")
        {
            var resolvedApiKey = apiKey ?? credentials.Get(apiKeyEnv ?? "OPENAI_API_KEY");
            var useResponses = string.Equals(provider.Type, "openai-responses", StringComparison.OrdinalIgnoreCase);
            var adapter = new OpenAiCompatibleAdapter(
                providerId,
                Endpoint.NormalizeBaseUrl(baseUrl),
                resolvedApiKey,
                provider.Models.Keys.ToList(),
                useResponses: useResponses);
            return llm.RegisterAdapter([providerId], adapter);
        }
        return RegisterDeepSeekAdapter(providerId, baseUrl, apiKeyEnv, apiKey, options, credentials, llm);
    }

    private static AdapterRegistrationHandle RegisterDeepSeekAdapter(
        string providerId,
        string baseUrl,
        string? apiKeyEnv,
        string? apiKey,
        HarnessOptions options,
        ICredentials credentials,
        LlmRuntime llm)
    {
        var resolvedBaseUrl = Endpoint.NormalizeBaseUrl(baseUrl);
        var resolvedApiKeyEnv = apiKeyEnv ?? HarnessComposer.DefaultApiKeyEnv;
        var connection = new DeepSeekConnectionOptions(
            resolvedBaseUrl,
            resolvedApiKeyEnv,
            new RequestDefaults(),
            DeepSeekConnectionOptions.DefaultMaxTokens,
            DeepSeekConnectionOptions.DefaultContextWindowValue,
            DeepSeekCatalog,
            DeepSeekConnectionOptions.DefaultStreamIdleTimeoutMs,
            ResolvedRetryPolicy.Resolve(null, "llm-deepseek"));
        var adapter = new DeepSeekAdapter(providerId, new DeepSeekAdapterOptions
        {
            Options = () => connection,
            ResolveApiKey = (conn, _) =>
            {
                var raw = apiKey ?? credentials.Get(conn.ApiKeyEnv)
                    ?? throw new LlmException(new LlmFailure(
                        $"provider \"{providerId}\" credential \"{conn.ApiKeyEnv}\" is not configured",
                        LlmFailureCodes.MissingCredential));
                if (!ApiKey.Normalize(raw, out var key, out var rejection))
                {
                    throw new LlmException(new LlmFailure(
                        $"provider \"{providerId}\" credential \"{conn.ApiKeyEnv}\" is unusable: {rejection}",
                        LlmFailureCodes.InvalidCredential));
                }
                return Task.FromResult(key);
            },
            ResolveUserId = () => AnonymousUserId.Resolve(options.Home),
        });
        return llm.RegisterAdapter([providerId], adapter);
    }
}
