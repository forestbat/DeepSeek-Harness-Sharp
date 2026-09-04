using System.Text.Json;
using Cordis;
using Dsh.Core;
using Dsh.Llm;

namespace Dsh.Tools;

public sealed record TodoItem(string Content, string Status);

public sealed record TodoWritePayload(IReadOnlyList<TodoItem> Todos) : SessionEventPayload
{
    public const string EventType = "todo/write";

    public override string Type => EventType;
}

public static class TodoWriteTool
{
    public const string ToolName = "todo_write";

    private const string DescriptionHead =
        "Record and update a structured task list for the current work. Send the ENTIRE "
        + "list every call — it REPLACES the previous list (there are no partial updates, "
        + "no per-item edits). Use it to plan multi-step work and show progress: add one "
        + "todo per concrete step before you start. ";

    private const string DescriptionParallel =
        "Mark every todo being actively worked "
        + "on `in_progress` — several at once when work genuinely runs in parallel (e.g. "
        + "concurrent subagents or background commands), one for sequential work; while "
        + "work remains, at least one task should be `in_progress`. ";

    private const string DescriptionSingle =
        "Keep AT MOST ONE todo `in_progress` at a "
        + "time; while work remains, exactly one active task should be `in_progress`. ";

    private const string DescriptionTail =
        "Mark a todo "
        + "`completed` the moment it is done (do not batch completions), and allow no "
        + "`in_progress` item only once all work is complete. Skip the list for trivial "
        + "single-step tasks. Statuses: `pending` (not started), `in_progress` (being "
        + "worked on now), `completed` (finished).";

    private static readonly IReadOnlySet<string> Statuses = new HashSet<string>(StringComparer.Ordinal)
    {
        "pending", "in_progress", "completed",
    };

    public static IDisposable Register(Context ctx, bool allowParallelInProgress = true)
    {
        SessionEventCodec.Register<TodoWritePayload>(TodoWritePayload.EventType);
        var tools = ctx.Get<ToolRuntime>(ToolRuntime.ServiceName)!;
        return tools.Register(new ToolDefinition
        {
            Name = ToolName,
            Description = DescriptionHead + (allowParallelInProgress ? DescriptionParallel : DescriptionSingle) + DescriptionTail,
            Parameters = ToolSchemas.Parse("""
                {
                  "type": "object",
                  "additionalProperties": false,
                  "required": ["todos"],
                  "properties": {
                    "todos": {
                      "type": "array",
                      "description": "The COMPLETE task list, replacing any previous list.",
                      "items": {
                        "type": "object",
                        "additionalProperties": false,
                        "required": ["content", "status"],
                        "properties": {
                          "content": { "type": "string", "description": "What the task is — a short imperative line." },
                          "status": {
                            "type": "string",
                            "enum": ["pending", "in_progress", "completed"],
                            "description": "pending (not started) | in_progress (now) | completed (done)."
                          }
                        }
                      }
                    }
                  }
                }
                """),
            Output = new ToolOutputDefinition(
                ToolSchemas.Parse("""
                    {
                      "type": "object",
                      "additionalProperties": false,
                      "required": ["todos", "counts"],
                      "properties": {
                        "todos": {
                          "type": "array",
                          "items": {
                            "type": "object",
                            "additionalProperties": false,
                            "required": ["content", "status"],
                            "properties": {
                              "content": { "type": "string" },
                              "status": { "type": "string", "enum": ["pending", "in_progress", "completed"] }
                            }
                          }
                        },
                        "counts": {
                          "type": "object",
                          "additionalProperties": false,
                          "required": ["pending", "inProgress", "completed"],
                          "properties": {
                            "pending": { "type": "integer" },
                            "inProgress": { "type": "integer" },
                            "completed": { "type": "integer" }
                          }
                        }
                      }
                    }
                    """),
                (_, value) =>
                {
                    var counts = value.GetProperty("counts");
                    return
                    [
                        new TextBlock(
                            $"Updated todo list: {counts.GetProperty("pending").GetInt32()} pending, "
                            + $"{counts.GetProperty("inProgress").GetInt32()} in progress, "
                            + $"{counts.GetProperty("completed").GetInt32()} completed."),
                    ];
                }),
            Execute = (args, exec) => Execute(args, exec, allowParallelInProgress),
        });
    }

    private static Task<object?> Execute(JsonElement args, ToolRunContext exec, bool allowParallel)
    {
        var todos = ToTodoList(args.GetProperty("todos"), allowParallel);
        if (exec.Agent is null)
            throw new InvalidOperationException("todo_write requires an owning agent session");
        exec.Agent.Session.Append(new TodoWritePayload(todos));
        return Task.FromResult<object?>(new
        {
            todos,
            counts = new
            {
                pending = Count(todos, "pending"),
                inProgress = Count(todos, "in_progress"),
                completed = Count(todos, "completed"),
            },
        });
    }

    private static int Count(IReadOnlyList<TodoItem> todos, string status)
        => todos.Count(todo => todo.Status == status);

    internal static List<TodoItem> ToTodoList(JsonElement element, bool allowParallel)
    {
        var todos = new List<TodoItem>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var active = 0;
        foreach (var item in element.EnumerateArray())
        {
            var content = item.GetProperty("content").GetString()?.Trim() ?? "";
            if (content.Length == 0)
                throw new ArgumentException("invalid todo: `content` must be a non-empty string");
            if (!seen.Add(content))
                throw new ArgumentException($"invalid todos: duplicate content {JsonSerializer.Serialize(content)}");
            var status = item.GetProperty("status").GetString() ?? "";
            if (!Statuses.Contains(status))
                throw new ArgumentException($"invalid todo status: expected one of pending, in_progress, completed, got {JsonSerializer.Serialize(status)}");
            if (status == "in_progress") active++;
            todos.Add(new TodoItem(content, status));
        }
        if (!allowParallel && active > 1)
            throw new ArgumentException($"invalid todos: at most one task may be in_progress (got {active})");
        return todos;
    }
}
