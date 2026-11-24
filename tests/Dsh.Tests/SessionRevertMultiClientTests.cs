using Dsh.Core;
using Dsh.Interaction;
using Dsh.Llm;
using Dsh.Persistence;

namespace Dsh.Tests;

/**
 * 阶段 4 的双客户端场景: 一个客户端把会话回退掉, 另一个客户端**正在读旧区间**。
 *
 * 回退是只追加的事件(4a), 所以旧区间必须原样可读、日志只会变长, 而两个读者折同一份日志必须得到
 * 同一个"当前会话"。这条用例就是把这个承诺钉在真实 host + 两个真实客户端上, 而不是只在进程内验证。
 */
[Collection(SerialProcessCollection.Name)]
public sealed class SessionRevertMultiClientTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /** 回退会先按检查点恢复文件: 隔离环境里没有可用仓库, 那样会挡住回退; 这条用例只验证日志语义。 */
    private const string HomeWithoutCheckpoints = """
        logging:
          file: false
        plugins:
          "@deepseek-ai/dsh-checkpoints":
            enabled: false
        """;

    [Fact(Timeout = 60000)]
    public async Task RevertByOneClient_KeepsTheOldRangeReadable_ForTheOther()
    {
        await using var host = await HarnessHostFixture.CreateAsync(
            "rv2", TestContext.Current.CancellationToken, decorateBackend: null, homeSettings: HomeWithoutCheckpoints);
        var session = host.Environment.Agent.Session;
        var id = session.Id.Value;
        AppendTurn(session, 1, "第一轮");
        AppendTurn(session, 2, "第二轮");
        await FlushAsync(host);

        var reader = await host.ConnectAsync("reader", Ct);
        var reverter = await host.ConnectAsync("reverter", Ct);
        var target = SessionTimeline.UserMessages(session)[^1].Seq;
        // 命令停在"这条消息所属回合之前", 所以回退目标是回合起点而不是消息本身。
        var boundary = SessionTimeline.TurnStartOf(session.SnapshotEvents(), target);
        Assert.True(boundary < target);
        var before = await reader.TotalEventCountAsync(id, Ct);
        var prefix = await reader.GetSessionLogSliceAsync(id, 0, (int)before, Ct);
        Assert.NotNull(prefix);
        var prefixBytes = prefix.Bytes;
        Assert.Equal(prefix.ContentHash, SessionLogSlice.HashOf(prefixBytes));

        // 另一个客户端(经 host 命令)把第二轮回退掉。
        var command = await reverter.SessionEditing.ExecuteSessionCommandAsync(id, $"/timestamp revert #{target}", Ct);
        Assert.True(command.Known, $"回退命令没有被识别: {command.Text}");
        Assert.Contains("reverted to before", command.Text, StringComparison.Ordinal);
        // 落盘一次: 切片按帧索引读盘, 索引要等写侧 flush 之后才覆盖新追加的那几条(真实客户端此时会退回事件快照)。
        await FlushAsync(host);

        // 日志只变长(命令本身还写了自己的两条事件), 旧区间一个字节都没变。
        var after = await reader.TotalEventCountAsync(id, Ct);
        Assert.True(after > before, $"回退之后日志应当变长: before={before} after={after}");
        Assert.Equal(after, session.Seq);
        var reread = await reader.GetSessionLogSliceAsync(id, 0, (int)before, Ct);
        Assert.NotNull(reread);
        Assert.Equal(prefix.ContentHash, reread.ContentHash);
        Assert.Equal(prefixBytes, reread.Bytes);

        // 增量里带回退事件; 被回退的原文仍然躺在日志里(读者读旧区间不受影响)。
        var delta = await reader.GetSessionLogSliceAsync(id, before, (int)(after - before), Ct);
        Assert.NotNull(delta);
        var deltaEvents = SessionLogSliceParser.Parse(delta.Bytes, delta.FromSeq);
        var revert = Assert.Single(deltaEvents, ev => ev.Data is SessionRevertPayload);
        Assert.Equal(boundary, Assert.IsType<SessionRevertPayload>(revert.Data).TargetSeq);
        var snapshot = await reader.GetSessionSnapshotRangeAsync(id, 0, (int)after, Ct);
        Assert.Contains(snapshot.Events, ev => ev.Data is UserMessagePayload payload
            && payload.Message.Content.OfType<TextBlock>().Any(block => block.Text == "第二轮: 用户"));

        // 两个读者折同一份日志得到同一个当前会话: 客户端本地折叠 vs host 的内存会话。
        var merged = new List<SessionEvent>();
        merged.AddRange(SessionLogSliceParser.Parse(prefix.Bytes, prefix.FromSeq));
        merged.AddRange(deltaEvents);
        var local = Session.FromRestore(session.Id, merged, session.Header, 0);
        Assert.Equal(Texts(session), Texts(local));
        Assert.Equal(["第一轮: 用户", "第一轮: 助手"], Texts(local));
    }

    private static async Task FlushAsync(HarnessHostFixture host)
    {
        var store = host.Environment.App.Ctx.Get<SessionStore>(SessionStore.ServiceName)!;
        foreach (var live in store.List())
            await store.Flush(live);
    }

    private static IReadOnlyList<string> Texts(Session session)
        => [.. session.DeriveMessages().Select(message => MessageText.Flatten(message.Content))];

    /** 每轮一条用户消息 + 一条助手消息: 第二轮正好可以整段回退掉。 */
    private static void AppendTurn(Session session, int turn, string text)
    {
        session.Append(new TurnStartPayload(turn));
        session.Append(new StepStartPayload(turn, 1));
        session.Append(new UserMessagePayload(MessageFactory.CreateUserText($"{text}: 用户")), new SurfaceOp.Append());
        session.Append(
            new AssistantMessagePayload(turn, 1, MessageFactory.CreateAssistantMessage([new TextBlock($"{text}: 助手")], "p", "m")),
            new SurfaceOp.Append());
        session.Append(new TurnEndPayload(turn, new TurnEndReason.Completed()));
    }
}