using Cordis;
using Dsh.Core;
using Dsh.Interaction;
using Dsh.Llm;
using Dsh.Llm.Anthropic;
using Dsh.Llm.DeepSeek;
using Dsh.Llm.OpenAi;
using Dsh.Persistence;

namespace Dsh.Boot;

public sealed record HarnessOptions(
    HarnessHome Home,
    string? Cwd = null,
    string? Provider = null,
    string? Model = null,
    string? BaseUrl = null,
    string? ApiKeyEnv = null,
    string? ApiKey = null,
    string? ReasoningEffort = null,
    string? SettingsConfig = null);

public sealed class HarnessApp : IDisposable
{
    public required Context Ctx { get; init; }
    public required HarnessHome Home { get; init; }
    public required ICredentials Credentials { get; init; }
    public required JsonlSessionPersistence Persistence { get; init; }
    public required string Provider { get; init; }
    public required string Model { get; init; }
    public required string? ReasoningEffort { get; init; }

    private readonly List<IDisposable> _disposables = [];

    internal void Track(IDisposable disposable) => _disposables.Add(disposable);

    public void Dispose()
    {
        Persistence.Dispose();
        foreach (var disposable in ((IEnumerable<IDisposable>)_disposables).Reverse())
            disposable.Dispose();
    }
}

public static class HarnessComposer
{
    public const string DefaultProvider = "deepseek-official";
    public const string DefaultModel = "deepseek-v4-flash";
    public const string DefaultBaseUrl = "https://api.deepseek.com";
    public const string DefaultApiKeyEnv = "DEEPSEEK_API_KEY";

    private static readonly IReadOnlyList<DeepSeekCatalogModel> Catalog =
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

    public static HarnessApp Compose(HarnessOptions options)
    {
        options.Home.Ensure();
        var credentials = new EnvCredentials(options.Home, options.Cwd);
        var ctx = new Context();
        ctx.Provide("dshHomePath", options.Home.Root);
        ctx.Provide("credentials", credentials);

        var settings = HarnessSettings.Load(options.Home);
        var config = settings.ResolveConfig(options.SettingsConfig);
        var provider = options.Provider ?? config?.Provider ?? DefaultProvider;
        var model = options.Model ?? config?.Model ?? DefaultModel;
        var reasoningEffort = options.ReasoningEffort ?? config?.ReasoningEffort;

        var persistence = new JsonlSessionPersistence(options.Home.SessionsPath);

        _ = new SessionStore(ctx);
        _ = new SystemPrompt(ctx, new SystemPromptConfig());
        _ = new ToolRuntime(ctx);
        var llm = new LlmRuntime(ctx);
        _ = new AgentRegistry(ctx);
        _ = new AgentLoop(ctx, new AgentLoopConfig(), _ => persistence);
        _ = ApprovalService.Register(ctx);
        _ = UserQuestionService.Register(ctx);
        _ = CommandsService.Register(ctx);
        var modelCommand = ModelCommand.Register(ctx, options.Home);
        var reasoningCommand = ReasoningCommand.Register(ctx);
        var providerCommand = ProviderCommand.Register(ctx, options.Home);
        var goalCommand = GoalCommand.Register(ctx);
        var skillCommand = SkillCommand.Register(ctx);
        var memoryCommand = MemoryCommand.Register(ctx);
        var pluginCommand = PluginCommand.Register(ctx);
        var mcpCommand = McpCommand.Register(ctx, options.Home);
        var safetyGuard = SafetyCommandGuard.Register(ctx, settings.Safety);

        var providerSettings = settings.ResolveProvider(provider);
        var registration = RegisterProviderAdapter(
            ctx,
            provider,
            providerSettings,
            options.BaseUrl ?? providerSettings?.BaseUrl ?? DefaultBaseUrl,
            options.ApiKeyEnv ?? providerSettings?.ApiKeyEnv ?? DefaultApiKeyEnv,
            options.ApiKey ?? providerSettings?.ApiKey,
            options,
            credentials,
            llm);

        var app = new HarnessApp
        {
            Ctx = ctx,
            Home = options.Home,
            Credentials = credentials,
            Persistence = persistence,
            Provider = provider,
            Model = model,
            ReasoningEffort = reasoningEffort,
        };
        app.Track(registration);
        app.Track(modelCommand);
        app.Track(reasoningCommand);
        app.Track(providerCommand);
        app.Track(goalCommand);
        app.Track(skillCommand);
        app.Track(memoryCommand);
        app.Track(pluginCommand);
        app.Track(mcpCommand);
        app.Track(safetyGuard);
        if (settings.Safety?.AutoApprove == true)
            app.Track(ApprovalAnswerers.AutoApprove(ctx));
        WirePersistence(ctx, persistence);
        return app;
    }

    internal static AdapterRegistrationHandle RegisterDeepSeekAdapter(
        Context ctx,
        string providerId,
        string baseUrl,
        string? apiKeyEnv,
        string? apiKey,
        HarnessOptions options,
        ICredentials credentials,
        LlmRuntime llm)
    {
        var resolvedBaseUrl = Endpoint.NormalizeBaseUrl(baseUrl ?? Environment.GetEnvironmentVariable("DEEPSEEK_BASE_URL") ?? DefaultBaseUrl);
        var resolvedApiKeyEnv = apiKeyEnv ?? DefaultApiKeyEnv;
        var connection = new DeepSeekConnectionOptions(
            resolvedBaseUrl,
            resolvedApiKeyEnv,
            new RequestDefaults(),
            DeepSeekConnectionOptions.DefaultMaxTokens,
            DeepSeekConnectionOptions.DefaultContextWindowValue,
            Catalog,
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

    internal static AdapterRegistrationHandle RegisterProviderAdapter(
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
            var adapter = new AnthropicAdapter(providerId, baseUrl, resolvedApiKey, provider?.ModelIds);
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
                provider.ModelIds,
                useResponses: useResponses);
            return llm.RegisterAdapter([providerId], adapter);
        }
        return RegisterDeepSeekAdapter(ctx, providerId, baseUrl, apiKeyEnv, apiKey, options, credentials, llm);
    }

    internal static void WirePersistence(Context ctx, JsonlSessionPersistence persistence)
    {
        var handles = new Dictionary<SessionId, ISessionHandle>();
        ctx.On(SessionStore.CreatedEvent, (_, args) =>
        {
            var session = (Session)args[0]!;
            if (handles.ContainsKey(session.Id))
                return new ValueTask<object?>();
            var handle = persistence.Create(session.Header, session.InheritedEventCount);
            handles[session.Id] = handle;
            var seed = session.SnapshotEvents();
            if (seed.Count > 0)
                handle.Append(seed);
            return new ValueTask<object?>();
        });
        ctx.On(SessionStore.EventEvent, (_, args) =>
        {
            var session = (Session)args[0]!;
            if (handles.TryGetValue(session.Id, out var handle))
                handle.Append([(SessionEvent)args[1]!]);
            return new ValueTask<object?>();
        });
        ctx.On(SessionStore.FlushEvent, (_, args) =>
        {
            var session = (Session)args[0]!;
            if (handles.TryGetValue(session.Id, out var handle))
                handle.Flush();
            return new ValueTask<object?>();
        });
        ctx.On(SessionStore.DisposedEvent, (_, args) =>
        {
            var session = (Session)args[0]!;
            if (handles.Remove(session.Id, out var handle))
            {
                handle.Flush();
                handle.Close();
            }
            return new ValueTask<object?>();
        });
    }
}

public static class AnonymousUserId
{
    public static string Resolve(HarnessHome home)
    {
        var path = Path.Combine(home.Root, ".anonymous-user-id");
        if (File.Exists(path))
        {
            var existing = File.ReadAllText(path).Trim();
            if (Guid.TryParse(existing, out _))
                return existing;
        }
        var id = Guid.NewGuid().ToString();
        File.WriteAllText(path, id + '\n');
        return id;
    }
}
