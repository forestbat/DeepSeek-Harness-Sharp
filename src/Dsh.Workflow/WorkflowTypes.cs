using System.Text.Json.Serialization;
using Cordis;
using Dsh.Llm;

namespace Dsh.Workflow;

[JsonConverter(typeof(BrandJsonConverter<WorkflowRunId>))]
public readonly record struct WorkflowRunId(string Value) : IBrand<WorkflowRunId>
{
    public static WorkflowRunId Create(string value) => new(value);
    public override string ToString() => Value;
}

public sealed record WorkflowPhase(
    string Title,
    string? Detail = null,
    string? Provider = null,
    string? Model = null);

public sealed record WorkflowMeta(
    string Name,
    string Description,
    string? WhenToUse = null,
    IReadOnlyList<WorkflowPhase>? Phases = null);

public static class WorkflowStopReason
{
    public const string Completed = "completed";
    public const string Cancelled = "cancelled";
    public const string Error = "error";
}

public sealed record WorkflowResult
{
    public required object? Value { get; init; }
    public required string StopReason { get; init; }
    public string? Error { get; init; }
    public required int AgentsStarted { get; init; }
}

public sealed record WorkflowRunInfo(WorkflowRunId Id, WorkflowMeta Meta);

public sealed record WorkflowAgentInfo(
    int Seq,
    string Label,
    string? Phase,
    SessionId ChildId);

public static class WorkflowAgentOutcome
{
    public const string Completed = "completed";
    public const string Failed = "failed";
    public const string Cancelled = "cancelled";
}

public sealed record WorkflowAgentEndInfo(
    int Seq,
    string Label,
    string? Phase,
    SessionId ChildId,
    string Outcome);

public sealed record WorkflowResultInfo(
    string StopReason,
    string? Error,
    int AgentsStarted);

public static class WorkflowErrorCodes
{
    public const string ScriptParse = "SCRIPT_PARSE";
    public const string MetaInvalid = "META_INVALID";
    public const string InvalidArgument = "INVALID_ARGUMENT";
    public const string UnsupportedOption = "UNSUPPORTED_OPTION";
    public const string UnsupportedSchema = "UNSUPPORTED_SCHEMA";
    public const string AgentCap = "AGENT_CAP";
    public const string ItemCap = "ITEM_CAP";
    public const string AgentStart = "AGENT_START";
    public const string AgentResult = "AGENT_RESULT";
    public const string ResultUnserializable = "RESULT_UNSERIALIZABLE";
    public const string Cancelled = "CANCELLED";
}

public sealed class WorkflowError(string message, string code, Exception? innerException = null, bool fatal = true)
    : HarnessException(message, code, innerException)
{
    public bool Fatal { get; } = fatal;
}

public static class WorkflowFatal
{
    public static bool IsFatalWorkflowError(Exception? error)
        => error is WorkflowError { Fatal: true };
}

public abstract class WorkflowEngine(Context ctx) : Service(ctx, ServiceName)
{
    public const string ServiceName = "workflowEngine";

    public abstract IWorkflowRun Start(WorkflowStartRequest request);

    protected void EmitWorkflowEvent(string name, params object?[] args)
        => Ctx.Events.Emit(Ctx, name, args);
}