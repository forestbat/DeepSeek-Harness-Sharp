using System.Text.Json;
using Cordis;
using Dsh.Core;
using Dsh.Llm;

namespace Dsh.Sdk;

public sealed class HarnessSdkServer
{
    private sealed record SessionRecord(AgentHandle Handle, IAgent Agent);

    private readonly Context _ctx;
    private readonly List<Func<bool>> _disposers = [];
    private readonly Dictionary<string, SessionRecord> _sessions = [];
    private readonly Dictionary<string, Task<SessionRecord>> _sessionCreations = [];
    private bool _initialized;
    private bool _shuttingDown;
    private string _cwd = "";
    private string _provider = "deepseek-official";
    private string _model = "deepseek-v4-flash";
    private string? _reasoningEffort;
    private int? _maxTokens;

    public HarnessSdkServer(Context ctx, IJsonRpcPeer transport)
    {
        _ctx = ctx;
        _disposers.Add(ctx.On(SessionStore.EventEvent, (_, args) =>
        {
            var session = (Session)args[0]!;
            var sessionEvent = (SessionEvent)args[1]!;
            transport.Notify(SdkMethods.SessionEvent, new SessionEventNotification(session.Id.Value, sessionEvent));
            return new ValueTask<object?>();
        }, new EventOptions { Global = true }));
        _disposers.Add(ctx.On(AgentEventNames.Status, (_, args) =>
        {
            var payload = args[0]!;
            if (payload.GetType().GetProperty("Agent")?.GetValue(payload) is not IAgent agent)
                return new ValueTask<object?>();
            var status = payload.GetType().GetProperty("Status")?.GetValue(payload) is AgentStatus statusValue
                ? statusValue
                : AgentStatus.Idle;
            transport.Notify(SdkMethods.SessionStatus, new SessionStatusNotification(agent.Id.Value, status.ToString().ToLowerInvariant()));
            return new ValueTask<object?>();
        }, new EventOptions { Global = true }));
        _disposers.Add(ctx.On(SessionStore.CreatedEvent, (_, args) =>
        {
            var session = (Session)args[0]!;
            if (session.Header.ParentSession is { } parentSession)
            {
                transport.Notify(SdkMethods.SubagentStarted, new SubagentStartedNotification(parentSession.Value, session.Id.Value));
            }
            return new ValueTask<object?>();
        }, new EventOptions { Global = true }));
    }

    public Task<InitializeResult> InitializeAsync(InitializeParams parameters)
    {
        if (parameters.ReasoningEffort is { Length: 0 })
            throw new ArgumentException("initialize reasoningEffort must be a non-empty string");
        if (parameters.MaxTokens is <= 0)
            throw new ArgumentException("initialize maxTokens must be a positive safe integer");

        var llm = _ctx.Get<LlmRuntime>(LlmRuntime.ServiceName)
            ?? throw new InvalidOperationException("SDK server requires the llm service");
        if (llm.ListProviders().All(provider => provider.Id != parameters.Provider))
            throw new InvalidOperationException($"no adapter registered for provider \"{parameters.Provider}\"");
        llm.ResolveModelInfo(parameters.Provider, parameters.Model);

        _cwd = Path.GetFullPath(parameters.Cwd);
        _provider = parameters.Provider;
        _model = parameters.Model;
        _reasoningEffort = parameters.ReasoningEffort;
        _maxTokens = parameters.MaxTokens;
        _initialized = true;
        return Task.FromResult(new InitializeResult(new ServerInfo("deepseek-harness-sdk-runtime", "0.0.1")));
    }

    public async Task<SessionPromptResult> PromptAsync(SessionPromptParams parameters)
    {
        if (!_initialized)
            throw new InvalidOperationException("SDK server is not initialized");
        var record = await GetOrCreateSessionAsync(parameters.SessionId);
        AssertLiveAgent(record, parameters.SessionId);
        var contentBlocks = DeserializeContentBlocks(parameters.ContentBlocks);
        AssertLiveAgent(record, parameters.SessionId);
        var message = MessageFactory.CreateUserMessage(contentBlocks);
        record.Agent.Followup(message);
        return new SessionPromptResult(message.Id.Value);
    }

    public async Task<Dictionary<string, object?>> ShutdownAsync()
    {
        _shuttingDown = true;
        foreach (var pending in _sessionCreations.Values.ToList())
        {
            try
            {
                await pending;
            }
            catch (Exception error)
            {
                _ctx.Logger.Warn($"SDK shutdown ignored pending session creation failure: {error.Message}");
            }
        }
        _sessionCreations.Clear();
        foreach (var disposer in _disposers)
            disposer();
        _disposers.Clear();
        var sessions = _ctx.Get<SessionStore>(SessionStore.ServiceName);
        foreach (var record in _sessions.Values)
        {
            record.Agent.Cancel(new AgentCancelCause.Disposed());
            if (sessions is not null)
                await sessions.Flush(record.Agent.Session);
            record.Handle.Dispose.Dispose();
        }
        _sessions.Clear();
        return [];
    }

    public async Task<object?> HandleRequestAsync(string method, JsonElement? parameters)
    {
        switch (method)
        {
            case SdkMethods.Initialize:
                return await InitializeAsync(Deserialize<InitializeParams>(parameters));
            case SdkMethods.SessionPrompt:
                return await PromptAsync(Deserialize<SessionPromptParams>(parameters));
            case SdkMethods.Shutdown:
                return await ShutdownAsync();
            case SdkMethods.CordisServiceCall:
                return await CordisServiceCallAsync(Deserialize<CordisServiceCallParams>(parameters));
            case SdkMethods.CordisEventEmit:
                CordisEventEmit(Deserialize<CordisEventParams>(parameters));
                return null;
            case SdkMethods.CordisEventSerial:
                return await CordisEventSerialAsync(Deserialize<CordisEventParams>(parameters));
            default:
                throw new InvalidOperationException($"unknown DeepSeek Harness SDK runtime method: {method}");
        }
    }

    public async Task<object?> CordisServiceCallAsync(CordisServiceCallParams parameters)
    {
        var service = _ctx.Get(parameters.Service, strict: false)
            ?? throw new InvalidOperationException($"no cordis service named \"{parameters.Service}\"");
        var method = service.GetType()
            .GetMethods()
            .Where(candidate => string.Equals(candidate.Name, parameters.Method, StringComparison.OrdinalIgnoreCase))
            .OrderBy(candidate => candidate.GetParameters().Length)
            .FirstOrDefault()
            ?? throw new InvalidOperationException($"service \"{parameters.Service}\" has no method \"{parameters.Method}\"");
        var arguments = ConvertArguments(method.GetParameters(), parameters.Args);
        var result = method.Invoke(service, arguments);
        result = await AwaitIfNeeded(result);
        return result is null ? null : JsonSerializer.SerializeToElement(result, DshJson.Options);
    }

    public void CordisEventEmit(CordisEventParams parameters)
        => _ctx.Emit(parameters.Name, parameters.Args?.Select(arg => (object?)arg).ToArray() ?? []);

    public async Task<object?> CordisEventSerialAsync(CordisEventParams parameters)
    {
        var result = await _ctx.Serial(parameters.Name, parameters.Args?.Select(arg => (object?)arg).ToArray() ?? []);
        return result is null ? null : JsonSerializer.SerializeToElement(result, DshJson.Options);
    }

    private static object?[] ConvertArguments(System.Reflection.ParameterInfo[] parameters, JsonElement[]? args)
    {
        var values = new object?[parameters.Length];
        for (var index = 0; index < parameters.Length; index++)
        {
            if (args is not null && index < args.Length)
            {
                values[index] = args[index].Deserialize(parameters[index].ParameterType, DshJson.Options);
            }
            else if (parameters[index].HasDefaultValue)
            {
                values[index] = parameters[index].DefaultValue;
            }
            else
            {
                throw new InvalidOperationException($"missing argument for parameter \"{parameters[index].Name}\"");
            }
        }
        return values;
    }

    private static async Task<object?> AwaitIfNeeded(object? result)
    {
        switch (result)
        {
            case null:
                return null;
            case Task task:
            {
                await task;
                var resultType = task.GetType();
                return resultType.IsGenericType ? resultType.GetProperty("Result")?.GetValue(task) : null;
            }
            default:
            {
                var type = result.GetType();
                if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(ValueTask<>))
                {
                    return await (Task<object?>)type.GetMethod("AsTask")!.Invoke(result, null)!;
                }
                if (result is ValueTask valueTask)
                    await valueTask;
                return result is ValueTask ? null : result;
            }
        }
    }

    private static T Deserialize<T>(JsonElement? element) where T : class
        => element is { } value
            ? value.Deserialize<T>(DshJson.Options) ?? throw new JsonException($"invalid {typeof(T).Name} params")
            : throw new JsonException($"missing {typeof(T).Name} params");

    private static IReadOnlyList<ContentBlock> DeserializeContentBlocks(JsonElement[] elements)
    {
        var blocks = new List<ContentBlock>(elements.Length);
        foreach (var element in elements)
        {
            try
            {
                blocks.Add(element.Deserialize<ContentBlock>(DshJson.Options)
                    ?? throw new JsonException("content block is null"));
            }
            catch (JsonException)
            {
                throw new InvalidOperationException("SDK image prompt requires an attachment store");
            }
        }
        return blocks;
    }

    private async Task<SessionRecord> GetOrCreateSessionAsync(string sessionId)
    {
        if (_shuttingDown)
            throw new InvalidOperationException("SDK server is shutting down");
        if (_sessions.TryGetValue(sessionId, out var existing))
            return existing;
        if (_sessionCreations.TryGetValue(sessionId, out var pending))
            return await pending;
        var creation = CreateSessionAsync(sessionId);
        _sessionCreations[sessionId] = creation;
        try
        {
            var record = await creation;
            _sessions[sessionId] = record;
            return record;
        }
        finally
        {
            _sessionCreations.Remove(sessionId);
        }
    }

    private async Task<SessionRecord> CreateSessionAsync(string sessionId)
    {
        var agents = _ctx.Get<AgentRegistry>(AgentRegistry.ServiceName)
            ?? throw new InvalidOperationException("SDK server requires the agents service");
        var handle = await agents.Create(new CreateAgentOptions(
            SessionId.Create(sessionId),
            _cwd,
            new AgentOptions(_provider, _model, _reasoningEffort is null ? null : ReasoningEffortId.Create(_reasoningEffort), _maxTokens)));
        return new SessionRecord(handle, handle.Agent);
    }

    private void AssertLiveAgent(SessionRecord record, string sessionId)
    {
        var live = _ctx.Get<AgentRegistry>(AgentRegistry.ServiceName)?.Get(record.Agent.Id);
        if (live is null || !ReferenceEquals(live, record.Agent))
            throw new InvalidOperationException($"session agent was disposed outside the server: {sessionId}");
    }
}
