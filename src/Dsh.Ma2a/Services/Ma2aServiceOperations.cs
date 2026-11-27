using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using static Dsh.Ma2a.Services.Ma2aServiceRequests;

namespace Dsh.Ma2a.Services;

public static class Ma2aServiceOperations
{
    private const string FileContentField = "content_hex";
    private const string ProcessInputField = "input";
    private const string StdinField = "stdin";
    private const string HostedInputFileField = "input_file";
    private const string HostedInputHexField = "input_hex";
    private const string KeepHostedInputOpenField = "keep_input_open";
    private const string LocalPathField = "local_path";
    private const string LocalPortField = "local_port";
    private const string ReplaceField = "replace";

    public sealed record FormField(string Name, JsonElement Schema, ulong? InitialNumber);
    public readonly record struct PreparedForm(JsonArray? Body, FormIntent? Intent = null);

    public abstract record FormIntent;
    public sealed record TerminalIntent(bool SupportsResize, bool AllowsInput) : FormIntent;
    public sealed record ProxyIntent(ushort LocalPort) : FormIntent;
    public sealed record TransferIntent(
        Func<Ma2aClient, string, string, IProgress<Ma2aTransferProgress>, CancellationToken, Task<Ma2aTransferResult>> ExecuteAsync) : FormIntent;
    public sealed record StreamIntent(Func<Ma2aServiceSession, CancellationToken, Task>? SendInputAsync = null,
        bool DecodeAgentEvents = false, bool UsesServiceControlFrames = false) : FormIntent;

    public sealed class Operation
    {
        private readonly Dictionary<string, Field> _fields;
        private readonly Func<Arguments, PreparedForm> _prepare;

        internal Operation(string family, string name, string? toolAction, IReadOnlyList<Field> fields,
            Func<Arguments, JsonArray> build, bool mutation = false)
            : this(family, name, toolAction, fields, a => new PreparedForm(build(a)), mutation) { }

        internal Operation(string family, string name, string? toolAction, IReadOnlyList<Field> fields,
            Func<Arguments, PreparedForm> prepare, bool mutation = false)
        {
            Family = family;
            Name = name;
            ToolName = toolAction is null ? null : $"{family}.{toolAction}";
            IsMutation = mutation;
            _fields = fields.ToDictionary(field => field.Name, StringComparer.Ordinal);
            _prepare = prepare;
            FormFields = Array.AsReadOnly(fields.Select(field => field.Form).OfType<FormField>().ToArray());
        }

        public string Family { get; }
        public string Name { get; }
        public string? ToolName { get; }
        public bool IsMutation { get; }
        public IReadOnlyList<FormField> FormFields { get; }

        public PreparedForm PrepareForm(IReadOnlyDictionary<string, JsonNode?> values)
            => _prepare(new Arguments(name => (_fields[name].Form is { } field ? values[field.Name] : null, name), IsForm: true));

        public JsonArray BuildToolRequest(JsonObject values)
            => _prepare(new Arguments(name => _fields[name].ToolName is { } alias ? (values[alias], alias) : (null, name), IsForm: false)).Body
                ?? throw new InvalidOperationException("This operation has no tool request body.");
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
            [RemotePath("path"), Text(FileContentField, maximumBytes: MaximumInlineFileBytes * 2, formOnly: true), Flag(ReplaceField), ToolField("length")],
            PrepareFileWrite, mutation: true),
        new("files", "rename", "rename",
            [RemotePath("from", toolName: "path"), RemotePath("to", toolName: "destination"), Flag(ReplaceField)],
            a => Rename(a.Text("from"), a.Text("to"), a.Flag(ReplaceField)), mutation: true),
        new("files", "remove", "remove", [RemotePath("path"), Flag("recursive")],
            a => Remove(a.Text("path"), a.Flag("recursive")), mutation: true),
        new("transfer", "download", null, [RemotePath("path"), LocalPath(), Flag(ReplaceField)], a => PrepareFileTransfer(a, upload: false)),
        new("transfer", "upload", null, [RemotePath("path"), LocalPath(), Flag(ReplaceField)], a => PrepareFileTransfer(a, upload: true), mutation: true),
        new("exec", "exec", "run", [.. ProgramFields(), ToolField(StdinField), ToolField("timeout_ms")],
            a => new PreparedForm(Exec(a.Text("program"), a.ProgramArguments(), a.OptionalText("cwd"), a.IsForm || a.Flag(StdinField),
                a.Number("timeout_ms", DefaultExecTimeoutMilliseconds)), a.IsForm ? new TerminalIntent(SupportsResize: false, AllowsInput: true) : null), mutation: true),
        new("shell", "shell", "open",
            [.. ProgramFields(optionalProgram: true), ToolField("columns"), ToolField("rows"), ToolField("lifetime_ms")],
            a => new PreparedForm(Shell(a.OptionalText("program"), a.ProgramArguments(), a.OptionalText("cwd"),
                checked((ushort)a.Number("columns", DefaultShellColumns)), checked((ushort)a.Number("rows", DefaultShellRows)),
                a.Number("lifetime_ms", MaximumLifetimeMilliseconds)), a.IsForm ? new TerminalIntent(SupportsResize: true, AllowsInput: true) : null), mutation: true),
        new("process", "process start", "start", [.. ProgramFields(), Flag(StdinField), Text("name", true)],
            a => ProcessStart(a.Text("program"), a.ProgramArguments(), a.OptionalText("cwd"), a.Flag(StdinField), a.OptionalText("name")), mutation: true),
        new("process", "process list", "list", [Text("after", true)], a => Processes(a.OptionalText("after"))),
        new("process", "process inspect", "inspect", [ProcessId()], a => ProcessInspect(a.Text("process_id"))),
        new("process", "process logs", "logs", [ProcessId(), Number("cursor")],
            a => ProcessLogs(a.Text("process_id"), a.OptionalNumber("cursor"))),
        new("process", "process attach", "attach", [ProcessId(), Flag(StdinField), ToolField("cursor")], PrepareProcessAttach, mutation: true),
        new("process", "process stdin", "stdin", [ProcessId(), Text(ProcessInputField, maximumBytes: null, formOnly: true), Flag("close")],
            PrepareProcessInput, mutation: true),
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
            a => new PreparedForm(AgentWatch(a.Text("agent"), a.Text("task_id"), checked((uint)a.Number("after"))),
                a.IsForm ? new StreamIntent(DecodeAgentEvents: true) : null)),
        new("agent", "cancel", "cancel", [Text("agent"), TaskId()], a => AgentCancel(a.Text("agent"), a.Text("task_id")), mutation: true),
        new("agent", "artifact", "artifact",
            [Text("agent"), TaskId(), Text("name"), LocalPath(), Flag(ReplaceField, formOnly: true), ToolField("offset"), ToolField("identity")],
            PrepareAgentArtifact),
        new("service", "describe", "describe", [Text("after", true)],
            a => new PreparedForm(Describe(a.OptionalText("after")), a.IsForm ? new StreamIntent(UsesServiceControlFrames: true) : null)),
        new("service", "call", "call",
            [Text("service", toolName: "name"), Number("version", 1, ushort.MaxValue), Text("operation"),
                Text("body_hex", maximumBytes: MaximumHostedCallBodyBytes * 2, toolName: "hex"),
                Text(HostedInputHexField, maximumBytes: null, formOnly: true), Text(HostedInputFileField, true, maximumBytes: null, formOnly: true),
                Flag(KeepHostedInputOpenField, formOnly: true)], PrepareHostedCall, mutation: true),
        new("proxy", "forward", "open",
            [Text("host"), Number("port", 1, ushort.MaxValue), Number(LocalPortField, maximum: ushort.MaxValue, formOnly: true), ToolField("idle_ms")],
            a => new PreparedForm(Proxy(a.Text("host"), checked((ushort)a.Number("port")), a.Number("idle_ms", DefaultProxyIdleMilliseconds)),
                a.IsForm ? new ProxyIntent(checked((ushort)a.Number(LocalPortField))) : null), mutation: true),
    ]);

    private static PreparedForm PrepareFileWrite(Arguments values)
    {
        var path = values.Text("path");
        var input = values.IsForm ? Convert.FromHexString(values.Text(FileContentField)) : null;
        var length = input is null ? checked((uint)values.Number("length")) : (uint)input.Length;
        return new(Write(path, length, values.Flag(ReplaceField)),
            input is null ? null : new StreamIntent((session, signal) => SendAndEndAsync(session, input, signal)));
    }

    private static PreparedForm PrepareFileTransfer(Arguments values, bool upload)
    {
        var remote = values.Text("path");
        var local = values.Text(LocalPathField);
        var replace = values.Flag(ReplaceField);
        return new(null, new TransferIntent((client, target, request, progress, signal) => upload
            ? Ma2aFileTransfer.UploadAsync(client, target, remote, local, request, replace, progress, signal)
            : Ma2aFileTransfer.DownloadAsync(client, target, remote, local, request, replace, progress: progress, cancellationToken: signal)));
    }

    private static PreparedForm PrepareAgentArtifact(Arguments values)
    {
        var agent = values.Text("agent");
        var task = values.Text("task_id");
        var name = values.Text("name");
        var body = AgentArtifact(agent, task, name, values.Number("offset"), values.Get("identity") as JsonArray);
        if (!values.IsForm)
            return new(body);
        var local = values.Text(LocalPathField);
        var replace = values.Flag(ReplaceField);
        return new(body, new TransferIntent((client, target, request, progress, signal) =>
            Ma2aFileTransfer.DownloadArtifactAsync(client, target, agent, task, name, local, request, replace,
                progress: progress, cancellationToken: signal)));
    }

    private static PreparedForm PrepareProcessAttach(Arguments values)
    {
        var id = values.Text("process_id");
        var cursor = values.OptionalNumber("cursor");
        var stdin = values.Flag(StdinField);
        return new(ProcessAttach(id, cursor, stdin), values.IsForm ? new TerminalIntent(SupportsResize: false, AllowsInput: stdin) : null);
    }

    private static PreparedForm PrepareProcessInput(Arguments values)
    {
        var body = ProcessStdin(values.Text("process_id"), values.Flag("close"));
        if (!values.IsForm)
            return new(body);
        var input = values.Text(ProcessInputField);
        return new(body, new StreamIntent((session, signal) => SendAndEndAsync(session, Encoding.UTF8.GetBytes(input), signal)));
    }

    private static async Task SendAndEndAsync(Ma2aServiceSession session, ReadOnlyMemory<byte> input, CancellationToken signal)
    {
        await session.SendDataAsync(0, input, signal);
        await session.EndAsync(cancellationToken: signal);
    }

    private static PreparedForm PrepareHostedCall(Arguments values)
    {
        StreamIntent? intent = null;
        if (values.IsForm)
        {
            var path = values.OptionalText(HostedInputFileField);
            var hex = values.Text(HostedInputHexField);
            if (path is not null && string.IsNullOrWhiteSpace(path))
                throw new ArgumentException("Choose a local input file or leave input file null.");
            if (path is not null && hex.Length != 0)
                throw new ArgumentException("Input file and input hex are mutually exclusive.");
            var keepOpen = values.Flag(KeepHostedInputOpenField);
            intent = new StreamIntent((session, signal) => SendHostedInputAsync(session, path, hex, keepOpen, signal),
                UsesServiceControlFrames: true);
        }
        return new(ServiceCall(values.Text("service"), checked((ushort)values.Number("version", 1)),
            values.Text("operation"), Convert.FromHexString(values.Text("body_hex"))), intent);
    }

    private static async Task SendHostedInputAsync(Ma2aServiceSession session, string? path, string hex, bool keepOpen,
        CancellationToken signal)
    {
        if (path is null)
        {
            var bytes = Convert.FromHexString(hex);
            if (bytes.Length > 0)
                await session.SendDataAsync(0, bytes, signal);
        }
        else
        {
            await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
                Ma2aServiceSession.MaximumDataLength, FileOptions.Asynchronous | FileOptions.SequentialScan);
            var buffer = new byte[Ma2aServiceSession.MaximumDataLength];
            int count;
            while ((count = await input.ReadAsync(buffer, signal)) > 0)
                await session.SendDataAsync(0, buffer.AsMemory(0, count), signal);
        }
        if (!keepOpen)
            await session.EndAsync(cancellationToken: signal);
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

    private static Field LocalPath() => Text(LocalPathField, maximumBytes: null, formOnly: true);
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
