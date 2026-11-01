using System.Text.Json;
using System.Text.Json.Nodes;
using Dsh.Llm;

namespace Dsh.Core;

public sealed record ToolOutputDefinition(
    JsonObject Schema,
    Func<JsonElement, JsonElement, IReadOnlyList<ContentBlock>> Render,
    Func<JsonElement, JsonElement, JsonElement?>? PresentationMeta = null);

public sealed class ToolDefinition
{
    public required string Name { get; init; }
    public required string Description { get; init; }
    public required JsonObject Parameters { get; init; }
    public required ToolOutputDefinition Output { get; init; }
    public required Func<JsonElement, ToolRunContext, Task<object?>> Execute { get; init; }
    public Func<ToolExecution, ToolExecutionResult, IReadOnlyList<ContentBlock>?>? FinalizeContent { get; init; }
    public long? TimeoutMs { get; init; }
    public Func<JsonElement, bool>? IsConcurrencySafe { get; init; }
    /** Trusted host file tools defer default notification to their actual mediated operations. */
    public bool MediatesFileAccess { get; init; }
    public Func<JsonElement, string>? FileIntent { get; init; }
    public bool UsesScriptPermissions { get; init; }

    /**
     * Absolute paths a call writes, including the directory a newly started process runs in.
     * A target outside the session's write permission root turns the call into an approval request.
     */
    public Func<JsonElement, ToolRunContext, IReadOnlyList<string>>? WriteTargets { get; init; }
}

public abstract record PreToolDecision
{
    public sealed record Allow : PreToolDecision;

    public sealed record Deny(string Reason) : PreToolDecision;

    public sealed record Ask(string? Reason = null) : PreToolDecision;
}

public abstract record PostToolDecision
{
    public sealed record Accept(
        IReadOnlyList<ContentBlock>? Content = null,
        JsonElement? Value = null,
        IReadOnlyList<UserMessage>? AdditionalContexts = null) : PostToolDecision;

    public sealed record Block(IReadOnlyList<ContentBlock> Feedback, IReadOnlyList<UserMessage>? AdditionalContexts = null) : PostToolDecision;
}

public sealed record ToolErrorInfo(string Name, string Code);

public sealed record ToolFailure(string Message, ToolErrorInfo? Info = null);

public abstract record ToolExecutionResult
{
    public required bool IsError { get; init; }
    public required IReadOnlyList<ContentBlock> Content { get; init; }
    public JsonElement? Meta { get; init; }
    public IReadOnlyList<UserMessage>? AdditionalContexts { get; init; }

    public sealed record Success : ToolExecutionResult
    {
        public required JsonElement Value { get; init; }
        public bool ConcludesTurn { get; init; }
    }

    public sealed record Failure : ToolExecutionResult
    {
        public required ToolFailure Error { get; init; }
    }
}

public class ToolExecutionInput
{
    public required ToolCallId CallId { get; init; }
    public ToolCallId? RootCallId { get; init; }
    public required string Name { get; init; }
    public required JsonElement Arguments { get; init; }
    public string RawArguments { get; init; } = "{}";
    public IAgent? Agent { get; init; }
    public object? Parent { get; init; }
    public required CancellationToken Signal { get; init; }

    // 以下投影由 dsh/service.call 桥按名字字符串访问(匹配不区分大小写)。
    public string name => Name;
    public JsonNode? arguments => Arguments.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null
        ? null
        : JsonNode.Parse(Arguments.GetRawText());
    public IAgent? agent => Agent;
    public CancellationToken signal => Signal;
    public string callId => CallId.Value;
}

public class ToolExecution : ToolExecutionInput
{
    public required ToolCallId RootCallIdValue { get; init; }
    public object Token { get; } = new();
    public CancellationToken WrapperSignal { get; set; }
}

public sealed class ToolRunContext : ToolExecution
{
    private readonly List<UserMessage> _deferred = [];

    public void DeferContext(UserMessage context) => _deferred.Add(context);

    public IReadOnlyList<UserMessage> DeferredContexts => _deferred;

    public bool ConcludedRequested { get; private set; }

    public bool BodyInvoked { get; internal set; }

    public bool ExternalProcessProtectionRequired { get; internal set; }
    public IExternalProcessPolicy? ExternalProcessPolicy { get; internal set; }
    public string? SafetyApprovalRevision { get; set; }
    public string? InitialFileIntent { get; internal set; }
    public bool UsesScriptPermissions { get; internal set; }
    public string? OwnerPackage { get; internal set; }
    private readonly HashSet<(string Revision, string Path, string Intent)> _fileApprovals = [];
    public bool HasFileApproval(string revision, string path, string intent) => _fileApprovals.Contains((revision, path, intent));
    public void RememberFileApproval(string revision, string path, string intent) => _fileApprovals.Add((revision, path, intent));

    public SettingsMutationAuditPayload? LastSettingsMutation { get; private set; }

    public void RecordSettingsMutation(SettingsMutationAuditPayload audit, Dsh.Runtime.Context? fallback = null)
    {
        LastSettingsMutation = audit;
        if (Agent is { } agent)
            agent.Session.Append(audit);
        else
            fallback?.LoggerFor("settings-mutation").Warn(
                $"settings {audit.Outcome}; tool={audit.ToolName}; call={audit.CallId.Value}; root={audit.RootCallId.Value}; target={audit.Path}\n--- before\n{audit.Before}\n+++ after\n{audit.After}");
    }

    internal Func<ToolExecution, ToolExecutionResult, IReadOnlyList<ContentBlock>?>? CapturedFinalizer { get; set; }

    public void ConcludeTurn() => ConcludedRequested = true;

    /** Call only after successfully committing a file mutation, never for write intent or directories. */
    public void RecordFileChange(string absolutePath) => Agent?.Session.Append(new FileChangedPayload(absolutePath));
}

public abstract record ScheduledToolPreparation
{
    public sealed record Dispatch(ToolRunContext Exec) : ScheduledToolPreparation;

    public sealed record PostResult(ToolRunContext Exec, ToolExecutionResult Result) : ScheduledToolPreparation;

    public sealed record FinalResult(ToolRunContext Exec, ToolExecutionResult Result) : ScheduledToolPreparation;
}

public abstract record ScheduledToolDispatch
{
    public sealed record PostResult(ToolExecutionResult Result) : ScheduledToolDispatch;

    public sealed record FinalResult(ToolExecutionResult Result) : ScheduledToolDispatch;
}

public static class ToolErrorCodes
{
    public const string UnknownTool = "UNKNOWN_TOOL";
    public const string InvalidToolOutput = "INVALID_TOOL_OUTPUT";
    public const string Aborted = "ABORTED";
    public const string AbortedBeforeDispatch = "ABORTED_BEFORE_DISPATCH";
}

public sealed class ToolNotFoundException : HarnessException
{
    public ToolNotFoundException(string toolName, string? reachableFrom = null)
        : base(reachableFrom is null
            ? $"unknown tool \"{toolName}\""
            : $"unknown tool \"{toolName}\": {reachableFrom}",
            ToolErrorCodes.UnknownTool)
    {
    }
}

public sealed class ToolOutputException : HarnessException
{
    public IReadOnlyList<string> Violations { get; }

    public ToolOutputException(string toolName, IReadOnlyList<string> violations)
        : base($"tool \"{toolName}\" returned invalid output: {string.Join("; ", violations)}", ToolErrorCodes.InvalidToolOutput)
    {
        Violations = violations;
    }
}

public sealed record ToolRestriction(IReadOnlyList<string>? Allow = null, IReadOnlyList<string>? Deny = null);
