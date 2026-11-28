using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using A2A;
using Dsh.Boot;
using Dsh.Runtime;
using A2AMessage = A2A.Message;
using A2APart = A2A.Part;
using A2ARole = A2A.Role;

namespace Dsh.A2A;

public sealed record A2aTaskView(string Id, string ContextId, string State, string? Answer, string? Error);

public sealed record A2aTaskResult(bool Ok, A2aTaskView? Task, string? Answer, string? Error);

public sealed record A2aTasksResult(bool Ok, IReadOnlyList<A2aTaskView> Tasks, string? Error);

public sealed record A2aCardResult(
    bool Ok,
    string Name,
    string Description,
    string Version,
    bool Streaming,
    IReadOnlyList<string> Interfaces,
    IReadOnlyList<string> Skills,
    string? Error);

/**
 * A2A Client: 用官方 SDK(A2ACardResolver/A2AClient) 调用远端 A2A Server。
 * 支持 TTL 缓存 AgentCard 与可选 contextId 多轮续接。
 */
public sealed class A2aRemoteClient(
    IReadOnlyDictionary<string, A2aRemoteSettings> remotes,
    OutboundProxy? proxy = null,
    TimeSpan? cardCacheTtl = null,
    TimeProvider? clock = null)
{
    private const int PollIntervalMs = 300;
    private const string ProtocolVersionHeader = "A2A-Version";
    private static readonly TimeSpan DefaultCardCacheTtl = TimeSpan.FromMinutes(5);

    private sealed record CachedCard(AgentCard Card, DateTimeOffset ExpiresAt);

    private readonly IReadOnlyDictionary<string, A2aRemoteSettings> _remotes = remotes;
    private readonly OutboundProxy _proxy = proxy ?? new OutboundProxy();
    private readonly TimeSpan _cardCacheTtl = cardCacheTtl ?? DefaultCardCacheTtl;
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly ConcurrentDictionary<string, CachedCard> _cardCache = new(StringComparer.Ordinal);

    public async Task<A2aCardResult> CardAsync(string target, CancellationToken cancellationToken)
    {
        var (baseUri, headers) = Resolve(target);
        var card = await GetOrFetchCardAsync(baseUri, headers, cancellationToken).ConfigureAwait(false);
        return new A2aCardResult(
            true,
            card.Name,
            card.Description,
            card.Version,
            card.Capabilities.Streaming ?? false,
            [.. card.SupportedInterfaces.Select(entry => $"{entry.ProtocolBinding} {entry.Url}")],
            [.. card.Skills.Select(skill => $"{skill.Id}: {skill.Name}")],
            null);
    }

    public Task<A2aTaskResult> SendAsync(string target, string message, bool wait, int timeoutSeconds, CancellationToken cancellationToken)
        => SendAsync(target, message, wait, timeoutSeconds, contextId: null, cancellationToken);

    public async Task<A2aTaskResult> SendAsync(
        string target,
        string message,
        bool wait,
        int timeoutSeconds,
        string? contextId,
        CancellationToken cancellationToken = default)
    {
        var (_, client, http) = await ConnectAsync(target, cancellationToken).ConfigureAwait(false);
        using (http)
        using (client)
        {
            var request = new SendMessageRequest
            {
                Message = new A2AMessage
                {
                    MessageId = Guid.NewGuid().ToString("N"),
                    ContextId = string.IsNullOrWhiteSpace(contextId) ? null : contextId,
                    Role = A2ARole.User,
                    Parts = [A2APart.FromText(message)],
                },
            };
            if (!wait)
                request.Configuration = new SendMessageConfiguration { ReturnImmediately = true };
            var response = await client.SendMessageAsync(request, cancellationToken).ConfigureAwait(false);
            if (response.Task is not { } task)
                return new A2aTaskResult(true, null, response.Message is { } direct ? Join(direct.Parts) : null, null);
            if (wait)
                task = await PollAsync(client, task, timeoutSeconds, cancellationToken).ConfigureAwait(false);
            return new A2aTaskResult(true, View(task), null, null);
        }
    }

    public async Task<A2aTaskResult> GetAsync(string target, string taskId, CancellationToken cancellationToken)
    {
        var (_, client, http) = await ConnectAsync(target, cancellationToken).ConfigureAwait(false);
        using (http)
        using (client)
        {
            var task = await client.GetTaskAsync(new GetTaskRequest { Id = taskId }, cancellationToken).ConfigureAwait(false);
            return new A2aTaskResult(true, View(task), null, null);
        }
    }

    public async Task<A2aTasksResult> ListAsync(string target, string? contextId, int pageSize, CancellationToken cancellationToken)
    {
        var (_, client, http) = await ConnectAsync(target, cancellationToken).ConfigureAwait(false);
        using (http)
        using (client)
        {
            var response = await client.ListTasksAsync(
                new ListTasksRequest { ContextId = contextId, PageSize = pageSize },
                cancellationToken).ConfigureAwait(false);
            return new A2aTasksResult(true, [.. response.Tasks.Select(task => View(task))], null);
        }
    }

    public async Task<A2aTaskResult> CancelAsync(string target, string taskId, CancellationToken cancellationToken)
    {
        var (_, client, http) = await ConnectAsync(target, cancellationToken).ConfigureAwait(false);
        using (http)
        using (client)
        {
            var task = await client.CancelTaskAsync(new CancelTaskRequest { Id = taskId }, cancellationToken).ConfigureAwait(false);
            return new A2aTaskResult(true, View(task), null, null);
        }
    }

    public void InvalidateCardCache(string? target = null)
    {
        if (target is null)
        {
            _cardCache.Clear();
            return;
        }
        var (baseUri, _) = Resolve(target);
        _cardCache.TryRemove(baseUri.AbsoluteUri, out _);
    }

    private async Task<(AgentCard Card, A2AClient Client, HttpClient Http)> ConnectAsync(string target, CancellationToken cancellationToken)
    {
        var (baseUri, headers) = Resolve(target);
        var card = await GetOrFetchCardAsync(baseUri, headers, cancellationToken).ConfigureAwait(false);
        // 协议版本从 card 自动取(如 qwen 强制要求 A2A-Version: 1.0), 用户不必手填; 显式配置则优先。
        var rpcHeaders = new Dictionary<string, string>(headers, StringComparer.OrdinalIgnoreCase);
        if (!rpcHeaders.ContainsKey(ProtocolVersionHeader)
            && card.SupportedInterfaces.FirstOrDefault()?.ProtocolVersion is { Length: > 0 } version)
            rpcHeaders[ProtocolVersionHeader] = version;
        var http = new HttpClient(_proxy.CreateHandler()) { Timeout = Timeout.InfiniteTimeSpan };
        Apply(http, rpcHeaders);
        var rpc = card.SupportedInterfaces.FirstOrDefault()?.Url;
        var rpcUri = string.IsNullOrWhiteSpace(rpc) ? baseUri : new Uri(rpc);
        return (card, new A2AClient(rpcUri, http), http);
    }

    private async Task<AgentCard> GetOrFetchCardAsync(
        Uri baseUri,
        IReadOnlyDictionary<string, string> headers,
        CancellationToken cancellationToken)
    {
        var cacheKey = baseUri.AbsoluteUri;
        var now = _clock.GetUtcNow();
        if (_cardCacheTtl > TimeSpan.Zero
            && _cardCache.TryGetValue(cacheKey, out var cached)
            && cached.ExpiresAt > now)
        {
            return cached.Card;
        }

        using var cardHttp = new HttpClient(_proxy.CreateHandler()) { Timeout = Timeout.InfiniteTimeSpan };
        Apply(cardHttp, headers);
        var card = await FetchCardAsync(baseUri, cardHttp, cancellationToken).ConfigureAwait(false);
        if (_cardCacheTtl > TimeSpan.Zero)
            _cardCache[cacheKey] = new CachedCard(card, _clock.GetUtcNow().Add(_cardCacheTtl));
        return card;
    }

    private static void Apply(HttpClient http, IReadOnlyDictionary<string, string> headers)
    {
        foreach (var (name, value) in headers)
            http.DefaultRequestHeaders.TryAddWithoutValidation(name, value);
    }

    /**
     * 自行抓取并解析 agent card, 而不是用 A2ACardResolver: proto3 JSON 会省略空的 repeated 字段,
     * 而 C# SDK 把 skills 等当作必需字段(如 qwen 的 card 不带 skills 时解析直接失败)。这里补齐缺省的空集合。
     */
    private static async Task<AgentCard> FetchCardAsync(Uri baseUri, HttpClient http, CancellationToken cancellationToken)
    {
        var cardUri = new Uri($"{baseUri.ToString().TrimEnd('/')}/.well-known/agent-card.json");
        var json = await http.GetStringAsync(cardUri, cancellationToken).ConfigureAwait(false);
        var node = JsonNode.Parse(json) as JsonObject
            ?? throw new InvalidOperationException($"agent card at {cardUri} is not a JSON object");
        foreach (var field in new[] { "supportedInterfaces", "skills", "defaultInputModes", "defaultOutputModes", "signatures" })
            node.TryAdd(field, new JsonArray());
        node.TryAdd("capabilities", new JsonObject());
        return JsonSerializer.Deserialize<AgentCard>(node.ToJsonString(), A2AJsonUtilities.DefaultOptions)
            ?? throw new InvalidOperationException($"agent card at {cardUri} could not be parsed");
    }

    private (Uri Base, Dictionary<string, string> Headers) Resolve(string target)
    {
        if (_remotes.TryGetValue(target, out var remote) && remote.Url is { Length: > 0 } url)
        {
            var (baseUri, headers) = Parse(url);
            foreach (var (name, value) in remote.Headers)
                headers[name] = value;
            if (remote.Token is { Length: > 0 } token && !headers.ContainsKey("authorization"))
                headers["authorization"] = $"Bearer {token}";
            return (baseUri, headers);
        }
        if (Uri.TryCreate(target, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
            return Parse(target);
        throw new InvalidOperationException(
            $"a2a target \"{target}\" is neither a configured remote nor an absolute http(s) URL"
            + (_remotes.Count > 0 ? $" (configured: {string.Join(", ", _remotes.Keys)})" : ""));
    }

    /**
     * 一个目标就是一条 URL: 查询串里的 token 变成 Authorization: Bearer, 其余键值变成请求头。
     * 于是任何 A2A server 的连接信息都能压成一行字符串(便于粘贴/脚本产出), 不必展开成 YAML 的头映射。
     */
    private static (Uri Base, Dictionary<string, string> Headers) Parse(string raw)
    {
        var uri = new Uri(raw);
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var query = uri.Query.StartsWith('?') ? uri.Query[1..] : uri.Query;
        foreach (var pair in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = pair.IndexOf('=');
            var name = Uri.UnescapeDataString(separator < 0 ? pair : pair[..separator]);
            var value = separator < 0 ? "" : Uri.UnescapeDataString(pair[(separator + 1)..]);
            if (name.Length == 0)
                continue;
            if (string.Equals(name, "token", StringComparison.OrdinalIgnoreCase))
                headers["authorization"] = $"Bearer {value}";
            else
                headers[name] = value;
        }
        var baseUri = new UriBuilder(uri) { Query = "" }.Uri;
        return (baseUri, headers);
    }

    private static async Task<AgentTask> PollAsync(A2AClient client, AgentTask task, int timeoutSeconds, CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(timeoutSeconds);
        while (!IsTerminal(task.Status.State) && DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(PollIntervalMs, cancellationToken).ConfigureAwait(false);
            task = await client.GetTaskAsync(new GetTaskRequest { Id = task.Id }, cancellationToken).ConfigureAwait(false);
        }
        return task;
    }

    private static bool IsTerminal(TaskState state)
        => state is TaskState.Completed or TaskState.Failed or TaskState.Canceled or TaskState.Rejected;

    private static A2aTaskView View(AgentTask task)
    {
        var text = ExtractAnswer(task);
        return task.Status.State is TaskState.Failed or TaskState.Rejected
            ? new A2aTaskView(task.Id, task.ContextId, task.Status.State.ToString(), null, text)
            : new A2aTaskView(task.Id, task.ContextId, task.Status.State.ToString(), text, null);
    }

    private static string? ExtractAnswer(AgentTask task)
    {
        if (task.Status.Message is { Parts.Count: > 0 } message)
        {
            var fromStatus = Join(message.Parts);
            if (fromStatus.Length > 0)
                return fromStatus;
        }
        if (task.Artifacts is { Count: > 0 } artifacts)
        {
            var fromArtifacts = string.Concat(artifacts.SelectMany(artifact => Join(artifact.Parts)));
            if (fromArtifacts.Length > 0)
                return fromArtifacts;
        }
        return null;
    }

    private static string Join(IEnumerable<A2APart> parts)
        => string.Concat(parts.Select(part => part.Text).Where(text => !string.IsNullOrEmpty(text)));
}
