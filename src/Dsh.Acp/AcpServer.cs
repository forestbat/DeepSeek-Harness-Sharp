using System.Text.Json;
using Dsh.Runtime;
using Dsh.Runtime.Events;
using Dsh.Core;
using Dsh.Llm;
using Dsh.Sdk;
using Dsh.Transport;
using Dsh.Agent;

namespace Dsh.Acp;

public sealed class AcpServer
{
    private sealed record AcpSessionRecord(AgentHandle Handle, IAgent Agent, IDisposable? Hooks);

    private sealed record NewSessionParams(string Cwd, JsonElement[]? AdditionalDirectories = null);

    private sealed record ResumeSessionParams(string SessionId, string Cwd, JsonElement[]? AdditionalDirectories = null);

    private sealed record CloseSessionParams(string SessionId);

    private sealed record PromptParams(string SessionId, JsonElement[] Prompt);

    private readonly Context _ctx;
    private readonly IJsonRpcPeer _transport;
    private readonly string? _provider;
    private readonly string? _model;
    private readonly Func<Context, IDisposable>? _installAgentHooks;
    private readonly LocalEndpointToken? _requiredToken;
    private readonly Dictionary<string, AcpSessionRecord> _sessions = [];
    private readonly List<Func<bool>> _disposers = [];
    private volatile bool _authenticated;
    private bool _closed;

    /** requiredToken gates every method except initialize/authenticate; null means the transport itself is trusted (e.g. stdio). */
    public AcpServer(Context ctx, IJsonRpcPeer transport, string? provider = null, string? model = null,
        Func<Context, IDisposable>? installAgentHooks = null, LocalEndpointToken? requiredToken = null)
    {
        _ctx = ctx;
        _transport = transport;
        _provider = provider;
        _model = model;
        _installAgentHooks = installAgentHooks;
        _requiredToken = requiredToken;
        _authenticated = requiredToken is null;
        _disposers.Add(ctx.On<Dsh.Core.SessionEventNotification>(notification =>
        {
            var session = notification.Session;
            var sessionEvent = notification.Event;
            if (_sessions.TryGetValue(session.Id.Value, out var record)
                && ReferenceEquals(record.Agent.Session, session))
            {
                EmitSessionUpdate(session, sessionEvent);
            }
        }, new EventOptions { Global = true }));
    }

    public async Task<object?> HandleRequestAsync(string method, JsonElement? parameters)
    {
        if (_closed)
            throw new InvalidOperationException("the ACP bridge has been disposed");
        if (!_authenticated && method is not (AcpMethods.Initialize or AcpMethods.Authenticate))
            throw new InvalidOperationException($"authenticate with \"{AcpMethods.LocalTokenAuthMethod}\" before calling {method}");
        switch (method)
        {
            case AcpMethods.Initialize:
                return InitializeResult();
            case AcpMethods.Authenticate:
                return Authenticate(parameters);
            case AcpMethods.NewSession:
                return await NewSessionAsync(Deserialize<NewSessionParams>(parameters));
            case AcpMethods.ListSessions:
                return ListSessions();
            case AcpMethods.ResumeSession:
                return await ResumeSessionAsync(Deserialize<ResumeSessionParams>(parameters));
            case AcpMethods.CloseSession:
                return await CloseSessionAsync(Deserialize<CloseSessionParams>(parameters));
            case AcpMethods.Prompt:
                return await PromptAsync(Deserialize<PromptParams>(parameters));
            default:
                throw new InvalidOperationException($"unknown ACP method: {method}");
        }
    }

    public void Cancel(JsonElement? parameters)
    {
        if (parameters is not { } value || !value.TryGetProperty("sessionId", out var sessionId)
            || sessionId.ValueKind != JsonValueKind.String)
        {
            return;
        }
        if (_sessions.TryGetValue(sessionId.GetString()!, out var record))
            record.Agent.Cancel(new AgentCancelCause.User());
    }

    public async Task CloseAllAsync()
    {
        _closed = true;
        foreach (var disposer in _disposers)
            disposer();
        _disposers.Clear();
        var sessions = _ctx.Get<SessionStore>(SessionStore.ServiceName);
        foreach (var record in _sessions.Values)
        {
            record.Agent.Cancel(new AgentCancelCause.Disposed());
            if (sessions is not null)
                await sessions.Flush(record.Agent.Session);
            record.Hooks?.Dispose();
            record.Handle.Dispose.Dispose();
        }
        _sessions.Clear();
    }

    private Dictionary<string, object?> Authenticate(JsonElement? parameters)
    {
        if (_requiredToken is null)
            return [];
        var presented = parameters is { } value
            && value.TryGetProperty("methodId", out var methodId) && methodId.ValueEquals(AcpMethods.LocalTokenAuthMethod)
            && value.TryGetProperty("_meta", out var meta) && meta.ValueKind == JsonValueKind.Object
            && meta.TryGetProperty("token", out var token) && token.ValueKind == JsonValueKind.String
                ? token.GetString()
                : null;
        if (!_requiredToken.Matches(presented))
            throw new InvalidOperationException("ACP authentication failed");
        _authenticated = true;
        return [];
    }

    private Dictionary<string, object?> InitializeResult()
    {
        return new Dictionary<string, object?>
        {
            ["protocolVersion"] = AcpMethods.ProtocolVersion,
            ["agentInfo"] = new Dictionary<string, object?>
            {
                ["name"] = "deepseek-harness-acp",
                ["version"] = "0.0.1",
            },
            ["agentCapabilities"] = new Dictionary<string, object?>
            {
                ["mcpCapabilities"] = new Dictionary<string, object?> { ["http"] = true },
                ["promptCapabilities"] = new Dictionary<string, object?>
                {
                    ["image"] = false,
                    ["audio"] = false,
                    ["embeddedContext"] = false,
                },
                ["sessionCapabilities"] = new Dictionary<string, object?>
                {
                    ["close"] = new Dictionary<string, object?>(),
                    ["list"] = new Dictionary<string, object?>(),
                    ["resume"] = new Dictionary<string, object?>(),
                },
            },
            ["authMethods"] = _requiredToken is null ? Array.Empty<object?>() : [LocalTokenAuthMethod()],
        };
    }

    private static Dictionary<string, object?> LocalTokenAuthMethod() => new()
    {
        ["id"] = AcpMethods.LocalTokenAuthMethod,
        ["name"] = "Local token",
        ["description"] = "Send the content of <harness home>/run/dsh-acp.token as _meta.token",
    };

    private async Task<Dictionary<string, object?>> NewSessionAsync(NewSessionParams parameters)
    {
        ValidateWorkspaceParameters(parameters.Cwd, parameters.AdditionalDirectories);
        var agents = _ctx.Get<AgentRegistry>(AgentRegistry.ServiceName)
            ?? throw new InvalidOperationException("ACP server requires the agents service");
        var sessionId = Guid.NewGuid().ToString();
        var handle = await agents.Create(new CreateAgentOptions(
            SessionId.Create(sessionId),
            parameters.Cwd,
            new AgentOptions(_provider, _model)));
        _sessions[sessionId] = new AcpSessionRecord(handle, handle.Agent, _installAgentHooks?.Invoke(handle.Agent.Ctx));
        return new Dictionary<string, object?>
        {
            ["sessionId"] = sessionId,
            ["configOptions"] = Array.Empty<object?>(),
        };
    }

    private Dictionary<string, object?> ListSessions()
    {
        var sessions = _ctx.Get<SessionStore>(SessionStore.ServiceName)?.List() ?? [];
        var items = sessions.Select(session => (object?)new Dictionary<string, object?>
        {
            ["sessionId"] = session.Id.Value,
            ["cwd"] = session.Header.Cwd ?? "",
        }).ToList();
        return new Dictionary<string, object?> { ["sessions"] = items };
    }

    private async Task<Dictionary<string, object?>> ResumeSessionAsync(ResumeSessionParams parameters)
    {
        ValidateWorkspaceParameters(parameters.Cwd, parameters.AdditionalDirectories);
        var sessionId = SessionId.Create(parameters.SessionId);
        if (_sessions.ContainsKey(parameters.SessionId)
            || _ctx.Get<SessionStore>(SessionStore.ServiceName)?.Get(sessionId) is not null)
        {
            throw new InvalidOperationException($"session is already active: {parameters.SessionId}");
        }
        var agents = _ctx.Get<AgentRegistry>(AgentRegistry.ServiceName)
            ?? throw new InvalidOperationException("ACP server requires the agents service");
        var handle = await agents.Resume(new ResumeAgentOptions(
            sessionId,
            new AgentOptions(_provider, _model)));
        _sessions[parameters.SessionId] = new AcpSessionRecord(handle, handle.Agent, _installAgentHooks?.Invoke(handle.Agent.Ctx));
        return new Dictionary<string, object?> { ["configOptions"] = Array.Empty<object?>() };
    }

    private async Task<Dictionary<string, object?>> CloseSessionAsync(CloseSessionParams parameters)
    {
        if (!_sessions.TryGetValue(parameters.SessionId, out var record))
            throw new InvalidOperationException($"unknown session: {parameters.SessionId}");
        record.Agent.Cancel(new AgentCancelCause.Disposed());
        var sessions = _ctx.Get<SessionStore>(SessionStore.ServiceName);
        if (sessions is not null)
            await sessions.Flush(record.Agent.Session);
        record.Hooks?.Dispose();
        record.Handle.Dispose.Dispose();
        _sessions.Remove(parameters.SessionId);
        return new Dictionary<string, object?>();
    }

    private async Task<Dictionary<string, object?>> PromptAsync(PromptParams parameters)
    {
        if (!_sessions.TryGetValue(parameters.SessionId, out var record))
            throw new InvalidOperationException($"unknown session: {parameters.SessionId}");
        var content = DeserializePrompt(parameters.Prompt);
        if (content.Count == 0)
            throw new InvalidOperationException("empty prompt");
        var message = MessageFactory.CreateUserMessage(content);
        record.Agent.Followup(message);
        await record.Agent.WhenIdle();
        var stopReason = LastStopReason(record.Agent.Session);
        return new Dictionary<string, object?> { ["stopReason"] = stopReason };
    }

    private static List<ContentBlock> DeserializePrompt(JsonElement[] prompt)
    {
        var blocks = new List<ContentBlock>(prompt.Length);
        foreach (var element in prompt)
        {
            if (!element.TryGetProperty("type", out var typeElement) || typeElement.ValueKind != JsonValueKind.String)
                throw new InvalidOperationException("ACP prompt content is invalid");
            switch (typeElement.GetString())
            {
                case "text":
                    blocks.Add(new TextBlock(element.GetProperty("text").GetString() ?? ""));
                    break;
                case "resource_link":
                    blocks.Add(new TextBlock(ResourceLinkText(element)));
                    break;
                default:
                    throw new InvalidOperationException("ACP prompt content is not supported");
            }
        }
        return blocks;
    }

    private static string ResourceLinkText(JsonElement block)
    {
        var name = block.TryGetProperty("name", out var nameElement) ? nameElement.GetString() ?? "" : "";
        var uri = block.TryGetProperty("uri", out var uriElement) ? uriElement.GetString() ?? "" : "";
        return $"\n[resource_link name={JsonSerializer.Serialize(name)} uri={JsonSerializer.Serialize(uri)}]\n";
    }

    private static string LastStopReason(Session session)
    {
        TurnEndReason? end = null;
        foreach (var sessionEvent in session.SnapshotEvents())
        {
            if (sessionEvent.Data is TurnEndPayload turnEnd)
                end = turnEnd.Reason;
        }
        return end switch
        {
            TurnEndReason.Completed => AcpMethods.StopEndTurn,
            TurnEndReason.MaxTokens => AcpMethods.StopMaxTokens,
            TurnEndReason.Aborted => AcpMethods.StopEndTurn,
            TurnEndReason.Interrupted => AcpMethods.StopCancelled,
            TurnEndReason.Blocked => AcpMethods.StopEndTurn,
            TurnEndReason.Error => AcpMethods.StopEndTurn,
            _ => AcpMethods.StopCancelled,
        };
    }

    private void EmitSessionUpdate(Session session, SessionEvent sessionEvent)
    {
        switch (sessionEvent.Data)
        {
            case AssistantMessagePayload assistant:
                foreach (var block in assistant.Message.Content)
                {
                    switch (block)
                    {
                        case TextBlock { Text.Length: > 0 } text:
                            NotifyUpdate(session.Id.Value, assistant.Message.Id.Value, AcpMethods.UpdateAgentMessageChunk, text.Text);
                            break;
                        case ReasoningBlock { Text.Length: > 0 } reasoning:
                            NotifyUpdate(session.Id.Value, assistant.Message.Id.Value, AcpMethods.UpdateAgentThoughtChunk, reasoning.Text);
                            break;
                    }
                }
                break;
            case ToolCallPayload toolCall:
                _transport.Notify(AcpMethods.ClientSessionUpdate, new Dictionary<string, object?>
                {
                    ["sessionId"] = session.Id.Value,
                    ["update"] = new Dictionary<string, object?>
                    {
                        ["sessionUpdate"] = AcpMethods.UpdateToolCall,
                        ["toolCallId"] = toolCall.CallId.Value,
                        ["title"] = toolCall.Name,
                        ["kind"] = "other",
                        ["status"] = "in_progress",
                        ["rawInput"] = ParseToolArguments(toolCall.Arguments),
                    },
                });
                break;
            case ToolResultPayload toolResult:
                _transport.Notify(AcpMethods.ClientSessionUpdate, new Dictionary<string, object?>
                {
                    ["sessionId"] = session.Id.Value,
                    ["update"] = new Dictionary<string, object?>
                    {
                        ["sessionUpdate"] = AcpMethods.UpdateToolCallResult,
                        ["toolCallId"] = toolResult.Message.ToolSource.CallId.Value,
                        ["status"] = toolResult.Message.Block.IsError == true ? "failed" : "completed",
                        ["content"] = Array.Empty<object?>(),
                    },
                });
                break;
        }
    }

    private void NotifyUpdate(string sessionId, string messageId, string sessionUpdate, string text)
    {
        _transport.Notify(AcpMethods.ClientSessionUpdate, new Dictionary<string, object?>
        {
            ["sessionId"] = sessionId,
            ["update"] = new Dictionary<string, object?>
            {
                ["sessionUpdate"] = sessionUpdate,
                ["messageId"] = messageId,
                ["content"] = new Dictionary<string, object?>
                {
                    ["type"] = "text",
                    ["text"] = text,
                },
            },
        });
    }

    private static object ParseToolArguments(string arguments)
    {
        try
        {
            return JsonDocument.Parse(arguments).RootElement.Clone();
        }
        catch (JsonException)
        {
            return arguments;
        }
    }

    private static void ValidateWorkspaceParameters(string cwd, JsonElement[]? additionalDirectories)
    {
        if (!Path.IsPathRooted(cwd))
            throw new InvalidOperationException($"cwd must be an absolute path: {cwd}");
        if (additionalDirectories is { Length: > 0 })
            throw new InvalidOperationException("additionalDirectories is not supported");
    }

    private static T Deserialize<T>(JsonElement? element) where T : class
        => element is { } value
            ? DshJson.Deserialize<T>(value) ?? throw new JsonException($"invalid {typeof(T).Name} params")
            : throw new JsonException($"missing {typeof(T).Name} params");
}