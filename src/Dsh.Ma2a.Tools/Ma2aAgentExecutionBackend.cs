using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Dsh.Core;
using Dsh.Llm;
using Dsh.Ma2a.Services;

namespace Dsh.Ma2a.Tools;

public sealed class Ma2aAgentExecutionBackend : IAgentExecutionBackend
{
    public const string TargetPrefix = "ma2a:";
    private const string LocalEndpointAlias = "local";
    private const int PollIntervalMs = 250;
    private static readonly TimeSpan DefaultTaskTimeout = TimeSpan.FromSeconds(120);

    private readonly string _stateDirectory;
    private readonly Func<string, Ma2aClient> _clientFactory;
    private readonly ConcurrentDictionary<AgentId, Ma2aExecutionHandle> _handles = new();

    public Ma2aAgentExecutionBackend(string? stateDirectory = null, Func<string, Ma2aClient>? clientFactory = null)
    {
        _stateDirectory = string.IsNullOrWhiteSpace(stateDirectory)
            ? DefaultStateDirectory()
            : Path.GetFullPath(stateDirectory);
        _clientFactory = clientFactory ?? (directory => new Ma2aClient(directory));
    }

    public AgentBackendKind Kind => AgentBackendKind.Ma2a;

    public async Task<IReadOnlyList<RemoteExecutionTarget>> ListTargetsAsync(CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(_stateDirectory))
            return [];
        var client = _clientFactory(_stateDirectory);
        JsonElement snapshot;
        try
        {
            snapshot = await client.GetSnapshotAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception error) when (IsTransientUnavailable(error))
        {
            return [];
        }

        var payload = snapshot.GetProperty("result").GetProperty("payload");
        var localEndpointId = ReadEndpointId(payload);
        var targets = new List<RemoteExecutionTarget>();

        await AppendLocalAgentsAsync(client, localEndpointId, targets, cancellationToken).ConfigureAwait(false);
        foreach (var peerEndpointId in CollectPeerEndpoints(payload, localEndpointId))
            await AppendRemoteAgentsAsync(client, peerEndpointId, targets, cancellationToken).ConfigureAwait(false);

        return targets;
    }

    public bool CanHandle(AgentDispatchRequest request)
    {
        if (request.TargetId is { Length: > 0 } targetId)
            return targetId.StartsWith(TargetPrefix, StringComparison.OrdinalIgnoreCase);
        return request.PreferredBackend == AgentBackendKind.Ma2a;
    }

    public async Task<IAgentExecutionHandle> DispatchAsync(
        AgentDispatchRequest request,
        CancellationToken cancellationToken = default)
    {
        var (endpointId, agentName, targetId) = await ResolveTargetAsync(request, cancellationToken).ConfigureAwait(false);
        var client = _clientFactory(_stateDirectory);
        var localEndpointId = await TryReadLocalEndpointIdAsync(client, cancellationToken).ConfigureAwait(false);
        var isLocal = IsLocalEndpoint(endpointId, localEndpointId);
        var prompt = request.Prompt ?? "";
        var fromAgent = request.Sender?.Id;

        var taskId = request.SessionId?.Value;
        if (string.IsNullOrEmpty(taskId))
        {
            taskId = await SubmitTaskAsync(client, endpointId, agentName, prompt, fromAgent, isLocal, cancellationToken)
                .ConfigureAwait(false);
        }
        else if (prompt.Length > 0)
        {
            await SendTaskMessageAsync(client, endpointId, agentName, taskId, prompt, isLocal, cancellationToken)
                .ConfigureAwait(false);
        }

        var agentId = request.AgentId ?? AgentId.Create(taskId);
        var handle = new Ma2aExecutionHandle(
            client,
            endpointId,
            agentName,
            taskId,
            agentId,
            targetId,
            isLocal);
        _handles[agentId] = handle;

        if (request.WaitForCompletion && prompt.Length > 0)
            await handle.WaitForCompletionAsync(cancellationToken).ConfigureAwait(false);

        return handle;
    }

    public async Task<IAgentExecutionHandle?> AttachAsync(
        AgentId agentId,
        string? targetId = null,
        CancellationToken cancellationToken = default)
    {
        if (_handles.TryGetValue(agentId, out var existing))
            return existing;
        if (string.IsNullOrWhiteSpace(targetId) || !targetId.StartsWith(TargetPrefix, StringComparison.OrdinalIgnoreCase))
            return null;
        var (endpointId, agentName) = ParseTargetId(targetId);
        var client = _clientFactory(_stateDirectory);
        var localEndpointId = await TryReadLocalEndpointIdAsync(client, cancellationToken).ConfigureAwait(false);
        var isLocal = IsLocalEndpoint(endpointId, localEndpointId);
        var taskId = agentId.Value;
        var handle = new Ma2aExecutionHandle(client, endpointId, agentName, taskId, agentId, targetId, isLocal);
        try
        {
            _ = await handle.GetStatusAsync(cancellationToken).ConfigureAwait(false);
            _handles[agentId] = handle;
            return handle;
        }
        catch (Exception error) when (IsTransientUnavailable(error))
        {
            return null;
        }
    }

    internal static (string EndpointId, string AgentName) ParseTargetId(string targetId)
    {
        if (!targetId.StartsWith(TargetPrefix, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException($"Invalid MA2A target id \"{targetId}\".", nameof(targetId));
        var remainder = targetId[TargetPrefix.Length..];
        var separator = remainder.IndexOf(':');
        if (separator < 0)
        {
            if (remainder.Length == 0)
                throw new ArgumentException("MA2A target id must specify an agent name.", nameof(targetId));
            return (LocalEndpointAlias, remainder);
        }
        var endpoint = remainder[..separator];
        var agent = remainder[(separator + 1)..];
        if (endpoint.Length == 0 || agent.Length == 0)
            throw new ArgumentException($"Invalid MA2A target id \"{targetId}\".", nameof(targetId));
        return (endpoint, agent);
    }

    private async Task<(string EndpointId, string AgentName, string TargetId)> ResolveTargetAsync(
        AgentDispatchRequest request,
        CancellationToken cancellationToken)
    {
        if (request.TargetId is { Length: > 0 } rawTarget)
        {
            var (endpoint, agent) = ParseTargetId(rawTarget);
            return (endpoint, agent, rawTarget);
        }
        var targets = await ListTargetsAsync(cancellationToken).ConfigureAwait(false);
        var first = targets.FirstOrDefault()
            ?? throw new InvalidOperationException("No MA2A agents are currently reachable.");
        var (resolvedEndpoint, resolvedAgent) = ParseTargetId(first.TargetId);
        return (resolvedEndpoint, resolvedAgent, first.TargetId);
    }

    private static async Task AppendLocalAgentsAsync(
        Ma2aClient client,
        string? localEndpointId,
        List<RemoteExecutionTarget> targets,
        CancellationToken cancellationToken)
    {
        try
        {
            var response = await client.CallAsync(
                new JsonObject { ["operation"] = "agent_list", ["after"] = null },
                cancellationToken).ConfigureAwait(false);
            var payload = response.GetProperty("result").GetProperty("payload");
            var endpointSegment = localEndpointId ?? LocalEndpointAlias;
            foreach (var entry in EnumerateAgentEntries(payload))
            {
                targets.Add(new RemoteExecutionTarget(
                    TargetId: $"{TargetPrefix}{endpointSegment}:{entry.Name}",
                    Kind: AgentBackendKind.Ma2a,
                    DisplayName: entry.Name,
                    Endpoint: endpointSegment,
                    Available: entry.Available,
                    Capabilities: ["ma2a", "agent"],
                    Metadata: entry.Description is { Length: > 0 } desc
                        ? new Dictionary<string, string> { ["scope"] = "local", ["description"] = desc }
                        : new Dictionary<string, string> { ["scope"] = "local" }));
            }
        }
        catch (Exception error) when (IsTransientUnavailable(error))
        {
        }
    }

    private static async Task AppendRemoteAgentsAsync(
        Ma2aClient client,
        string peerEndpointId,
        List<RemoteExecutionTarget> targets,
        CancellationToken cancellationToken)
    {
        try
        {
            var result = await CallRemoteAgentServiceAsync(
                client, peerEndpointId, Ma2aServiceRequests.Agents(), cancellationToken).ConfigureAwait(false);
            foreach (var entry in EnumerateCborAgentEntries(result))
            {
                targets.Add(new RemoteExecutionTarget(
                    TargetId: $"{TargetPrefix}{peerEndpointId}:{entry.Name}",
                    Kind: AgentBackendKind.Ma2a,
                    DisplayName: $"{entry.Name}@{peerEndpointId[..Math.Min(8, peerEndpointId.Length)]}",
                    Endpoint: peerEndpointId,
                    Available: entry.Available,
                    Capabilities: ["ma2a", "agent"],
                    Metadata: entry.Description is { Length: > 0 } desc
                        ? new Dictionary<string, string> { ["scope"] = "remote", ["description"] = desc }
                        : new Dictionary<string, string> { ["scope"] = "remote" }));
            }
        }
        catch (Exception error) when (IsTransientUnavailable(error))
        {
        }
    }

    private static IReadOnlyList<string> CollectPeerEndpoints(JsonElement payload, string? localEndpointId)
    {
        var peers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (payload.TryGetProperty("connections", out var connections) && connections.ValueKind == JsonValueKind.Array)
        {
            foreach (var connection in connections.EnumerateArray())
            {
                var id = ReadFirstString(connection, "peer_endpoint_id", "endpoint_id", "endpoint");
                if (IsRemoteEndpointId(id, localEndpointId))
                    peers.Add(id!);
            }
        }
        if (payload.TryGetProperty("control_sync", out var controlSync)
            && controlSync.ValueKind == JsonValueKind.Object
            && controlSync.TryGetProperty("peer_endpoint_ids", out var peerIds)
            && peerIds.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in peerIds.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.String && IsRemoteEndpointId(item.GetString(), localEndpointId))
                    peers.Add(item.GetString()!);
            }
        }
        return [.. peers];
    }

    private static bool IsRemoteEndpointId(string? candidate, string? localEndpointId)
        => candidate is { Length: 64 }
            && !string.Equals(candidate, localEndpointId, StringComparison.OrdinalIgnoreCase);

    private static bool IsLocalEndpoint(string endpointId, string? localEndpointId)
        => string.Equals(endpointId, LocalEndpointAlias, StringComparison.OrdinalIgnoreCase)
            || (localEndpointId is not null && string.Equals(endpointId, localEndpointId, StringComparison.OrdinalIgnoreCase));

    private static async Task<string?> TryReadLocalEndpointIdAsync(Ma2aClient client, CancellationToken cancellationToken)
    {
        try
        {
            var response = await client.CallAsync(
                new JsonObject { ["operation"] = "endpoint_info" },
                cancellationToken).ConfigureAwait(false);
            var payload = response.GetProperty("result").GetProperty("payload");
            return ReadFirstString(payload, "endpoint_id", "endpoint");
        }
        catch (Exception error) when (IsTransientUnavailable(error))
        {
            return null;
        }
    }

    private static async Task<string> SubmitTaskAsync(
        Ma2aClient client,
        string endpointId,
        string agentName,
        string prompt,
        string? fromAgent,
        bool isLocal,
        CancellationToken cancellationToken)
    {
        if (isLocal)
        {
            var response = await client.CallAsync(new JsonObject
            {
                ["operation"] = "agent_task_submit",
                ["request_id"] = NewHexId(),
                ["agent"] = agentName,
                ["message"] = BuildLocalAgentMessage(prompt),
            }, cancellationToken).ConfigureAwait(false);
            var payload = response.GetProperty("result").GetProperty("payload");
            return ExtractTaskId(payload)
                ?? throw new InvalidOperationException("MA2A agent_task_submit did not return a task_id.");
        }

        var result = await CallRemoteAgentServiceAsync(
            client,
            endpointId,
            Ma2aServiceRequests.AgentSubmit(agentName, prompt, fromAgent),
            cancellationToken).ConfigureAwait(false);
        return ExtractTaskId(result)
            ?? throw new InvalidOperationException("Remote MA2A agent submit did not return a task_id.");
    }

    private static async Task SendTaskMessageAsync(
        Ma2aClient client,
        string endpointId,
        string agentName,
        string taskId,
        string message,
        bool isLocal,
        CancellationToken cancellationToken)
    {
        if (isLocal)
        {
            await client.CallAsync(new JsonObject
            {
                ["operation"] = "agent_task_send",
                ["request_id"] = NewHexId(),
                ["task_id"] = taskId,
                ["message"] = BuildLocalAgentMessage(message),
            }, cancellationToken).ConfigureAwait(false);
            return;
        }

        _ = await CallRemoteAgentServiceAsync(
            client,
            endpointId,
            Ma2aServiceRequests.AgentSend(agentName, taskId, message),
            cancellationToken).ConfigureAwait(false);
    }

    private static async Task<JsonNode?> CallRemoteAgentServiceAsync(
        Ma2aClient client,
        string targetEndpointId,
        JsonArray body,
        CancellationToken cancellationToken)
    {
        await using var session = await Ma2aServiceSession.OpenAsync(
            client, targetEndpointId, "agent", NewHexId(), body, cancellationToken).ConfigureAwait(false);
        while (true)
        {
            var frame = await session.ReadAsync(cancellationToken).ConfigureAwait(false);
            if (frame.Kind != Ma2aFrameKind.Outcome)
                continue;
            var outcome = frame.DecodeCbor() as JsonObject;
            return outcome?["1"];
        }
    }

    private static JsonObject BuildLocalAgentMessage(string text) => new()
    {
        ["role"] = "user",
        ["parts"] = new JsonArray
        {
            new JsonObject
            {
                ["kind"] = "text",
                ["content"] = text,
            },
        },
    };

    private static string NewHexId() => Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));

    private static string? ReadEndpointId(JsonElement payload)
        => payload.TryGetProperty("endpoint", out var endpoint) && endpoint.ValueKind == JsonValueKind.Object
            ? ReadFirstString(endpoint, "endpoint_id")
            : null;

    private static string? ReadFirstString(JsonElement element, params string[] names)
    {
        if (element.ValueKind != JsonValueKind.Object)
            return null;
        foreach (var name in names)
        {
            if (element.TryGetProperty(name, out var prop) && prop.ValueKind == JsonValueKind.String)
                return prop.GetString();
        }
        return null;
    }

    private static IEnumerable<(string Name, string? Description, bool Available)> EnumerateAgentEntries(JsonElement payload)
    {
        var array = payload.ValueKind == JsonValueKind.Array
            ? payload
            : payload.ValueKind == JsonValueKind.Object && payload.TryGetProperty("agents", out var agents) && agents.ValueKind == JsonValueKind.Array
                ? agents
                : default;
        if (array.ValueKind != JsonValueKind.Array)
            yield break;

        foreach (var item in array.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String && item.GetString() is { Length: > 0 } simpleName)
            {
                yield return (simpleName, null, true);
                continue;
            }
            if (item.ValueKind != JsonValueKind.Object)
                continue;
            var name = ReadFirstString(item, "agent", "name");
            if (string.IsNullOrEmpty(name))
                continue;
            var description = ReadFirstString(item, "description");
            var available = !item.TryGetProperty("running", out var running) || running.ValueKind != JsonValueKind.False;
            yield return (name, description, available);
        }
    }

    private static IEnumerable<(string Name, string? Description, bool Available)> EnumerateCborAgentEntries(JsonNode? node)
    {
        var array = node as JsonArray ?? (node as JsonObject)?["0"] as JsonArray ?? (node as JsonObject)?["agents"] as JsonArray;
        if (array is null)
            yield break;

        foreach (var item in array)
        {
            if (item is JsonValue value && value.TryGetValue<string>(out var simpleName) && simpleName.Length > 0)
            {
                yield return (simpleName, null, true);
                continue;
            }
            if (item is JsonArray tuple && tuple.Count > 0 && tuple[0]?.GetValue<string>() is { Length: > 0 } tupleName)
            {
                var desc = tuple.Count > 1 && tuple[1] is JsonValue d && d.TryGetValue<string>(out var s) ? s : null;
                yield return (tupleName, desc, true);
                continue;
            }
            if (item is JsonObject obj)
            {
                var name = (obj["agent"] ?? obj["name"])?.GetValue<string>();
                if (string.IsNullOrEmpty(name))
                    continue;
                var desc = obj["description"]?.GetValue<string>();
                yield return (name, desc, true);
            }
        }
    }

    private static string? ExtractTaskId(JsonElement payload)
    {
        if (payload.ValueKind == JsonValueKind.String)
            return payload.GetString();
        if (payload.ValueKind != JsonValueKind.Object)
            return null;
        if (ReadFirstString(payload, "task_id", "id") is { Length: > 0 } direct)
            return direct;
        if (payload.TryGetProperty("task", out var task) && task.ValueKind == JsonValueKind.Object)
            return ReadFirstString(task, "task_id", "id");
        return null;
    }

    private static string? ExtractTaskId(JsonNode? node) => node switch
    {
        JsonValue value when value.TryGetValue<string>(out var text) => text,
        JsonObject obj when obj["$bytes"] is JsonValue hex && hex.TryGetValue<string>(out var bytesHex) => bytesHex,
        JsonObject obj => ExtractTaskId(obj["task_id"] ?? obj["id"] ?? obj["task"] ?? obj["0"]),
        JsonArray arr when arr.Count > 0 => ExtractTaskId(arr[0]),
        _ => null,
    };

    private static bool IsTransientUnavailable(Exception error)
        => error is Ma2aProtocolException or Ma2aServiceException or IOException or SocketException or InvalidOperationException;

    private static string DefaultStateDirectory() => OperatingSystem.IsWindows()
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ma2a")
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "state", "ma2a");

    private sealed class Ma2aExecutionHandle(
        Ma2aClient client,
        string endpointId,
        string agentName,
        string taskId,
        AgentId agentId,
        string targetId,
        bool isLocal) : IAgentExecutionHandle
    {
        public AgentId AgentId { get; } = agentId;
        public SessionId? SessionId { get; } = Dsh.Llm.SessionId.Create(taskId);
        public AgentBackendKind BackendKind => AgentBackendKind.Ma2a;
        public string TargetId { get; } = targetId;

        public async Task<AgentExecutionSnapshot> SendAsync(
            string message,
            AgentMessageIntent intent = AgentMessageIntent.Task,
            MessagePrincipal? sender = null,
            CancellationToken cancellationToken = default)
        {
            await SendTaskMessageAsync(client, endpointId, agentName, taskId, message, isLocal, cancellationToken)
                .ConfigureAwait(false);
            return await WaitForCompletionAsync(cancellationToken).ConfigureAwait(false);
        }

        public async Task<AgentExecutionSnapshot> GetStatusAsync(CancellationToken cancellationToken = default)
        {
            if (isLocal)
            {
                var response = await client.CallAsync(new JsonObject
                {
                    ["operation"] = "agent_task_inspect",
                    ["task_id"] = taskId,
                }, cancellationToken).ConfigureAwait(false);
                var payload = response.GetProperty("result").GetProperty("payload");
                return ParseLocalSnapshot(payload);
            }

            var remote = await CallRemoteAgentServiceAsync(
                client,
                endpointId,
                Ma2aServiceRequests.AgentGet(agentName, taskId),
                cancellationToken).ConfigureAwait(false);
            return ParseRemoteSnapshot(remote);
        }

        public async Task CancelAsync(CancellationToken cancellationToken = default)
        {
            if (isLocal)
            {
                await client.CallAsync(new JsonObject
                {
                    ["operation"] = "agent_task_cancel",
                    ["request_id"] = NewHexId(),
                    ["task_id"] = taskId,
                }, cancellationToken).ConfigureAwait(false);
                return;
            }

            _ = await CallRemoteAgentServiceAsync(
                client,
                endpointId,
                Ma2aServiceRequests.AgentCancel(agentName, taskId),
                cancellationToken).ConfigureAwait(false);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        internal async Task<AgentExecutionSnapshot> WaitForCompletionAsync(CancellationToken cancellationToken)
        {
            var deadline = DateTimeOffset.UtcNow.Add(DefaultTaskTimeout);
            while (true)
            {
                var snapshot = await GetStatusAsync(cancellationToken).ConfigureAwait(false);
                if (snapshot.Completed || DateTimeOffset.UtcNow >= deadline)
                    return snapshot;
                await Task.Delay(PollIntervalMs, cancellationToken).ConfigureAwait(false);
            }
        }

        private AgentExecutionSnapshot ParseLocalSnapshot(JsonElement payload)
        {
            var task = payload.ValueKind == JsonValueKind.Object && payload.TryGetProperty("task", out var nested) && nested.ValueKind == JsonValueKind.Object
                ? nested
                : payload;
            var rawState = ReadFirstString(task, "state", "status") ?? "";
            var output = ReadFirstString(task, "output", "text", "answer") ?? ExtractLocalText(task);
            var error = ReadFirstString(task, "error");
            return BuildSnapshot(rawState, output, error);
        }

        private AgentExecutionSnapshot ParseRemoteSnapshot(JsonNode? node)
        {
            var rawState = "";
            string? output = null;
            string? error = null;
            if (node is JsonObject obj)
            {
                rawState = (obj["state"] ?? obj["status"] ?? obj["0"])?.ToString() ?? "";
                output = (obj["output"] ?? obj["text"] ?? obj["answer"])?.ToString();
                error = obj["error"]?.ToString();
            }
            else if (node is JsonArray arr)
            {
                rawState = arr.Count > 1 ? arr[1]?.ToString() ?? "" : "";
                output = arr.Count > 2 ? ExtractCborText(arr[2]) : null;
            }
            return BuildSnapshot(rawState, output, error);
        }

        private AgentExecutionSnapshot BuildSnapshot(string rawState, string? output, string? error)
        {
            var normalized = rawState.ToLowerInvariant();
            var terminal = normalized is "completed" or "failed" or "canceled" or "cancelled" or "rejected";
            var failed = normalized is "failed" or "rejected";
            var state = terminal
                ? normalized is "canceled" or "cancelled" ? AgentLifecycleState.Disposed : AgentLifecycleState.Idle
                : AgentLifecycleState.Active;
            var finalError = error ?? (failed ? output ?? $"MA2A task ended with state {rawState}" : null);
            return new AgentExecutionSnapshot(
                AgentId,
                SessionId,
                AgentBackendKind.Ma2a,
                TargetId,
                state,
                Summary: output ?? finalError,
                Completed: terminal,
                Output: failed ? null : output,
                Error: finalError,
                UpdatedAt: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        }

        private static string? ExtractLocalText(JsonElement task)
        {
            if (task.ValueKind != JsonValueKind.Object)
                return null;
            if (task.TryGetProperty("message", out var msg) && msg.ValueKind == JsonValueKind.Object
                && msg.TryGetProperty("parts", out var parts) && parts.ValueKind == JsonValueKind.Array)
            {
                return string.Concat(parts.EnumerateArray()
                    .Select(part => ReadFirstString(part, "content", "text") ?? ""));
            }
            return null;
        }

        private static string? ExtractCborText(JsonNode? node) => node switch
        {
            JsonValue val when val.TryGetValue<string>(out var text) => text,
            JsonArray arr => string.Concat(arr.Select(ExtractCborText).Where(text => text is not null)),
            JsonObject obj => (obj["content"] ?? obj["text"])?.ToString(),
            _ => null,
        };
    }
}
