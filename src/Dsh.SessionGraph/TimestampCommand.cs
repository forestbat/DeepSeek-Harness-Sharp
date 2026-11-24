using Dsh.Boot;
using Dsh.Core;
using Dsh.Interaction;
using Dsh.Llm;
using Dsh.Runtime;
using Dsh.Checkpoints;

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

    /**
     * 回退 = 文件按检查点恢复到不晚于该消息的检查点, 再把会话截断到"这条消息所属回合开始之前"(文件恢复失败就不截断)。
     * 停在回合之前而不是消息之前: 消息之前紧挨着它的 TurnStart, 截在消息处会留下没有 TurnEnd 的回合。
     */
    private static async Task<CommandResult> RevertAsync(Context ctx, CommandInvocation invocation, long seq)
    {
        var session = invocation.Agent.Session;
        var boundary = SessionTimeline.TurnStartOf(session.SnapshotEvents(), seq);
        var cwd = session.Header.Cwd;
        // 文件恢复可能根本没发生: 检查点插件没启用、或该位置之前没有检查点。
        // 以前这两种情况都静默跳过并回一句"reverted", 用户以为文件回到过去了, 实际只有会话被截断。
        var files = "files not restored: the checkpoints plugin is disabled (enable @deepseek-ai/dsh-checkpoints)";
        if (ctx.Get<CheckpointService>(CheckpointService.ServiceName, false) is { } restore)
        {
            if (cwd is not { Length: > 0 })
                files = "files not restored: this session has no working directory";
            else
            {
                try
                {
                    var point = await restore.RestoreToSeqAsync(cwd, seq, invocation.Signal);
                    files = point is null
                        ? "files not restored: no checkpoint at or before this message"
                        : $"files restored to checkpoint {point}";
                }
                catch (Exception error) when (error is InvalidOperationException or IOException)
                {
                    return new CommandResult.Error($"files were not restored ({error.Message}); the session was left untouched");
                }
            }
        }
        return session.Revert(boundary) is not null
            ? new CommandResult.Success($"reverted to before #{boundary}; {files}; #{seq} is back in the input area")
            : new CommandResult.Error($"#{seq} cannot be reverted: only this session's own events can be dropped");
    }

    /**
     * 分叉 = 复制"这条消息所属回合之前"的前缀落成新会话; 被选中那条不进新会话(它的原文回填输入区)。
     * 与 TUI 的同名动作共用同一套语义。
     */
    private static CommandResult Fork(Context ctx, CommandInvocation invocation, long seq)
    {
        var persistence = ctx.Get<ISessionPersistence>(ISessionPersistence.ServiceName)
            ?? throw new InvalidOperationException("session persistence is not available");
        var session = invocation.Agent.Session;
        try
        {
            var boundary = SessionTimeline.TurnStartOf(session.SnapshotEvents(), seq);
            var forked = SessionFork.Fork(persistence, session, boundary, SessionFork.ForkTitle(session, seq), session.Header.Cwd);
            return new CommandResult.Success($"forked -> {forked}: #{seq} is back in the input area");
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
