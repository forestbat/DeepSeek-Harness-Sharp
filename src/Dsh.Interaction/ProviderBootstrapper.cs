using Cordis;
using Dsh.Boot;
using Dsh.Core;

namespace Dsh.Interaction;

public static class ProviderBootstrapper
{
    public static IDisposable Register(Context ctx, HarnessOptions options)
    {
        var settings = HarnessSettings.Load(options.Home);
        var defaultModel = settings.ResolveDefaultModel();
        var provider = options.Provider ?? defaultModel?.Provider ?? HarnessComposer.DefaultProvider;
        var providerSettings = settings.ResolveProvider(provider);
        var baseUrl = options.BaseUrl ?? providerSettings?.Options?.BaseUrl ?? HarnessComposer.DefaultBaseUrl;
        var apiKeyEnv = options.ApiKeyEnv ?? providerSettings?.Options?.ApiKeyEnv ?? HarnessComposer.DefaultApiKeyEnv;
        var apiKey = options.ApiKey ?? providerSettings?.Options?.ApiKey;
        var credentials = ctx.GetProp("credentials") as ICredentials
            ?? throw new InvalidOperationException("credentials is required for provider registration");
        var llm = ctx.Get<LlmRuntime>(LlmRuntime.ServiceName)
            ?? throw new InvalidOperationException("llm is required for provider registration");
        return ProviderAdapterRegistrar.RegisterProviderAdapter(
            ctx,
            provider,
            providerSettings,
            baseUrl,
            apiKeyEnv,
            apiKey,
            options,
            credentials,
            llm);
    }
}