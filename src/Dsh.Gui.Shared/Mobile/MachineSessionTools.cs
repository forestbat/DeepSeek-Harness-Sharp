using System.Text.Json;
using System.Text.Json.Nodes;
using Dsh.Core;
using Dsh.RemoteHost;
using Dsh.Runtime;

namespace Dsh.Gui.Shared.Mobile;

/**
 * 手机 agent 主动找别的机器办事: 先看那边的会话, 再把一句话发给其中某个会话。
 * 与界面共用同一批已连好的机器链路(手机上是 SSH.NET), 因此手机上不依赖外部 ssh 程序。
 */
public static class MachineSessionTools
{
    public const string SessionsTool = "desktop_sessions";
    public const string SendTool = "desktop_send_message";

    private const int MaxListedSessions = 40;

    public static IDisposable Register(Context ctx, Func<IReadOnlyList<IAgentMachineLink>> machines)
    {
        var tools = ctx.Get<ToolRuntime>(ToolRuntime.ServiceName)
            ?? throw new InvalidOperationException("tool runtime is not available");
        return new Registrations(
        [
            tools.Register(MobileTool.Create(SessionsTool,
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
                (args, cancellationToken) => SessionsAsync(machines, MobileTool.Text(args, "machine"), cancellationToken))),
            tools.Register(MobileTool.Create(SendTool,
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
                (args, cancellationToken) => SendAsync(machines, MobileTool.Text(args, "machine"), MobileTool.Text(args, "session"), MobileTool.Text(args, "text") ?? "", MobileTool.Text(args, "cwd"), cancellationToken))),
        ]);
    }

    private static Task<MobileToolAnswer> SessionsAsync(Func<IReadOnlyList<IAgentMachineLink>> machines, string? machine, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var targets = machine is { Length: > 0 }
            ? [Find(machines, machine)]
            : machines();
        var lines = new List<string>();
        foreach (var target in targets)
        {
            if (!target.IsConnected)
            {
                lines.Add(target.Detail is { Length: > 0 } detail
                    ? $"{target.Name} · 未连接 · {detail}"
                    : $"{target.Name} · 未连接");
                continue;
            }
            var sessions = target.Sessions;
            lines.Add(sessions.Count == 0
                ? $"{target.Name} · 已连接 · 没有会话"
                : $"{target.Name} · 已连接 · {sessions.Count} 个会话");
            lines.AddRange(sessions
                .OrderByDescending(session => session.LastActivityAt)
                .Take(MaxListedSessions)
                .Select(session => $"  {session.SessionId} · {session.Title ?? session.Cwd} · {session.Status}"));
        }
        return Task.FromResult(lines.Count == 0
            ? MobileToolAnswer.Failure("没有可用的机器")
            : MobileToolAnswer.Success(string.Join('\n', lines)));
    }

    private static async Task<MobileToolAnswer> SendAsync(Func<IReadOnlyList<IAgentMachineLink>> machines, string? machine, string? sessionId, string text, string? cwd, CancellationToken cancellationToken)
    {
        if (machine is not { Length: > 0 })
            return MobileToolAnswer.Failure("需要 machine 参数");
        if (text.Length == 0)
            return MobileToolAnswer.Failure("需要要发送的 text");
        var target = Find(machines, machine);
        var session = sessionId is { Length: > 0 }
            ? sessionId
            : (await target.CreateSessionAsync(cwd is { Length: > 0 } ? cwd : "", cancellationToken).ConfigureAwait(false)).SessionId;
        await target.SendMessageAsync(session, text, cancellationToken).ConfigureAwait(false);
        return MobileToolAnswer.Success($"已发送到 {target.Name}/{session}");
    }

    private static IAgentMachineLink Find(Func<IReadOnlyList<IAgentMachineLink>> machines, string machine)
        => machines().FirstOrDefault(link => string.Equals(link.Name, machine, StringComparison.Ordinal))
            ?? throw new InvalidOperationException($"没有这台机器: {machine}; 先用 {SessionsTool} 看看有哪些机器");

    private sealed class Registrations(IReadOnlyList<IDisposable> items) : IDisposable
    {
        public void Dispose()
        {
            foreach (var item in items)
                item.Dispose();
        }
    }
}
