using Dsh.Interaction;
using Dsh.Tui;

namespace Dsh.Tests;

/**
 * 时间线索引的合并语义。
 *
 * 这类缺陷不会崩, 只会让"全量消息里的下标"算错 —— 表现是跳转全部落空, 而且只有在
 * "索引先铺满、补页又送来同一批消息"这种顺序下才出现(验收里就是这样跑的)。
 */
public sealed class TranscriptHistoryIndexTests
{
    [Fact]
    public void MergeEarlier_IsIdempotent_SoRepeatedScansDoNotDuplicateMessages()
    {
        var index = new TranscriptHistoryIndex();
        index.Reset(200);
        SessionTimeline.Entry[] later = [new(300, 0, "later-1"), new(400, 0, "later-2")];
        index.MergeEarlier(later, [250], [], 200);

        // 同一批消息再合并一次(补页会重复送来): 条数不许变。
        index.MergeEarlier(later, [250], [], 200);

        Assert.Equal(2, index.Messages.Count);
        Assert.Equal([300L, 400L], index.Messages.Select(entry => entry.Seq));
    }

    [Fact]
    public void MergeEarlier_InsertsEarlierMessagesBeforeExistingOnes()
    {
        var index = new TranscriptHistoryIndex();
        index.Reset(200);
        index.MergeEarlier([new SessionTimeline.Entry(300, 0, "new")], [250], [], 200);
        index.MergeEarlier([new SessionTimeline.Entry(100, 0, "old")], [50], [], 0);

        Assert.Equal([100L, 300L], index.Messages.Select(entry => entry.Seq));
        Assert.Equal(0, index.CoveredFrom);
        // 回合起点同理: 更早的插到前面, 不重复。
        Assert.Equal(50, index.TurnStartOf(120));
        Assert.Equal(250, index.TurnStartOf(320));
    }
}
