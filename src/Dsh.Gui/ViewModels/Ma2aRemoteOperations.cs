using System.Text.Json;
using System.Text.Json.Nodes;
using Dsh.Ma2a.Services;

namespace Dsh.Gui.ViewModels;

public sealed record Ma2aRemoteOperation(string Family, string Name, IReadOnlyList<Ma2aFieldViewModel> Fields,
    Func<IReadOnlyDictionary<string, JsonNode?>, JsonNode>? Build)
{
    public string Label => $"{Family} / {Name}";
    public bool IsTerminal => Family is "shell" or "exec" || Name == "process attach";
    public bool IsMutation => Family is "exec" or "shell" or "proxy"
        || Name is "mkdir" or "write" or "rename" or "remove" or "upload" or "process start" or "process attach"
            or "process stdin" or "process signal" or "process terminate" or "process delete" or "submit" or "send" or "cancel" or "call";

    public static IReadOnlyList<Ma2aRemoteOperation> Create() =>
    [
        new("files", "list", [PathField("path"), Text("after", true)], p => Ma2aServiceRequests.List(S(p, "path"), O(p, "after"))),
        new("files", "stat", [PathField("path")], p => Ma2aServiceRequests.Stat(S(p, "path"))),
        new("files", "mkdir", [PathField("path"), Flag("parents")], p => Ma2aServiceRequests.Mkdir(S(p, "path"), B(p, "parents"))),
        new("files", "read", [PathField("path"), Number("offset"), Number("length", 0, 65_536, 65_536)], p => Ma2aServiceRequests.Read(S(p, "path"), U(p, "offset"), (uint)U(p, "length"))),
        new("files", "write", [PathField("path"), Field("content_hex", new JsonObject { ["type"] = "string", ["max_bytes"] = Ma2aServiceSession.MaximumDataLength * 2 }), Flag("replace")], p => Ma2aServiceRequests.Write(S(p, "path"), (uint)Convert.FromHexString(S(p, "content_hex")).Length, B(p, "replace"))),
        new("files", "rename", [PathField("from"), PathField("to"), Flag("replace")], p => Ma2aServiceRequests.Rename(S(p, "from"), S(p, "to"), B(p, "replace"))),
        new("files", "remove", [PathField("path"), Flag("recursive")], p => Ma2aServiceRequests.Remove(S(p, "path"), B(p, "recursive"))),
        new("transfer", "download", [PathField("path"), Text("local_path", maximumBytes: null), Flag("replace")], null),
        new("transfer", "upload", [PathField("path"), Text("local_path", maximumBytes: null), Flag("replace")], null),
        new("exec", "exec", ProgramFields(), p => Ma2aServiceRequests.Exec(S(p, "program"), Args(p), O(p, "cwd"), true)),
        new("shell", "shell", ProgramFields(true), p => Ma2aServiceRequests.Shell(O(p, "program"), Args(p), O(p, "cwd"))),
        new("process", "process start", [.. ProgramFields(), Flag("stdin"), Text("name", true)], p => Ma2aServiceRequests.ProcessStart(S(p, "program"), Args(p), O(p, "cwd"), B(p, "stdin"), O(p, "name"))),
        new("process", "process list", [Text("after", true)], p => Ma2aServiceRequests.Processes(O(p, "after"))),
        new("process", "process inspect", [Text("process_id")], p => Ma2aServiceRequests.ProcessInspect(S(p, "process_id"))),
        new("process", "process logs", [Text("process_id"), Number("cursor")], p => Ma2aServiceRequests.ProcessLogs(S(p, "process_id"), U(p, "cursor"))),
        new("process", "process attach", [Text("process_id"), Flag("stdin")], p => Ma2aServiceRequests.ProcessAttach(S(p, "process_id"), stdin: B(p, "stdin"))),
        new("process", "process stdin", [Text("process_id"), Text("input", maximumBytes: null), Flag("close")], p => Ma2aServiceRequests.ProcessStdin(S(p, "process_id"), B(p, "close"))),
        new("process", "process signal", [Text("process_id"), Number("signal", 0, 6)], p => Ma2aServiceRequests.ProcessSignal(S(p, "process_id"), (byte)U(p, "signal"))),
        new("process", "process terminate", [Text("process_id"), Number("grace_ms", 0, 60_000, 5_000)], p => Ma2aServiceRequests.ProcessTerminate(S(p, "process_id"), (uint)U(p, "grace_ms"))),
        new("process", "process delete", [Text("process_id")], p => Ma2aServiceRequests.ProcessDelete(S(p, "process_id"))),
        new("agent", "discover", [Text("after", true)], p => Ma2aServiceRequests.Agents(O(p, "after"))),
        new("agent", "submit", [Text("agent"), Text("message", maximumBytes: Ma2aServiceRequests.MaximumAgentMessageBytes)], p => Ma2aServiceRequests.AgentSubmit(S(p, "agent"), S(p, "message"))),
        new("agent", "send", [Text("agent"), Text("task_id"), Text("message", maximumBytes: Ma2aServiceRequests.MaximumAgentMessageBytes)], p => Ma2aServiceRequests.AgentSend(S(p, "agent"), S(p, "task_id"), S(p, "message"))),
        new("agent", "get", [Text("agent"), Text("task_id")], p => Ma2aServiceRequests.AgentGet(S(p, "agent"), S(p, "task_id"))),
        new("agent", "tasks", [Text("agent"), Text("after", true)], p => Ma2aServiceRequests.AgentTasks(S(p, "agent"), O(p, "after"))),
        new("agent", "watch", [Text("agent"), Text("task_id"), Number("after", 0, uint.MaxValue)], p => Ma2aServiceRequests.AgentWatch(S(p, "agent"), S(p, "task_id"), (uint)U(p, "after"))),
        new("agent", "cancel", [Text("agent"), Text("task_id")], p => Ma2aServiceRequests.AgentCancel(S(p, "agent"), S(p, "task_id"))),
        new("agent", "artifact", [Text("agent"), Text("task_id"), Text("name"), Text("local_path", maximumBytes: null), Flag("replace")], p => Ma2aServiceRequests.AgentArtifact(S(p, "agent"), S(p, "task_id"), S(p, "name"))),
        new("service", "describe", [Text("after", true)], p => Ma2aServiceRequests.Describe(O(p, "after"))),
        new("service", "call", [Text("service"), Number("version", 1, ushort.MaxValue), Text("operation"), Text("body_hex", maximumBytes: Ma2aServiceRequests.MaximumHostedCallBodyBytes * 2), Text("input_hex", maximumBytes: null), Text("input_file", true, maximumBytes: null), Flag("keep_input_open")], BuildHostedCall),
        new("proxy", "forward", [Text("host"), Number("port", 1, ushort.MaxValue), Number("local_port", 0, ushort.MaxValue)], p => Ma2aServiceRequests.Proxy(S(p, "host"), (ushort)U(p, "port"))),
    ];

    public static string S(IReadOnlyDictionary<string, JsonNode?> values, string name) => values[name]?.GetValue<string>() ?? "";
    public static string? O(IReadOnlyDictionary<string, JsonNode?> values, string name) => values[name]?.GetValue<string>();
    public static bool B(IReadOnlyDictionary<string, JsonNode?> values, string name) => values[name]?.GetValue<bool>() == true;
    public static ulong U(IReadOnlyDictionary<string, JsonNode?> values, string name) => values[name]!.GetValue<ulong>();
    private static string[] Args(IReadOnlyDictionary<string, JsonNode?> values) => values["args"]!.AsArray().Select(value => value!.GetValue<string>()).ToArray();

    private static JsonNode BuildHostedCall(IReadOnlyDictionary<string, JsonNode?> values)
    {
        var path = O(values, "input_file");
        if (path is not null && string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("Choose a local input file or leave input file null.");
        if (path is not null && S(values, "input_hex").Length != 0)
            throw new ArgumentException("Input file and input hex are mutually exclusive.");
        return Ma2aServiceRequests.ServiceCall(S(values, "service"), (ushort)U(values, "version"),
            S(values, "operation"), Convert.FromHexString(S(values, "body_hex")));
    }

    private static Ma2aFieldViewModel Text(string name, bool nullable = false, int? maximumBytes = 4096)
    {
        JsonNode schema = new JsonObject { ["type"] = "string" };
        if (maximumBytes is { } maximum) schema["max_bytes"] = maximum;
        if (nullable) schema = new JsonObject { ["nullable"] = schema };
        return Field(name, schema);
    }

    private static Ma2aFieldViewModel PathField(string name)
        => Text(name, maximumBytes: Ma2aServiceRequests.MaximumRemotePathTextBytes);

    private static Ma2aFieldViewModel Flag(string name) => Field(name, new JsonObject { ["type"] = "boolean" });

    private static Ma2aFieldViewModel Number(string name, ulong minimum = 0, ulong maximum = ulong.MaxValue, ulong? value = null)
    {
        var field = Field(name, new JsonObject { ["type"] = "integer", ["minimum"] = minimum, ["maximum"] = maximum });
        field.Number = value ?? minimum;
        return field;
    }

    private static Ma2aFieldViewModel Field(string name, JsonNode schema)
    {
        using var document = JsonDocument.Parse(schema.ToJsonString());
        return new Ma2aFieldViewModel(name, document.RootElement.Clone());
    }

    private static IReadOnlyList<Ma2aFieldViewModel> ProgramFields(bool optionalProgram = false) =>
    [
        Text("program", optionalProgram),
        Field("args", new JsonObject { ["type"] = "array", ["max_items"] = 256, ["items"] = new JsonObject { ["type"] = "string", ["max_bytes"] = 4096 } }),
        Text("cwd", true),
    ];
}
