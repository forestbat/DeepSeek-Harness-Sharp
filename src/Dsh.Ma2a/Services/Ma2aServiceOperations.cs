using System.Text.Json;
using System.Text.Json.Nodes;
using static Dsh.Ma2a.Services.Ma2aServiceRequests;

namespace Dsh.Ma2a.Services;

public static class Ma2aServiceOperations
{
    public sealed record FormField(string Name, JsonElement Schema, ulong? InitialNumber);

    public sealed class Operation
    {
        private readonly Dictionary<string, Field> _fields;
        private readonly Func<Arguments, JsonArray>? _build;

        internal Operation(string family, string name, string? toolAction, IReadOnlyList<Field> fields,
            Func<Arguments, JsonArray>? build, bool mutation = false, bool terminal = false)
        {
            Family = family;
            Name = name;
            ToolName = toolAction is null ? null : $"{family}.{toolAction}";
            IsMutation = mutation;
            IsTerminal = terminal;
            _fields = fields.ToDictionary(field => field.Name, StringComparer.Ordinal);
            _build = build;
            FormFields = Array.AsReadOnly(fields.Select(field => field.Form).OfType<FormField>().ToArray());
        }

        public string Family { get; }
        public string Name { get; }
        public string? ToolName { get; }
        public bool IsMutation { get; }
        public bool IsTerminal { get; }
        public IReadOnlyList<FormField> FormFields { get; }
        public bool HasRequestBody => _build is not null;

        public JsonArray BuildFormRequest(IReadOnlyDictionary<string, JsonNode?> values)
            => _build!(new Arguments(name => (_fields[name].Form is { } field ? values[field.Name] : null, name), IsForm: true));

        public JsonArray BuildToolRequest(JsonObject values)
            => _build!(new Arguments(name => _fields[name].ToolName is { } alias ? (values[alias], alias) : (null, name), IsForm: false));
    }

    internal sealed record Field(string Name, string? ToolName, FormField? Form);

    internal readonly record struct Arguments(Func<string, (JsonNode? Value, string Name)> Read, bool IsForm)
    {
        public string Text(string name)
        {
            var (value, inputName) = Read(name);
            return value?.GetValue<string>() ?? (IsForm ? "" : throw new ArgumentException($"{inputName} is required."));
        }

        public JsonNode? Get(string name) => Read(name).Value;
        public string? OptionalText(string name) => Get(name)?.GetValue<string>();
        public bool Flag(string name) => Get(name)?.GetValue<bool>() ?? false;
        public ulong? OptionalNumber(string name) => Get(name)?.GetValue<ulong>();
        public ulong Number(string name, ulong fallback = 0) => OptionalNumber(name) ?? fallback;
        public string[] ProgramArguments()
            => Get("args") is JsonArray items ? items.Select(item => item!.GetValue<string>()).ToArray() : [];
    }

    public static IReadOnlyList<Operation> All { get; } = Array.AsReadOnly<Operation>(
    [
        new("files", "list", "list", [RemotePath("path"), Text("after", true)],
            a => List(a.Text("path"), a.OptionalText("after"))),
        new("files", "stat", "stat", [RemotePath("path")], a => Stat(a.Text("path"))),
        new("files", "mkdir", "mkdir", [RemotePath("path"), Flag("parents")],
            a => Mkdir(a.Text("path"), a.Flag("parents")), mutation: true),
        new("files", "read", "read",
            [RemotePath("path"), Number("offset"), Number("length", maximum: MaximumInlineFileBytes, initial: MaximumInlineFileBytes)],
            a => Read(a.Text("path"), a.Number("offset"), checked((uint)a.Number("length", MaximumInlineFileBytes)))),
        new("files", "write", "write",
            [RemotePath("path"), Text("content_hex", maximumBytes: MaximumInlineFileBytes * 2, formOnly: true), Flag("replace"), ToolField("length")],
            a => Write(a.Text("path"), a.IsForm ? (uint)Convert.FromHexString(a.Text("content_hex")).Length : checked((uint)a.Number("length")),
                a.Flag("replace")), mutation: true),
        new("files", "rename", "rename",
            [RemotePath("from", toolName: "path"), RemotePath("to", toolName: "destination"), Flag("replace")],
            a => Rename(a.Text("from"), a.Text("to"), a.Flag("replace")), mutation: true),
        new("files", "remove", "remove", [RemotePath("path"), Flag("recursive")],
            a => Remove(a.Text("path"), a.Flag("recursive")), mutation: true),
        new("transfer", "download", null, [RemotePath("path"), LocalPath(), Flag("replace")], null),
        new("transfer", "upload", null, [RemotePath("path"), LocalPath(), Flag("replace")], null, mutation: true),
        new("exec", "exec", "run", [.. ProgramFields(), ToolField("stdin"), ToolField("timeout_ms")],
            a => Exec(a.Text("program"), a.ProgramArguments(), a.OptionalText("cwd"), a.IsForm || a.Flag("stdin"),
                a.Number("timeout_ms", DefaultExecTimeoutMilliseconds)), mutation: true, terminal: true),
        new("shell", "shell", "open",
            [.. ProgramFields(optionalProgram: true), ToolField("columns"), ToolField("rows"), ToolField("lifetime_ms")],
            a => Shell(a.OptionalText("program"), a.ProgramArguments(), a.OptionalText("cwd"),
                checked((ushort)a.Number("columns", DefaultShellColumns)), checked((ushort)a.Number("rows", DefaultShellRows)),
                a.Number("lifetime_ms", MaximumLifetimeMilliseconds)), mutation: true, terminal: true),
        new("process", "process start", "start", [.. ProgramFields(), Flag("stdin"), Text("name", true)],
            a => ProcessStart(a.Text("program"), a.ProgramArguments(), a.OptionalText("cwd"), a.Flag("stdin"), a.OptionalText("name")), mutation: true),
        new("process", "process list", "list", [Text("after", true)], a => Processes(a.OptionalText("after"))),
        new("process", "process inspect", "inspect", [ProcessId()], a => ProcessInspect(a.Text("process_id"))),
        new("process", "process logs", "logs", [ProcessId(), Number("cursor")],
            a => ProcessLogs(a.Text("process_id"), a.OptionalNumber("cursor"))),
        new("process", "process attach", "attach", [ProcessId(), Flag("stdin"), ToolField("cursor")],
            a => ProcessAttach(a.Text("process_id"), a.OptionalNumber("cursor"), a.Flag("stdin")), mutation: true, terminal: true),
        new("process", "process stdin", "stdin", [ProcessId(), Text("input", maximumBytes: null, formOnly: true), Flag("close")],
            a => ProcessStdin(a.Text("process_id"), a.Flag("close")), mutation: true),
        new("process", "process signal", "signal", [ProcessId(), Number("signal", maximum: MaximumProcessSignal)],
            a => ProcessSignal(a.Text("process_id"), checked((byte)a.Number("signal"))), mutation: true),
        new("process", "process terminate", "terminate",
            [ProcessId(), Number("grace_ms", maximum: MaximumProcessGraceMilliseconds, initial: DefaultProcessGraceMilliseconds)],
            a => ProcessTerminate(a.Text("process_id"), checked((uint)a.Number("grace_ms", DefaultProcessGraceMilliseconds))), mutation: true),
        new("process", "process delete", "delete", [ProcessId()], a => ProcessDelete(a.Text("process_id")), mutation: true),
        new("agent", "discover", "list", [Text("after", true)], a => Agents(a.OptionalText("after"))),
        new("agent", "submit", "submit", [Text("agent"), Message(), ToolField("from_agent")],
            a => AgentSubmit(a.Text("agent"), a.Text("message"), a.OptionalText("from_agent")), mutation: true),
        new("agent", "send", "send", [Text("agent"), TaskId(), Message()],
            a => AgentSend(a.Text("agent"), a.Text("task_id"), a.Text("message")), mutation: true),
        new("agent", "get", "get", [Text("agent"), TaskId()], a => AgentGet(a.Text("agent"), a.Text("task_id"))),
        new("agent", "tasks", "tasks", [Text("agent"), Text("after", true)], a => AgentTasks(a.Text("agent"), a.OptionalText("after"))),
        new("agent", "watch", "watch", [Text("agent"), TaskId(), Number("after", maximum: uint.MaxValue)],
            a => AgentWatch(a.Text("agent"), a.Text("task_id"), checked((uint)a.Number("after")))),
        new("agent", "cancel", "cancel", [Text("agent"), TaskId()], a => AgentCancel(a.Text("agent"), a.Text("task_id")), mutation: true),
        new("agent", "artifact", "artifact",
            [Text("agent"), TaskId(), Text("name"), LocalPath(), Flag("replace", formOnly: true), ToolField("offset"), ToolField("identity")],
            a => AgentArtifact(a.Text("agent"), a.Text("task_id"), a.Text("name"), a.Number("offset"), a.Get("identity") as JsonArray)),
        new("service", "describe", "describe", [Text("after", true)], a => Describe(a.OptionalText("after"))),
        new("service", "call", "call",
            [Text("service", toolName: "name"), Number("version", 1, ushort.MaxValue), Text("operation"),
                Text("body_hex", maximumBytes: MaximumHostedCallBodyBytes * 2, toolName: "hex"),
                Text("input_hex", maximumBytes: null, formOnly: true), Text("input_file", true, maximumBytes: null, formOnly: true),
                Flag("keep_input_open", formOnly: true)], BuildHostedCall, mutation: true),
        new("proxy", "forward", "open",
            [Text("host"), Number("port", 1, ushort.MaxValue), Number("local_port", maximum: ushort.MaxValue, formOnly: true), ToolField("idle_ms")],
            a => Proxy(a.Text("host"), checked((ushort)a.Number("port")), a.Number("idle_ms", DefaultProxyIdleMilliseconds)), mutation: true),
    ]);

    private static JsonArray BuildHostedCall(Arguments values)
    {
        if (values.IsForm && values.OptionalText("input_file") is { } path)
        {
            if (string.IsNullOrWhiteSpace(path))
                throw new ArgumentException("Choose a local input file or leave input file null.");
            if (values.Text("input_hex").Length != 0)
                throw new ArgumentException("Input file and input hex are mutually exclusive.");
        }
        return ServiceCall(values.Text("service"), checked((ushort)values.Number("version", 1)),
            values.Text("operation"), Convert.FromHexString(values.Text("body_hex")));
    }

    private static Field Text(string name, bool nullable = false, int? maximumBytes = 4096, string? toolName = null, bool formOnly = false)
    {
        JsonNode schema = new JsonObject { ["type"] = "string" };
        if (maximumBytes is { } maximum)
            schema["max_bytes"] = maximum;
        return WithForm(name, nullable ? new JsonObject { ["nullable"] = schema } : schema, formOnly, toolName);
    }

    private static Field RemotePath(string name, string? toolName = null)
        => Text(name, maximumBytes: MaximumRemotePathTextBytes, toolName: toolName);

    private static Field LocalPath() => Text("local_path", maximumBytes: null, formOnly: true);
    private static Field ProcessId() => Text("process_id", toolName: "id");
    private static Field TaskId() => Text("task_id", toolName: "task");
    private static Field Message() => Text("message", maximumBytes: MaximumAgentMessageBytes, toolName: "text");
    private static Field ToolField(string name) => new(name, name, null);

    private static Field Flag(string name, bool formOnly = false)
        => WithForm(name, new JsonObject { ["type"] = "boolean" }, formOnly);

    private static Field Number(string name, ulong minimum = 0, ulong maximum = ulong.MaxValue, ulong? initial = null, bool formOnly = false)
        => WithForm(name, new JsonObject { ["type"] = "integer", ["minimum"] = minimum, ["maximum"] = maximum }, formOnly,
            initial: initial ?? minimum);

    private static Field WithForm(string name, JsonNode schema, bool formOnly = false, string? toolName = null, ulong? initial = null)
    {
        using var document = JsonDocument.Parse(schema.ToJsonString());
        return new Field(name, formOnly ? null : toolName ?? name, new FormField(name, document.RootElement.Clone(), initial));
    }

    private static IReadOnlyList<Field> ProgramFields(bool optionalProgram = false) =>
    [
        Text("program", optionalProgram, MaximumProgramTextBytes),
        WithForm("args", new JsonObject { ["type"] = "array", ["max_items"] = MaximumProgramArgumentCount,
            ["items"] = new JsonObject { ["type"] = "string", ["max_bytes"] = MaximumProgramTextBytes } }),
        Text("cwd", true, MaximumProgramTextBytes),
    ];
}
