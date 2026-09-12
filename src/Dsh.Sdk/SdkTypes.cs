using System.Text.Json;
using Dsh.Core;
using Dsh.Llm;

namespace Dsh.Sdk;

public static class SdkMethods
{
    public const string Initialize = "initialize";
    public const string SessionPrompt = "session/prompt";
    public const string Shutdown = "shutdown";
    public const string SessionEvent = "session.event";
    public const string SessionStatus = "session.status";
    public const string SubagentStarted = "subagent.started";
    public const string SubagentFinished = "subagent.finished";
    public const string CordisServiceCall = "cordis/service.call";
    public const string CordisEventEmit = "cordis/event.emit";
    public const string CordisEventSerial = "cordis/event.serial";
}

public sealed record InitializeParams(
    string Cwd,
    string Provider,
    string Model,
    string? ReasoningEffort = null,
    int? MaxTokens = null);

public sealed record ServerInfo(string Name, string Version);

public sealed record InitializeResult(ServerInfo ServerInfo);

public sealed record SessionPromptParams(string SessionId, JsonElement[] ContentBlocks);

public sealed record SessionPromptResult(string MessageId);

public sealed record SessionEventNotification(string SessionId, SessionEvent Event);

public sealed record SessionStatusNotification(string SessionId, string Status);

public sealed record SubagentStartedNotification(string ParentSessionId, string ChildSessionId);

public sealed record SubagentFinishedNotification(
    string Provider,
    string AgentId,
    string ParentSessionId,
    string ChildSessionId,
    string Status,
    string StopReason,
    IReadOnlyList<ContentBlock>? LastAssistantMessage = null);

public sealed record CordisServiceCallParams(string Service, string Method, JsonElement[]? Args = null);

public sealed record CordisEventParams(string Name, JsonElement[]? Args = null);
