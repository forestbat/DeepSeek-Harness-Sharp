using Dsh.Agent;
using Dsh.Boot;
using Dsh.Core;
using Dsh.Llm;
using Dsh.Runtime;
using Dsh.Tui;
using Dsh.Tui.Services;
using Dsh.Interaction;

namespace Dsh.Tests;

/**
 * 补页/缺页的如实告知(对应 GUI 的 HistoryIncomplete)。
 *
 * 打不开比少一段严重, 悄悄少一段又比"明确说少了哪一段"严重: 补页失败必须是可见的一句话,
 * 而且界面要停在已经画好的内容上 —— 半截重放留在屏幕上就是另一类"加载不出来"。
 */
[Collection("TuiTheme")]
public sealed class ChatPaneHistoryGapTests : IDisposable
{
    private const int TotalEvents = 60_000;
    private const int ResidentFrom = 40_000;
    /** Session 的分页大小: 页边界就是 seq / 4096。 */
    private const int PageSize = 4096;

    private readonly string _homeDir = Path.Combine(
        Path.Combine(TempTree.RepositoryRoot, "tests/.scratch/test-homes"),
        Guid.NewGuid().ToString("N"));

    [Fact]
    public void UnreadablePrefixPage_IsReportedInTheStatusLine_AndTheTranscriptStaysPut()
    {
        var events = BuildEvents(TotalEvents);
        var readable = true;
        var reads = 0;
        var readOffsets = new List<long>();
        var header = Header();
        var session = Session.FromWindow(
            header.Id,
            new SessionWindow(ResidentFrom, TotalEvents, [.. events.Skip(ResidentFrom)],
                (offset, count) =>
                {
                    if (!readable)
                        return [];
                    reads += 1;
                    readOffsets.Add(offset);
                    var from = (int)offset;
                    if (from >= events.Count)
                        return [];
                    var take = (int)Math.Min(count, events.Count - offset);
                    return [.. events.Skip(from).Take(take)];
                }),
            header,
            0);
        var (ctx, agent, home) = BuildAgent(session);
        // 建 agent 时折叠会自己读一遍前缀(与补页无关), 计数从这里重新开始。
        reads = 0;
        readOffsets.Clear();
        using var chat = new ChatWindow(ctx, agent, home);
        var pane = chat.InputPane;
        Draw(chat);
        Assert.True(pane.HasOlderHistory);
        pane.StickToBottom = false;
        pane.ScrollOffset = 0;
        Draw(chat);
        var topBefore = pane.ViewportTopText();

        // 第一页读得回来: 接上更早的历史, 视口留在原来那一行, 且没有失败原因上屏。
        Assert.True(pane.LoadOlderHistory());
        Draw(chat);
        Assert.Equal(topBefore, pane.ViewportTopText());
        Assert.False(pane.StickToBottom);
        Assert.Null(pane.OlderHistoryStatusText);
        Assert.DoesNotContain("读不出来", pane.StatusText, StringComparison.Ordinal);
        // 补一页 20000 条只读一遍(不再为判空把同一段读第二次)。
        // 补页只把这一段读一遍: 次数不超过区间覆盖的页数(旧实现为"判空一次 + 重放一次", 会翻倍)。
        Assert.True(reads <= 6, $"补一页读了 {reads} 次: {string.Join(", ", readOffsets)}");
        var renderedAfterFirstPage = pane.Renderer.LineCount;
        var topAfterFirstPage = pane.ViewportTopText();

        // 之后页读不出来: 补页失败, 但界面必须停在原地, 并把原因写进状态行。
        readable = false;
        Assert.False(pane.LoadOlderHistory());
        Assert.NotNull(pane.OlderHistoryStatusText);
        Assert.Contains("页", pane.OlderHistoryStatusText!, StringComparison.Ordinal);
        Assert.Contains(pane.OlderHistoryStatusText!, pane.StatusText, StringComparison.Ordinal);
        Assert.Equal(renderedAfterFirstPage, pane.Renderer.LineCount);
        Assert.Equal(topAfterFirstPage, pane.ViewportTopText());
        Assert.True(pane.HasOlderHistory);
    }

    private SessionHeader Header() => new()
    {
        Version = SessionHeader.SessionFormatVersion,
        Id = SessionId.Create($"session-{Guid.NewGuid():N}"),
        CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        IsSeeded = false,
    };

    /** 稠密事件: 回合起点 + 用户消息 + 流式正文, 让转录有行、有用户消息、有回合边界。 */
    private static List<SessionEvent> BuildEvents(int count)
    {
        var events = new List<SessionEvent>(count);
        var turn = 0;
        while (events.Count < count)
        {
            turn += 1;
            events.Add(New(events.Count, new TurnStartPayload(turn)));
            events.Add(New(events.Count, new UserMessagePayload(MessageFactory.CreateUserText($"问题 {turn}")), new SurfaceOp.Append()));
            events.Add(New(events.Count, new AssistantChunkPayload(turn, 1, new StreamChunk.TextDelta(0, $"回答 {turn} 的一段正文\n"))));
            events.Add(New(events.Count, new AssistantChunkPayload(turn, 1, new StreamChunk.TextDelta(0, $"回答 {turn} 的第二段\n"))));
        }
        return events;
    }

    private static SessionEvent New(long seq, SessionEventPayload payload, SurfaceOp? surfaceOp = null) => new()
    {
        Type = payload.Type,
        Seq = seq,
        Time = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        Data = payload,
        SurfaceOp = surfaceOp,
    };

    private (Context Ctx, AgentLoopAgent Agent, HarnessHome Home) BuildAgent(Session session)
    {
        var ctx = new Context();
        _ = new SessionStore(ctx);
        var agents = new AgentRegistry(ctx);
        _ = new AgentLoop(ctx);
        CommandsService.Register(ctx);
        var agent = new AgentLoopAgent(ctx, session.Id, new AgentOptions("test-provider", "test-model"), session, _ => 0);
        return (ctx, agent, HarnessHome.Resolve(_homeDir));
    }

    private static void Draw(ChatWindow chat)
    {
        chat.DrainUi();
        var grid = new CellGrid(100, 30);
        chat.Draw(grid, LayoutEngine.Calculate(100, 30));
    }

    public void Dispose() => TempTree.Delete(_homeDir);
}





