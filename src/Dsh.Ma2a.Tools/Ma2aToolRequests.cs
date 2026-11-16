using System.Text.Json.Nodes;
using Dsh.Ma2a.Services;

namespace Dsh.Ma2a.Tools;

public static class Ma2aToolRequests
{
    public static readonly string[] Operations =
    [
        "files.stat", "files.list", "files.mkdir", "files.read", "files.write", "files.rename", "files.remove",
        "exec.run", "shell.open", "process.start", "process.list", "process.inspect", "process.logs",
        "process.attach", "process.stdin", "process.signal", "process.terminate", "process.delete",
        "agent.list", "agent.submit", "agent.send", "agent.get", "agent.tasks", "agent.watch", "agent.cancel",
        "agent.artifact", "service.describe", "service.call", "proxy.open",
    ];

    public static JsonObject Prepare(string operation, JsonObject arguments)
    {
        string Text(string name) => arguments[name]?.GetValue<string>() ?? throw new ArgumentException($"{name} is required.");
        string? Optional(string name) => arguments[name]?.GetValue<string>();
        bool Flag(string name) => arguments[name]?.GetValue<bool>() ?? false;
        ulong Number(string name, ulong fallback) => arguments[name]?.GetValue<ulong>() ?? fallback;
        string[] Args() => arguments["args"] is JsonArray items ? items.Select(item => item!.GetValue<string>()).ToArray() : [];
        var body = operation switch
        {
            "files.stat" => Ma2aServiceRequests.Stat(Text("path")),
            "files.list" => Ma2aServiceRequests.List(Text("path"), Optional("after")),
            "files.mkdir" => Ma2aServiceRequests.Mkdir(Text("path"), Flag("parents")),
            "files.read" => Ma2aServiceRequests.Read(Text("path"), Number("offset", 0), checked((uint)Number("length", 65_536))),
            "files.write" => Ma2aServiceRequests.Write(Text("path"), checked((uint)Number("length", 0)), Flag("replace")),
            "files.rename" => Ma2aServiceRequests.Rename(Text("path"), Text("destination"), Flag("replace")),
            "files.remove" => Ma2aServiceRequests.Remove(Text("path"), Flag("recursive")),
            "exec.run" => Ma2aServiceRequests.Exec(Text("program"), Args(), Optional("cwd"), Flag("stdin"), Number("timeout_ms", 60_000)),
            "shell.open" => Ma2aServiceRequests.Shell(Optional("program"), Args(), Optional("cwd"),
                checked((ushort)Number("columns", 80)), checked((ushort)Number("rows", 24)), Number("lifetime_ms", Ma2aServiceRequests.MaximumLifetimeMilliseconds)),
            "process.start" => Ma2aServiceRequests.ProcessStart(Text("program"), Args(), Optional("cwd"), Flag("stdin"), Optional("name")),
            "process.list" => Ma2aServiceRequests.Processes(Optional("after")),
            "process.inspect" => Ma2aServiceRequests.ProcessInspect(Text("id")),
            "process.logs" => Ma2aServiceRequests.ProcessLogs(Text("id"), arguments["cursor"]?.GetValue<ulong>()),
            "process.attach" => Ma2aServiceRequests.ProcessAttach(Text("id"), arguments["cursor"]?.GetValue<ulong>(), Flag("stdin")),
            "process.stdin" => Ma2aServiceRequests.ProcessStdin(Text("id"), Flag("close")),
            "process.signal" => Ma2aServiceRequests.ProcessSignal(Text("id"), checked((byte)Number("signal", 0))),
            "process.terminate" => Ma2aServiceRequests.ProcessTerminate(Text("id"), checked((uint)Number("grace_ms", 5_000))),
            "process.delete" => Ma2aServiceRequests.ProcessDelete(Text("id")),
            "agent.list" => Ma2aServiceRequests.Agents(Optional("after")),
            "agent.submit" => Ma2aServiceRequests.AgentSubmit(Text("agent"), Text("text"), Optional("from_agent")),
            "agent.send" => Ma2aServiceRequests.AgentSend(Text("agent"), Text("task"), Text("text")),
            "agent.get" => Ma2aServiceRequests.AgentGet(Text("agent"), Text("task")),
            "agent.tasks" => Ma2aServiceRequests.AgentTasks(Text("agent"), Optional("after")),
            "agent.watch" => Ma2aServiceRequests.AgentWatch(Text("agent"), Text("task"), checked((uint)Number("after", 0))),
            "agent.cancel" => Ma2aServiceRequests.AgentCancel(Text("agent"), Text("task")),
            "agent.artifact" => Ma2aServiceRequests.AgentArtifact(Text("agent"), Text("task"), Text("name"), Number("offset", 0), arguments["identity"] as JsonArray),
            "service.describe" => Ma2aServiceRequests.Describe(Optional("after")),
            "service.call" => Ma2aServiceRequests.ServiceCall(Text("name"), checked((ushort)Number("version", 1)), Text("operation"), Convert.FromHexString(Text("hex"))),
            "proxy.open" => Ma2aServiceRequests.Proxy(Text("host"), checked((ushort)Number("port", 0)), Number("idle_ms", 60_000)),
            _ => throw new ArgumentException("Unknown native MA2A service operation.", nameof(operation)),
        };
        return new JsonObject { ["service"] = operation[..operation.IndexOf('.')], ["body"] = body };
    }
}
