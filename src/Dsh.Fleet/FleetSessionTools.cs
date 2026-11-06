using System.Text.Json;
using System.Text.Json.Nodes;
using Dsh.Core;
using Dsh.Llm;
using Dsh.Runtime;
using Dsh.Workspace;

namespace Dsh.Fleet;

/**
 * 让 agent 跨机器办事: 看别的机器上有哪些会话, 再把一句话发给其中某个会话。
 * 连接与会话页用的是同一批机器(fleet 的 ssh/tcp 通道), 首次调用才建连, 插件卸载时一并释放。
 */
public sealed class FleetSessionTools : IDisposable
{
    public const string SessionsTool = "machine_sessions";
    public const string SendTool = "machine_send_message";

    private const int MaxListedSessions = 40;

    private readonly Func<IWorkspaceClient> _connect;
    private readonly IReadOnlyList<IDisposable> _registrations;
    private readonly Lock _gate = new();
    private IWorkspaceClient? _client;
    private bool _disposed;

    public FleetSessionTools(Context ctx, Func<IWorkspaceClient> connect)
    {
        _connect = connect;
        var tools = ctx.Get<ToolRuntime>(ToolRuntime.ServiceName)
            ?? throw new InvalidOperationException("tool runtime is not available");
        _registrations =
        [
            tools.Register(Tool(SessionsTool,
                "列出别的机器上已有的会话; 省略 machine 时列出所有机器及其会话。",
                new JsonObject
                {
                    ["type"] = "object",
                    ["additionalProperties"] = false,
                    ["properties"] = new JsonObject
                    {
                        ["machine"] = new JsonObject { ["type"] = "string", ["description"] = "机器名，可省略" },
                    },
                },
                (args, cancellationToken) => SessionsAsync(Text(args, "machine"), cancellationToken))),
            tools.Register(Tool(SendTool,
                "把一句话发给某台机器上的某个会话; 不填 session 时在那台机器上新开一个会话再发。",
                new JsonObject
                {
                    ["type"] = "object",
                    ["additionalProperties"] = false,
                    ["required"] = new JsonArray("machine", "text"),
                    ["properties"] = new JsonObject
                    {
                        ["machine"] = new JsonObject { ["type"] = "string", ["description"] = "机器名" },
                        ["session"] = new JsonObject { ["type"] = "string", ["description"] = "会话 id，可省略" },
                        ["text"] = new JsonObject { ["type"] = "string", ["description"] = "要发送的消息" },
                        ["cwd"] = new JsonObject { ["type"] = "string", ["description"] = "新会话的工作目录，省略时用那台机器的 home" },
                    },
                },
                (args, cancellationToken) => SendAsync(Text(args, "machine"), Text(args, "session"), Text(args, "text") ?? "", Text(args, "cwd"), cancellationToken))),
        ];
    }

    private async Task<ToolAnswer> SessionsAsync(string? machine, CancellationToken cancellationToken)
    {
        var client = await ClientAsync(cancellationToken).ConfigureAwait(false);
        var targets = machine is { Length: > 0 }
            ? [Find(client, machine)]
            : client.Machines;
        var lines = new List<string>();
        foreach (var target in targets)
        {
            var name = target.Machine.Name;
            try
            {
                var lease = await target.WaitConnectedAsync(cancellationToken).ConfigureAwait(false);
                var sessions = await lease.Client.ListSessionsAsync(cancellationToken).ConfigureAwait(false);
                lines.Add(sessions.Count == 0
                    ? $"{name} · 已连接 · 没有会话"
                    : $"{name} · 已连接 · {sessions.Count} 个会话");
                lines.AddRange(sessions
                    .OrderByDescending(session => session.LastActivityAt)
                    .Take(MaxListedSessions)
                    .Select(session => $"  {session.SessionId} · {session.Title ?? session.Cwd} · {session.Status}"));
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                lines.Add($"{name} · 不可用 · {error.Message}");
            }
        }
        return ToolAnswer.Success(string.Join('\n', lines));
    }

    private async Task<ToolAnswer> SendAsync(string? machine, string? sessionId, string text, string? cwd, CancellationToken cancellationToken)
    {
        if (machine is not { Length: > 0 })
            return ToolAnswer.Failure("需要 machine 参数");
        if (text.Length == 0)
            return ToolAnswer.Failure("需要要发送的 text");
        var client = await ClientAsync(cancellationToken).ConfigureAwait(false);
        var target = Find(client, machine);
        var lease = await target.WaitConnectedAsync(cancellationToken).ConfigureAwait(false);
        var session = sessionId is { Length: > 0 }
            ? sessionId
            : (await lease.Client.CreateSessionAsync(cwd is { Length: > 0 } ? cwd : target.Info?.HomeRoot ?? "", cancellationToken).ConfigureAwait(false)).SessionId;
        await lease.Client.SendMessageAsync(session, text, [], cancellationToken).ConfigureAwait(false);
        return ToolAnswer.Success($"已发送到 {target.Machine.Name}/{session}");
    }

    private static IWorkspaceMachine Find(IWorkspaceClient client, string machine)
        => client.Find(machine) ?? throw new InvalidOperationException($"没有这台机器: {machine}; 先用 {SessionsTool} 看看有哪些机器");

    private async Task<IWorkspaceClient> ClientAsync(CancellationToken cancellationToken)
    {
        IWorkspaceClient client;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            client = _client ??= Start();
        }
        await client.WaitSettledAsync(cancellationToken).ConfigureAwait(false);
        return client;
    }

    private IWorkspaceClient Start()
    {
        var client = _connect();
        client.Start();
        return client;
    }

    private static ToolDefinition Tool(string name, string description, JsonObject parameters, Func<JsonElement, CancellationToken, Task<ToolAnswer>> run)
        => new()
        {
            Name = name,
            Description = description,
            Parameters = parameters,
            Output = new ToolOutputDefinition(
                new JsonObject
                {
                    ["type"] = "object",
                    ["additionalProperties"] = false,
                    ["required"] = new JsonArray("ok", "text"),
                    ["properties"] = new JsonObject
                    {
                        ["ok"] = new JsonObject { ["type"] = "boolean", ["description"] = "调用是否成功" },
                        ["text"] = new JsonObject { ["type"] = "string", ["description"] = "给模型看的结果文本" },
                    },
                },
                (_, value) => [new TextBlock(value.GetProperty("text").GetString() ?? "")]),
            Execute = async (args, context) =>
            {
                ToolAnswer answer;
                try
                {
                    answer = await run(args, context.Signal).ConfigureAwait(false);
                }
                catch (Exception error) when (error is not OperationCanceledException)
                {
                    answer = ToolAnswer.Failure(error.Message);
                }
                return new JsonObject { ["ok"] = answer.Succeeded, ["text"] = answer.Text };
            },
        };

    private static string? Text(JsonElement args, string name)
        => args.TryGetProperty(name, out var value) ? value.GetString() : null;

    public void Dispose()
    {
        IWorkspaceClient? client;
        lock (_gate)
        {
            if (_disposed)
                return;
            _disposed = true;
            client = _client;
            _client = null;
        }
        foreach (var registration in _registrations)
            registration.Dispose();
        if (client is not null)
            client.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }

    private sealed record ToolAnswer(bool Succeeded, string Text)
    {
        public static ToolAnswer Success(string text) => new(true, text);
        public static ToolAnswer Failure(string text) => new(false, text);
    }
}
