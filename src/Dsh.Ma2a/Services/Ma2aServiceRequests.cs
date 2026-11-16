using System.Text;
using System.Text.Json.Nodes;

namespace Dsh.Ma2a.Services;

/// <summary>Native wire requests shared by GUI forms and model tools.</summary>
public static class Ma2aServiceRequests
{
    public const uint DefaultLogLimit = 4_194_304;
    public const ulong MaximumLifetimeMilliseconds = 86_400_000;

    public static JsonArray Path(string path)
    {
        var separator = path.IndexOf(":/", StringComparison.Ordinal);
        if (separator < 1) throw new ArgumentException("Expected ROOT:/relative/path.", nameof(path));
        var root = path[..separator];
        if (root.Length > 64 || !IsRootName(root)) throw new ArgumentException("Invalid MA2A root name.", nameof(path));
        var suffix = path[(separator + 2)..];
        var components = suffix.Length == 0 ? [] : suffix.Split('/');
        if (components.Length > 64 || Encoding.UTF8.GetByteCount(path) > 4096 || components.Any(InvalidComponent))
            throw new ArgumentException("Invalid MA2A root-relative path.", nameof(path));
        return new JsonArray(root, new JsonArray(components.Select(component => JsonValue.Create(component)).ToArray()));
    }

    private static bool IsRootName(string root)
        => root[0] is >= 'a' and <= 'z' or >= '0' and <= '9'
            && root.All(character => character is >= 'a' and <= 'z' or >= '0' and <= '9' or '.' or '_' or '-');

    private static bool InvalidComponent(string component)
        => component is "" or "." or ".." || Encoding.UTF8.GetByteCount(component) > 255
            || component.Any(character => char.IsControl(character) || character is '/' or '\\' or ':');

    public static JsonArray Stat(string path) => new(0, Path(path));
    public static JsonArray List(string path, string? after = null) => new(1, Path(path), after);
    public static JsonArray Mkdir(string path, bool parents = false) => new(2, Path(path), parents);
    public static JsonArray Read(string path, ulong offset = 0, uint length = 65_536)
        => length <= 65_536 ? new(3, Path(path), offset, length) : throw new ArgumentOutOfRangeException(nameof(length));
    public static JsonArray Write(string path, uint length, bool replace = false)
        => length <= 65_536 ? new(4, Path(path), replace ? 1 : 0, length) : throw new ArgumentOutOfRangeException(nameof(length));
    public static JsonArray Rename(string from, string to, bool replace = false) => new(5, Path(from), Path(to), replace);
    public static JsonArray Remove(string path, bool recursive = false) => new(6, Path(path), recursive);

    public static JsonArray Download(string path, ulong offset = 0, JsonArray? identity = null)
    {
        if (offset > 0 && identity is null) throw new ArgumentException("Resuming requires the source content identity.", nameof(identity));
        return new JsonArray(0, new JsonArray(0, Path(path)), offset, identity?.DeepClone());
    }

    public static JsonArray Upload(string path, ulong size, bool replace = false)
        => new(1, new JsonArray(0, Path(path)), size, replace ? 1 : 0);

    public static JsonArray Program(string? program, IReadOnlyList<string>? args = null, string? cwd = null)
    {
        if (program is not null) ValidateText(program);
        if (cwd is not null) ValidateText(cwd);
        args ??= [];
        if (args.Count > 256) throw new ArgumentOutOfRangeException(nameof(args));
        foreach (var argument in args) ValidateText(argument);
        return new JsonArray(program, new JsonArray(args.Select(argument => JsonValue.Create(argument)).ToArray()), cwd, new JsonArray());
    }

    private static void ValidateText(string value)
    {
        if (Encoding.UTF8.GetByteCount(value) > 4096 || value.Contains('\0'))
            throw new ArgumentException("Program text exceeds its bound or contains NUL.", nameof(value));
    }

    public static JsonArray Exec(string program, IReadOnlyList<string>? args = null, string? cwd = null,
        bool stdin = false, ulong timeoutMilliseconds = 60_000)
    {
        ValidateLifetime(timeoutMilliseconds);
        return new JsonArray(Program(program, args, cwd), stdin, timeoutMilliseconds);
    }

    public static JsonArray Shell(string? program = null, IReadOnlyList<string>? args = null, string? cwd = null,
        ushort columns = 80, ushort rows = 24, ulong lifetimeMilliseconds = MaximumLifetimeMilliseconds)
    {
        ValidateLifetime(lifetimeMilliseconds);
        if (columns == 0 || rows == 0) throw new ArgumentOutOfRangeException(nameof(columns));
        return new JsonArray(Program(program, args, cwd), "xterm-256color", new JsonArray(columns, rows), lifetimeMilliseconds);
    }

    private static void ValidateLifetime(ulong milliseconds)
    {
        if (milliseconds is 0 or > MaximumLifetimeMilliseconds) throw new ArgumentOutOfRangeException(nameof(milliseconds));
    }

    public static JsonArray Resize(ushort columns, ushort rows)
        => columns != 0 && rows != 0 ? new(0, new JsonArray(columns, rows)) : throw new ArgumentOutOfRangeException(nameof(columns));

    public static JsonArray Proxy(string host, ushort port, ulong idleMilliseconds = 60_000)
        => port != 0 && idleMilliseconds is > 0 and <= 3_600_000
            ? new(host, port, idleMilliseconds) : throw new ArgumentOutOfRangeException(nameof(port));

    public static JsonArray ProcessStart(string program, IReadOnlyList<string>? args = null, string? cwd = null,
        bool stdin = false, string? name = null, uint logLimit = DefaultLogLimit)
    {
        if (logLimit is < 1_048_576 or > 16_777_216) throw new ArgumentOutOfRangeException(nameof(logLimit));
        return new JsonArray(0, new JsonArray(Program(program, args, cwd), stdin, logLimit, name, new JsonArray()));
    }

    public static JsonArray Processes(string? after = null) => new(1, OptionalId(after));
    public static JsonArray ProcessInspect(string id) => new(2, Id(id));
    public static JsonArray ProcessLogs(string id, ulong? cursor = null, uint maximumBytes = 1_048_576)
        => maximumBytes is > 0 and <= 1_048_576 ? new(3, Id(id), LogPosition(cursor), maximumBytes) : throw new ArgumentOutOfRangeException(nameof(maximumBytes));
    public static JsonArray ProcessAttach(string id, ulong? cursor = null, bool stdin = false) => new(4, Id(id), LogPosition(cursor), stdin);
    public static JsonArray ProcessStdin(string id, bool close = false) => new(5, Id(id), close);
    public static JsonArray ProcessSignal(string id, byte signal)
        => signal <= 6 ? new(6, Id(id), signal) : throw new ArgumentOutOfRangeException(nameof(signal));
    public static JsonArray ProcessTerminate(string id, uint graceMilliseconds = 5_000)
        => graceMilliseconds <= 60_000 ? new(7, Id(id), graceMilliseconds) : throw new ArgumentOutOfRangeException(nameof(graceMilliseconds));
    public static JsonArray ProcessDelete(string id) => new(8, Id(id));
    public static JsonArray LogPosition(ulong? cursor = null) => cursor is { } value ? new(2, value) : new(0);
    public static JsonArray LogNow() => new(1);

    public static JsonArray Agents(string? after = null) => new(0, after);
    public static JsonArray AgentMessage(string text) => new("user", new JsonArray(new JsonArray(0, text)));
    public static JsonArray AgentSubmit(string agent, string text, string? fromAgent = null) => new(1, new JsonArray(agent, AgentMessage(text), fromAgent));
    public static JsonArray AgentSend(string agent, string task, string text) => new(2, agent, Id(task), AgentMessage(text));
    public static JsonArray AgentGet(string agent, string task) => new(3, agent, Id(task));
    public static JsonArray AgentTasks(string agent, string? after = null) => new(4, agent, OptionalId(after));
    public static JsonArray AgentWatch(string agent, string task, uint after = 0) => new(5, agent, Id(task), after);
    public static JsonArray AgentCancel(string agent, string task) => new(6, agent, Id(task));
    public static JsonArray AgentArtifact(string agent, string task, string name, ulong offset = 0, JsonArray? identity = null)
        => offset == 0 || identity is not null ? new(7, agent, Id(task), name, offset, identity?.DeepClone())
            : throw new ArgumentException("Resuming requires the source content identity.", nameof(identity));

    public static JsonArray Describe(string? after = null) => new(0, after);
    public static JsonArray ServiceCall(string name, ushort version, string operation, ReadOnlySpan<byte> body)
        => version != 0 && body.Length <= 8192 ? new(1, name, version, operation, Ma2aCbor.Bytes(body))
            : throw new ArgumentException("Service calls need a nonzero version and at most 8 KiB of inline bytes.");

    private static JsonObject Id(string id) => Ma2aCbor.Bytes(Ma2aServiceSession.DecodeIdentifier(id, 16));
    private static JsonObject? OptionalId(string? id) => id is null ? null : Id(id);
}
