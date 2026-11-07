using Dsh.Boot;
using Dsh.Core;
using Dsh.Interaction;
using Dsh.Llm;
using Dsh.Runtime;

namespace Dsh.SessionGraph;

/**
 * `/timestamp`: 会话时间线。无参数列出用户自己发的消息; 指定序号后可跳转、回退或分叉。
 *
 * 它与 TUI 的同名本地命令共用 `SessionTimeline` 的候选规则, 因此两边看到的是同一份时间线;
 * 注册成会话命令之后 GUI 也能经 host 直接执行(jump 在客户端做, revert/fork 在服务端生效)。
 */
public static class TimestampCommand
{
    public const string Name = "timestamp";

    public static IDisposable Register(Context ctx)
    {
        var commands = ctx.Get<CommandsService>(CommandsService.ServiceName)
            ?? throw new InvalidOperationException("the timestamp command requires the commands service");
        return commands.Register(new CommandDefinition
        {
            Name = Name,
            Description = "Show the user-message timeline; jump to, revert to, or fork at one entry",
            Input = new CommandInputDescriptor("list | jump <#seq> | revert <#seq> | fork <#seq>"),
            Handler = invocation => ExecuteAsync(ctx, invocation),
        });
    }

    private static async Task<CommandResult> ExecuteAsync(Context ctx, CommandInvocation invocation)
    {
        var tokens = invocation.RawInput.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var session = invocation.Agent.Session;
        var entries = SessionTimeline.UserMessages(session);
        if (tokens.Length == 0 || tokens[0] == "list")
            return entries.Count == 0
                ? new CommandResult.Success("no user messages in this session")
                : new CommandResult.Success(string.Join('\n', entries.Select(FormatEntry)));
        if (tokens[0] is not ("jump" or "revert" or "fork") || tokens.Length < 2)
            return new CommandResult.Error("usage: /timestamp list | jump <#seq> | revert <#seq> | fork <#seq>");
        if (ParseSeq(tokens[1]) is not { } seq)
            return new CommandResult.Error($"invalid timeline target \"{tokens[1]}\": expected #<seq>");
        if (!entries.Any(entry => entry.Seq == seq))
            return new CommandResult.Error($"#{seq} is not a user message in this session");
        return tokens[0] switch
        {
            "jump" => new CommandResult.Success($"jump #{seq}"),
            "revert" => await RevertAsync(ctx, invocation, seq),
            _ => Fork(ctx, invocation, seq),
        };
    }

    /** 回退 = 文件按检查点恢复到 <= seq, 再把会话截断到该消息之前(文件恢复失败就不截断)。 */
    private static async Task<CommandResult> RevertAsync(Context ctx, CommandInvocation invocation, long seq)
    {
        var session = invocation.Agent.Session;
        var cwd = session.Header.Cwd;
        if (ctx.Get<ICheckpointRestore>(ICheckpointRestore.ServiceName, false) is { } restore
            && cwd is { Length: > 0 })
        {
            try
            {
                await restore.RestoreToSeqAsync(cwd, seq, invocation.Signal);
            }
            catch (Exception error) when (error is InvalidOperationException or IOException)
            {
                return new CommandResult.Error($"files were not restored ({error.Message}); the session was left untouched");
            }
        }
        return session.TryTruncateTo(seq)
            ? new CommandResult.Success($"reverted to before #{seq}")
            : new CommandResult.Error($"#{seq} cannot be truncated: only this session's own events can be dropped");
    }

    /** 分叉 = 复制 [0, seq] 落成新会话; 被选中那条本身保留, 与 TUI 的 `/timestamp fork` 一致。 */
    private static CommandResult Fork(Context ctx, CommandInvocation invocation, long seq)
    {
        var persistence = ctx.Get<ISessionPersistence>(ISessionPersistence.ServiceName)
            ?? throw new InvalidOperationException("session persistence is not available");
        var session = invocation.Agent.Session;
        try
        {
            var forked = SessionFork.Fork(persistence, session, seq + 1, $"fork of {session.Id} @{seq}", session.Header.Cwd);
            return new CommandResult.Success($"forked -> {forked}: use /resume {forked} to open it");
        }
        catch (InvalidOperationException error)
        {
            return new CommandResult.Error(error.Message);
        }
    }

    private static long? ParseSeq(string token)
    {
        var digits = token.StartsWith('#') ? token[1..] : token;
        return long.TryParse(digits, out var seq) ? seq : null;
    }

    private static string FormatEntry(SessionTimeline.Entry entry)
        => $"#{entry.Seq} {LocalTime(entry.Time)}  {SessionTimeline.Preview(entry.Text)}";

    private static string LocalTime(long epochMilliseconds)
        => DateTimeOffset.FromUnixTimeMilliseconds(epochMilliseconds).ToLocalTime().ToString("HH:mm:ss");
}
