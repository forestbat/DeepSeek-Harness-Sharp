using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Dsh.Boot;
using Dsh.Core;
using Dsh.Llm;
using Dsh.Runtime;

namespace Dsh.A2A;

/**
 * 出站 A2A 客户端工具: 让 DSH agent 作为 A2A Client 调用远端 agent。
 * A2A 是 client-server: 只做 Server 的实现之间不会自己产生会话, 这组工具补的正是 DSH 缺的 Client 角色。
 */
public sealed class A2aClientTools(IReadOnlyDictionary<string, A2aRemoteSettings> remotes)
{
    public const string Card = "a2a_card";
    public const string Send = "a2a_send";
    public const string Get = "a2a_get";
    public const string List = "a2a_list";
    public const string Cancel = "a2a_cancel";

    private const int DefaultPollSeconds = 60;
    private const int MaxPollSeconds = 600;

    private static readonly JsonObject ObjectSchema = JsonNode.Parse("""{"type":"object"}""")!.AsObject();

    private const string SectionText =
        "The a2a_* tools are an A2A client: they call a remote A2A agent (server). "
        + "target is a configured remote name or an absolute http(s) URL. "
        + "a2a_card discovers the agent, a2a_send submits a message (optionally waits for a terminal task), "
        + "a2a_get/a2a_list poll, a2a_cancel cancels.";

    private readonly A2aRemoteClient _client = new(remotes);

    public IDisposable Register(Context ctx)
    {
        var tools = ctx.Get<ToolRuntime>(ToolRuntime.ServiceName)
            ?? throw new InvalidOperationException("the a2a client tools require the tools service");
        var prompt = ctx.Get<SystemPrompt>(SystemPrompt.ServiceName)
            ?? throw new InvalidOperationException("the a2a client tools require the system prompt service");
        return new DisposableBundle(
            prompt.Section(PromptSection.Literal("tool:a2a", SectionText)),
            tools.Register(CardTool()),
            tools.Register(SendTool()),
            tools.Register(GetTool()),
            tools.Register(ListTool()),
            tools.Register(CancelTool()));
    }

    private ToolDefinition CardTool() => new()
    {
        Name = Card,
        Description = "Resolve a remote A2A agent's card (identity, capabilities, skills, interfaces).",
        Parameters = Parameters([("target", TargetSchema())], "target"),
        Output = new ToolOutputDefinition(ObjectSchema, (_, value) => Text(FormatCard(value))),
        Execute = async (args, exec) => await _client.CardAsync(RequireString(args, "target"), exec.Signal),
    };

    private ToolDefinition SendTool() => new()
    {
        Name = Send,
        Description = "Send a text message to a remote A2A agent as an A2A client; optionally poll until the task is terminal.",
        Parameters = Parameters(
        [
            ("target", TargetSchema()),
            ("message", StringSchema("Text to send as a user message.")),
            ("wait", new JsonObject { ["type"] = "boolean", ["description"] = "Poll for a terminal task state; default true." }),
            ("timeout_seconds", new JsonObject { ["type"] = "integer", ["minimum"] = 1, ["maximum"] = MaxPollSeconds, ["description"] = "Poll budget in seconds; default 60." }),
        ], "target", "message"),
        Output = new ToolOutputDefinition(ObjectSchema, (_, value) => Text(FormatTaskResult(value))),
        Execute = async (args, exec) =>
        {
            var wait = !args.TryGetProperty("wait", out var waitElement) || waitElement.ValueKind != JsonValueKind.False;
            var timeout = args.TryGetProperty("timeout_seconds", out var secondsElement) && secondsElement.TryGetInt32(out var seconds)
                ? Math.Clamp(seconds, 1, MaxPollSeconds)
                : DefaultPollSeconds;
            return await _client.SendAsync(RequireString(args, "target"), RequireString(args, "message"), wait, timeout, exec.Signal);
        },
    };

    private ToolDefinition GetTool() => new()
    {
        Name = Get,
        Description = "Get a remote A2A task's current state and answer by id.",
        Parameters = Parameters([("target", TargetSchema()), ("task_id", StringSchema("Task id returned by a2a_send."))], "target", "task_id"),
        Output = new ToolOutputDefinition(ObjectSchema, (_, value) => Text(FormatTaskResult(value))),
        Execute = async (args, exec) => await _client.GetAsync(RequireString(args, "target"), RequireString(args, "task_id"), exec.Signal),
    };

    private ToolDefinition ListTool() => new()
    {
        Name = List,
        Description = "List a remote A2A agent's tasks.",
        Parameters = Parameters(
        [
            ("target", TargetSchema()),
            ("context_id", StringSchema("Optional context id filter.")),
            ("page_size", new JsonObject { ["type"] = "integer", ["minimum"] = 1, ["maximum"] = 100, ["description"] = "Max tasks; default 50." }),
        ], "target"),
        Output = new ToolOutputDefinition(ObjectSchema, (_, value) => Text(FormatTasks(value))),
        Execute = async (args, exec) =>
        {
            var contextId = args.TryGetProperty("context_id", out var contextElement) && contextElement.ValueKind == JsonValueKind.String
                ? contextElement.GetString()
                : null;
            var pageSize = args.TryGetProperty("page_size", out var sizeElement) && sizeElement.TryGetInt32(out var size)
                ? Math.Clamp(size, 1, 100)
                : 50;
            return await _client.ListAsync(RequireString(args, "target"), contextId, pageSize, exec.Signal);
        },
    };

    private ToolDefinition CancelTool() => new()
    {
        Name = Cancel,
        Description = "Cancel a remote A2A task by id.",
        Parameters = Parameters([("target", TargetSchema()), ("task_id", StringSchema("Task id to cancel."))], "target", "task_id"),
        Output = new ToolOutputDefinition(ObjectSchema, (_, value) => Text(FormatTaskResult(value))),
        Execute = async (args, exec) => await _client.CancelAsync(RequireString(args, "target"), RequireString(args, "task_id"), exec.Signal),
    };

    private JsonObject TargetSchema() => new()
    {
        ["type"] = "string",
        ["description"] = remotes.Count == 0
            ? "Absolute http(s) URL of the remote A2A agent."
            : $"Configured remote name ({string.Join(", ", remotes.Keys)}) or an absolute http(s) URL.",
    };

    private static JsonObject StringSchema(string description) => new()
    {
        ["type"] = "string",
        ["description"] = description,
    };

    private static JsonObject Parameters((string Name, JsonObject Schema)[] properties, params string[] required)
    {
        var props = new JsonObject();
        foreach (var (name, schema) in properties)
            props[name] = schema;
        var requiredNames = new JsonArray();
        foreach (var name in required)
            requiredNames.Add(name);
        return new JsonObject
        {
            ["type"] = "object",
            ["additionalProperties"] = false,
            ["properties"] = props,
            ["required"] = requiredNames,
        };
    }

    private static IReadOnlyList<ContentBlock> Text(string text) => [new TextBlock(text)];

    private static string RequireString(JsonElement args, string name)
        => args.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && value.GetString() is { Length: > 0 } text
            ? text
            : throw new ArgumentException($"{name} must be a non-empty string");

    private static string FormatCard(JsonElement value)
    {
        var sb = new StringBuilder($"agent {Field(value, "name")} v{Field(value, "version")}");
        var description = Field(value, "description");
        if (description.Length > 0)
            sb.Append('\n').Append(description);
        var streaming = value.TryGetProperty("streaming", out var streamingElement) && streamingElement.ValueKind == JsonValueKind.True;
        sb.Append($"\nstreaming={streaming.ToString().ToLowerInvariant()}");
        var interfaces = Array(value, "interfaces");
        if (interfaces.Count > 0)
            sb.Append("\ninterfaces: ").Append(string.Join("; ", interfaces));
        var skills = Array(value, "skills");
        if (skills.Count > 0)
            sb.Append("\nskills: ").Append(string.Join("; ", skills));
        return sb.ToString();
    }

    private static string FormatTaskResult(JsonElement value)
    {
        var sb = new StringBuilder();
        if (value.TryGetProperty("task", out var taskElement) && taskElement.ValueKind == JsonValueKind.Object)
            sb.Append(FormatTask(taskElement));
        var answer = Field(value, "answer");
        if (answer.Length > 0)
            Append(sb, answer);
        var error = Field(value, "error");
        if (error.Length > 0)
            Append(sb, $"error: {error}");
        return sb.Length == 0 ? "(empty)" : sb.ToString();
    }

    private static string FormatTask(JsonElement task)
    {
        var sb = new StringBuilder($"task {Field(task, "id")} [{Field(task, "state")}] context={Field(task, "contextId")}");
        var answer = Field(task, "answer");
        if (answer.Length > 0)
            Append(sb, answer);
        var error = Field(task, "error");
        if (error.Length > 0)
            Append(sb, $"error: {error}");
        return sb.ToString();
    }

    private static string FormatTasks(JsonElement value)
    {
        if (!value.TryGetProperty("tasks", out var tasksElement) || tasksElement.ValueKind != JsonValueKind.Array)
            return "(no tasks)";
        var lines = tasksElement.EnumerateArray().Select(FormatTask).ToList();
        return lines.Count == 0 ? "(no tasks)" : string.Join('\n', lines);
    }

    private static void Append(StringBuilder builder, string text)
    {
        if (builder.Length > 0)
            builder.Append('\n');
        builder.Append(text);
    }

    private static string Field(JsonElement value, string name)
        => value.TryGetProperty(name, out var element) && element.ValueKind == JsonValueKind.String
            ? element.GetString() ?? ""
            : "";

    private static IReadOnlyList<string> Array(JsonElement value, string name)
        => value.TryGetProperty(name, out var element) && element.ValueKind == JsonValueKind.Array
            ? [.. element.EnumerateArray().Where(entry => entry.ValueKind == JsonValueKind.String).Select(entry => entry.GetString() ?? "")]
            : [];

    private sealed class DisposableBundle(params IDisposable[] disposables) : IDisposable
    {
        public void Dispose()
        {
            foreach (var disposable in disposables)
                disposable.Dispose();
        }
    }
}
