using Cordis;
using Dsh.Core;
using Dsh.Interaction;
using Dsh.Llm;
using Dsh.Llm.DeepSeek;
using Dsh.Persistence;

namespace Dsh.Boot;

public sealed record HarnessOptions(
    HarnessHome Home,
    string? Cwd = null,
    string? Provider = null,
    string? Model = null,
    string? BaseUrl = null,
    string? ApiKeyEnv = null);

public sealed class HarnessApp : IDisposable
{
    public required Context Ctx { get; init; }
    public required HarnessHome Home { get; init; }
    public required ICredentials Credentials { get; init; }
    public required JsonlSessionPersistence Persistence { get; init; }
    public required string Provider { get; init; }
    public required string Model { get; init; }

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

        var persistence = new JsonlSessionPersistence(options.Home.SessionsPath);

        var sessions = new SessionStore(ctx);
        var systemPrompt = new SystemPrompt(ctx, new SystemPromptConfig());
        var tools = new ToolRuntime(ctx);
        var llm = new LlmRuntime(ctx);
        var agents = new AgentRegistry(ctx);
        var agentLoop = new AgentLoop(ctx, new AgentLoopConfig(), _ => persistence);
        var approval = ApprovalService.Register(ctx);
        var questions = UserQuestionService.Register(ctx);
        var commands = CommandsService.Register(ctx);

        var registration = RegisterDeepSeekAdapter(ctx, options, credentials, llm);

        var app = new HarnessApp
        {
            Ctx = ctx,
            Home = options.Home,
            Credentials = credentials,
            Persistence = persistence,
            Provider = options.Provider ?? DefaultProvider,
            Model = options.Model ?? DefaultModel,
        };
        app.Track(registration);
        WirePersistence(ctx, persistence);
        return app;
    }

    internal static AdapterRegistrationHandle RegisterDeepSeekAdapter(Context ctx, HarnessOptions options, ICredentials credentials, LlmRuntime llm)
    {
        var connection = new DeepSeekConnectionOptions(
            options.BaseUrl ?? Environment.GetEnvironmentVariable("DEEPSEEK_BASE_URL") ?? DefaultBaseUrl,
            options.ApiKeyEnv ?? DefaultApiKeyEnv,
            new RequestDefaults(),
            DeepSeekConnectionOptions.DefaultMaxTokens,
            DeepSeekConnectionOptions.DefaultContextWindowValue,
            Catalog,
            DeepSeekConnectionOptions.DefaultStreamIdleTimeoutMs,
            ResolvedRetryPolicy.Resolve(null, "llm-deepseek"));
        var adapter = new DeepSeekAdapter(DefaultProvider, new DeepSeekAdapterOptions
        {
            Options = () => connection,
            ResolveApiKey = (conn, _) =>
            {
                var raw = credentials.Get(conn.ApiKeyEnv)
                    ?? throw new LlmException(new LlmFailure(
                        $"DeepSeek credential \"{conn.ApiKeyEnv}\" is not configured",
                        LlmFailureCodes.MissingCredential));
                if (!ApiKey.Normalize(raw, out var key, out var rejection))
                {
                    throw new LlmException(new LlmFailure(
                        $"DeepSeek credential \"{conn.ApiKeyEnv}\" is unusable: {rejection}",
                        LlmFailureCodes.InvalidCredential));
                }
                return Task.FromResult(key);
            },
            ResolveUserId = () => AnonymousUserId.Resolve(options.Home),
        });
        return llm.RegisterAdapter([DefaultProvider], adapter);
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
