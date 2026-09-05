using System.Text.Json;
using System.Text.Json.Nodes;
using Cordis;
using Dsh.Core;
using Dsh.Llm;

namespace Dsh.Subagent;

public sealed class StructuredOutputAttachment : IDisposable
{
    public const string ToolName = "structured_output";
    public const string SectionName = $"tool:{ToolName}";
    public const string Instruction =
        $"You must produce your final output by calling the `{ToolName}` tool. "
        + $"Do not respond with plain text; call `{ToolName}` with the structured result.";

    private const string ArgumentsInvalidCode = "INVALID_ARGS";

    private readonly IAgent _child;
    private readonly JsonObject _schema;
    private readonly Dictionary<ToolExecution, JsonElement> _staged = new(ReferenceEqualityComparer.Instance);
    private readonly List<IDisposable> _disposables = [];
    private JsonElement? _captured;

    private StructuredOutputAttachment(IAgent child, JsonObject schema)
    {
        _child = child;
        _schema = schema;
    }

    public ToolSchema Schema => new(
        ToolName,
        "Record the final structured result for this delegation. Call exactly once when the result is known; the call ends the turn.",
        _schema);

    public JsonElement? Captured => _captured;

    public static StructuredOutputAttachment Attach(Context childCtx, IAgent child, JsonObject schema)
    {
        var attachment = new StructuredOutputAttachment(child, schema);
        attachment._disposables.Add(new FuncDispose(childCtx.On(ToolRuntime.ExecuteEvent, attachment.OnExecute)));
        attachment._disposables.Add(new FuncDispose(childCtx.On(ToolRuntime.ResultEvent, attachment.OnResult)));
        var tools = childCtx.Get<ToolRuntime>(ToolRuntime.ServiceName)!;
        attachment._disposables.Add(tools.Guard(attachment.GuardTool));
        return attachment;
    }

    public void Dispose()
    {
        foreach (var disposable in _disposables)
            disposable.Dispose();
        _disposables.Clear();
    }

    // structured_output 不经 ToolRuntime 注册（注册只能落全局层，会污染其他 agent 的提示词）;
    // 执行在 tools/execute 瀑布按 child 身份拦截，与 TS 的子 scope 注册语义一致。
    private async ValueTask<object?> OnExecute(object? thisArg, object?[] args)
    {
        var exec = (ToolRunContext)args[0]!;
        var next = (Func<ValueTask<object?>>)args[^1]!;
        if (!ReferenceEquals(exec.Agent, _child) || exec.Name != ToolName)
            return await next();
        var violations = JsonSchemaValidator.Validate(_schema, exec.Arguments, "arguments");
        if (violations.Count > 0)
        {
            throw new HarnessException(
                $"tool \"{ToolName}\" arguments failed schema validation: {string.Join("; ", violations)}",
                ArgumentsInvalidCode);
        }
        _staged[exec] = exec.Arguments.Clone();
        exec.ConcludeTurn();
        return new ToolExecutionResult.Success
        {
            IsError = false,
            Value = JsonDocument.Parse("""{"recorded":true}""").RootElement,
            Content = [new TextBlock("Structured output recorded.")],
        };
    }

    private ValueTask<object?> OnResult(object? thisArg, object?[] args)
    {
        var exec = (ToolExecution)args[0]!;
        var result = (ToolExecutionResult)args[1]!;
        if (ReferenceEquals(exec.Agent, _child)
            && exec.Name == ToolName
            && exec.Parent is null
            && _staged.Remove(exec, out var staged)
            && !result.IsError)
        {
            _captured = staged;
        }
        return new ValueTask<object?>();
    }

    private string? GuardTool(ToolExecution exec)
        => ReferenceEquals(exec.Agent, _child) && _captured is not null
            ? $"structured output already recorded: the run is complete, so `{exec.Name}` is not executed"
            : null;

    private sealed class FuncDispose(Func<bool> dispose) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            dispose();
        }
    }
}
