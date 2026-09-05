using System.Text.Json;
using System.Text.Json.Nodes;
using Dsh.Core;
using Dsh.Llm;

namespace Dsh.Subagent;

public enum SubagentStopReason
{
    Completed,
    Aborted,
    Error,
    MaxTokens,
    Refusal,
}

public static class SubagentStopReasonWire
{
    public static string Of(SubagentStopReason reason) => reason switch
    {
        SubagentStopReason.Completed => "completed",
        SubagentStopReason.Aborted => "aborted",
        SubagentStopReason.Error => "error",
        SubagentStopReason.MaxTokens => "max-tokens",
        SubagentStopReason.Refusal => "refusal",
        _ => throw new ArgumentOutOfRangeException(nameof(reason), reason, null),
    };
}

public sealed record SubagentCapabilities(
    bool AgentOptions = false,
    bool OutputSchema = false,
    bool DepthLimit = false,
    bool ToolFilter = false,
    bool Persona = false);

public sealed record SubagentResult
{
    public required IReadOnlyList<ContentBlock> Output { get; init; }
    public required SubagentStopReason StopReason { get; init; }
    public JsonElement? Structured { get; init; }
    public string? Diagnostic { get; init; }
}

public interface ISubagentRun
{
    SessionId Id { get; }
    IAgent? LocalAgent { get; }
    Task<SubagentResult> Result { get; }
    Task DisposeAsync();
}

public sealed record SubagentStartRequest
{
    public string? Label { get; init; }
    public required IReadOnlyList<ContentBlock> Prompt { get; init; }
    public required IAgent Parent { get; init; }
    public required CancellationToken Signal { get; init; }
    public AgentOptions? AgentOptions { get; init; }
    public JsonObject? OutputSchema { get; init; }
    public int? MaxDepth { get; init; }
    public ToolRestriction? ToolFilter { get; init; }
    public string? Persona { get; init; }
}

public sealed record ResolvedSubagentStartRequest(SubagentStartRequest Request, SubagentDescriptorPayload Descriptor)
{
    public IAgent Parent => Request.Parent;
}

public interface ISubagentProvider
{
    string Name { get; }
    SubagentCapabilities Capabilities { get; }
    bool InheritsParentContext { get; }
    Task<ISubagentRun> StartAsync(ResolvedSubagentStartRequest request);
}

public sealed record SubagentRunInfo(string RunId, string Provider, SessionId Id, bool Local);

public sealed record SubagentRunEndInfo(
    string RunId,
    string Provider,
    SessionId Id,
    bool Local,
    SubagentStopReason StopReason,
    IReadOnlyList<ContentBlock>? LastAssistantMessage = null);
