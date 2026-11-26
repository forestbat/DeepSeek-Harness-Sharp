using System.Text.Json.Nodes;
using Dsh.Ma2a.Services;

namespace Dsh.Ma2a.Tools;

public static class Ma2aToolRequests
{
    private static readonly Dictionary<string, Func<Arguments, JsonArray>> Builders = new(StringComparer.Ordinal)
    {
        ["files.stat"] = a => Ma2aServiceRequests.Stat(a.Text("path")),
        ["files.list"] = a => Ma2aServiceRequests.List(a.Text("path"), a.Optional("after")),
        ["files.mkdir"] = a => Ma2aServiceRequests.Mkdir(a.Text("path"), a.Flag("parents")),
        ["files.read"] = a => Ma2aServiceRequests.Read(a.Text("path"), a.Number("offset", 0),
            checked((uint)a.Number("length", Ma2aServiceRequests.MaximumInlineFileBytes))),
        ["files.write"] = a => Ma2aServiceRequests.Write(a.Text("path"), checked((uint)a.Number("length", 0)), a.Flag("replace")),
        ["files.rename"] = a => Ma2aServiceRequests.Rename(a.Text("path"), a.Text("destination"), a.Flag("replace")),
        ["files.remove"] = a => Ma2aServiceRequests.Remove(a.Text("path"), a.Flag("recursive")),
        ["exec.run"] = a => Ma2aServiceRequests.Exec(a.Text("program"), a.Args(), a.Optional("cwd"), a.Flag("stdin"),
            a.Number("timeout_ms", Ma2aServiceRequests.DefaultExecTimeoutMilliseconds)),
        ["shell.open"] = a => Ma2aServiceRequests.Shell(a.Optional("program"), a.Args(), a.Optional("cwd"),
            checked((ushort)a.Number("columns", Ma2aServiceRequests.DefaultShellColumns)),
            checked((ushort)a.Number("rows", Ma2aServiceRequests.DefaultShellRows)),
            a.Number("lifetime_ms", Ma2aServiceRequests.MaximumLifetimeMilliseconds)),
        ["process.start"] = a => Ma2aServiceRequests.ProcessStart(a.Text("program"), a.Args(), a.Optional("cwd"), a.Flag("stdin"), a.Optional("name")),
        ["process.list"] = a => Ma2aServiceRequests.Processes(a.Optional("after")),
        ["process.inspect"] = a => Ma2aServiceRequests.ProcessInspect(a.Text("id")),
        ["process.logs"] = a => Ma2aServiceRequests.ProcessLogs(a.Text("id"), a.Values["cursor"]?.GetValue<ulong>()),
        ["process.attach"] = a => Ma2aServiceRequests.ProcessAttach(a.Text("id"), a.Values["cursor"]?.GetValue<ulong>(), a.Flag("stdin")),
        ["process.stdin"] = a => Ma2aServiceRequests.ProcessStdin(a.Text("id"), a.Flag("close")),
        ["process.signal"] = a => Ma2aServiceRequests.ProcessSignal(a.Text("id"), checked((byte)a.Number("signal", 0))),
        ["process.terminate"] = a => Ma2aServiceRequests.ProcessTerminate(a.Text("id"),
            checked((uint)a.Number("grace_ms", Ma2aServiceRequests.DefaultProcessGraceMilliseconds))),
        ["process.delete"] = a => Ma2aServiceRequests.ProcessDelete(a.Text("id")),
        ["agent.list"] = a => Ma2aServiceRequests.Agents(a.Optional("after")),
        ["agent.submit"] = a => Ma2aServiceRequests.AgentSubmit(a.Text("agent"), a.Text("text"), a.Optional("from_agent")),
        ["agent.send"] = a => Ma2aServiceRequests.AgentSend(a.Text("agent"), a.Text("task"), a.Text("text")),
        ["agent.get"] = a => Ma2aServiceRequests.AgentGet(a.Text("agent"), a.Text("task")),
        ["agent.tasks"] = a => Ma2aServiceRequests.AgentTasks(a.Text("agent"), a.Optional("after")),
        ["agent.watch"] = a => Ma2aServiceRequests.AgentWatch(a.Text("agent"), a.Text("task"), checked((uint)a.Number("after", 0))),
        ["agent.cancel"] = a => Ma2aServiceRequests.AgentCancel(a.Text("agent"), a.Text("task")),
        ["agent.artifact"] = a => Ma2aServiceRequests.AgentArtifact(a.Text("agent"), a.Text("task"), a.Text("name"), a.Number("offset", 0), a.Values["identity"] as JsonArray),
        ["service.describe"] = a => Ma2aServiceRequests.Describe(a.Optional("after")),
        ["service.call"] = a => Ma2aServiceRequests.ServiceCall(a.Text("name"), checked((ushort)a.Number("version", 1)), a.Text("operation"), Convert.FromHexString(a.Text("hex"))),
        ["proxy.open"] = a => Ma2aServiceRequests.Proxy(a.Text("host"), checked((ushort)a.Number("port", 0)),
            a.Number("idle_ms", Ma2aServiceRequests.DefaultProxyIdleMilliseconds)),
    };

    public static readonly string[] Operations = [.. Builders.Keys];

    public static JsonObject Prepare(string operation, JsonObject arguments)
    {
        if (!Builders.TryGetValue(operation, out var build))
            throw new ArgumentException("Unknown native MA2A service operation.", nameof(operation));
        return new JsonObject { ["service"] = operation[..operation.IndexOf('.')], ["body"] = build(new Arguments(arguments)) };
    }

    private readonly record struct Arguments(JsonObject Values)
    {
        public string Text(string name) => Optional(name) ?? throw new ArgumentException($"{name} is required.");
        public string? Optional(string name) => Values[name]?.GetValue<string>();
        public bool Flag(string name) => Values[name]?.GetValue<bool>() ?? false;
        public ulong Number(string name, ulong fallback) => Values[name]?.GetValue<ulong>() ?? fallback;
        public string[] Args() => Values["args"] is JsonArray items ? items.Select(item => item!.GetValue<string>()).ToArray() : [];
    }
}
