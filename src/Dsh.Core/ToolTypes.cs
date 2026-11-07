using System.Text.Json;
using System.Text.Json.Nodes;
using Dsh.Llm;

namespace Dsh.Core;

public sealed record ToolOutputDefinition(
    JsonObject Schema,
    Func<JsonElement, JsonElement, IReadOnlyList<ContentBlock>> Render,
    Func<JsonElement, JsonElement, JsonElement?>? PresentationMeta = null);

/**
 * 渐进式披露: 同一 Group 的工具默认不出现在模型可见的 schema 里, 只暴露一个路由工具;
 * 模型调用路由工具展开该组后才真正看到这些工具。DefaultVisible=true 表示即使成组也直接暴露。
 */
public sealed record ToolDisclosure(string Group, string Summary, bool DefaultVisible = false);

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
    /** Preliminary admission hints only; actual host-mediated operations are authorized separately. */
    public Func<JsonElement, IEnumerable<string>>? FilePaths { get; init; }
    public bool UsesScriptPermissions { get; init; }
    public ToolDisclosure? Disclosure { get; init; }

    /** Unavailable tools remain callable internally but are omitted from model-visible schemas. */
    public Func<bool>? IsAvailable { get; init; }

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

    // Transient scheduler selection is consumed by preparation, never persisted or sent over RPC.
    [System.Text.Json.Serialization.JsonIgnore]
    internal ToolRuntime.RegistrationSnapshot? ScheduledRegistration;

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

    internal Dsh.Runtime.Context? HostContext { get; init; }
    public string? SafetyApprovalRevision { get; set; }
    public string? InitialFileIntent { get; internal set; }
    public bool UsesScriptPermissions { get; internal set; }
    private readonly HashSet<(string Revision, string Path, string Intent)> _fileApprovals = [];
    public bool HasFileApproval(string revision, string path, string intent) => _fileApprovals.Contains((revision, path, intent));
    public void RememberFileApproval(string revision, string path, string intent) => _fileApprovals.Add((revision, path, intent));

    public async Task<bool> CommitAsync(string path,
        Func<string, Dsh.Runtime.SettingsMutationProposal?> prepare,
        Func<string, Dsh.Runtime.SettingsMutationProposal, string, CancellationToken, Task> commit)
    {
        var gate = SettingsMutation.RequireGate(this);
        if (!gate.Owns(path))
            throw new InvalidOperationException("The mutation does not target the active settings document.");
        var canonical = gate.SettingsPath;
        await SafetyFileAccess.AuthorizeAsync(canonical, "read", this, Signal, settingsReview: true).ConfigureAwait(false);
        if (prepare(canonical) is not { } proposal)
            return false;
        await gate.CommitAsync(new(canonical, proposal.Before, proposal.After, this),
            (temporary, signal) => commit(canonical, proposal, temporary, signal), Signal).ConfigureAwait(false);
        return true;
    }

    public SettingsMutationAuditPayload? LastSettingsMutation { get; private set; }

    public void RecordSettingsMutation(SettingsMutationAuditPayload audit, Dsh.Runtime.Context? fallback = null)
    {
        LastSettingsMutation = audit;
        if (Agent is { } agent)
            agent.Session.Append(audit);
        else
            fallback?.LoggerFor("settings-mutation").Warn(
                $"settings {audit.Outcome}; tool={audit.ToolName}; call={audit.CallId.Value}; root={audit.RootCallId.Value}; target={audit.Path}; beforeHash={audit.BeforeHash}; afterHash={audit.AfterHash}\n{audit.Diff}");
    }

    internal ToolRuntime.RegistrationSnapshot Registration { get; set; }

    internal void ClearSafetyApprovals()
    {
        SafetyApprovalRevision = null;
        _fileApprovals.Clear();
    }

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

public readonly struct FusedSignal : IDisposable
{
    private readonly CancellationTokenSource? _linked;
    private FusedSignal(CancellationToken token, CancellationTokenSource? linked)
    {
        Token = token;
        _linked = linked;
    }
    public CancellationToken Token { get; }
    public static FusedSignal Fuse(CancellationToken caller, CancellationToken wrapper)
    {
        if (caller == wrapper || !wrapper.CanBeCanceled) return new(caller, null);
        if (!caller.CanBeCanceled) return new(wrapper, null);
        var linked = CancellationTokenSource.CreateLinkedTokenSource(caller, wrapper);
        return new(linked.Token, linked);
    }
    public void Dispose() => _linked?.Dispose();
}
