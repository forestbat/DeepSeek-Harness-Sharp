using Dsh.Core;
using Dsh.Llm;
using Dsh.Tui;

namespace Dsh.Tests;

/**
 * 转录的行表示: 正文只存一份, 行是它的 (起点, 长度) 切片。
 *
 * 这条性质有两面: 行切片必须永远指向自己的文本(偏移不漂), 而且同一段文字不能在别处再存一份
 * (逐行 string 曾经让一份 12M 字符的转录多占同样多内存)。上限(4MB 驻留 / 4 万折行)已删除,
 * 所以这里同时钉住"再多内容也不丢头部"。
 */
public sealed class TranscriptLinesTests
{
    [Fact]
    public void LineSlices_StayAligned_AndStoreTheTextOnce()
    {
        var renderer = new TranscriptRenderer();
        const int lineCount = 500;
        for (var index = 0; index < lineCount; index += 1)
            renderer.AppendRaw($"{index:D5}-内容-{index}\n");

        Assert.Equal(lineCount, renderer.LineCount);
        var expectedStart = 0;
        for (var index = 0; index < lineCount; index += 1)
        {
            var line = renderer.LineAt(index);
            Assert.Equal(expectedStart, line.Start);
            Assert.Equal($"{index:D5}-内容-{index}", new string(renderer.LineSpan(index)));
            expectedStart += line.Length + 1;
        }

        // 正文长度 = 所有行(含换行)之和: 行没有额外拷贝一份文本。
        Assert.Equal(renderer.CurrentLength, expectedStart);
        Assert.Equal(0, renderer.ResetVersion);
    }

    [Fact]
    public void AppendingFarPastTheOldBudget_KeepsEveryLine()
    {
        var renderer = new TranscriptRenderer();
        var payload = new string('x', 1000);
        const int lineCount = 12_000;   // 12,000,000 字符: 旧实现的 4MB 上限会在这里裁掉大半个转录
        for (var index = 0; index < lineCount; index += 1)
            renderer.AppendRaw($"{index:D5}-{payload}\n");

        Assert.Equal(lineCount, renderer.LineCount);
        Assert.True(renderer.CurrentLength > 4 * 1024 * 1024);
        // 没有裁头: 行号与偏移只增不减, 第一条仍然在, 也不会因为"重置"作废按行号缓存的状态。
        Assert.Equal(0, renderer.LineAt(0).Start);
        Assert.StartsWith("00000-", new string(renderer.LineSpan(0)));
        Assert.StartsWith($"{lineCount - 1:D5}-", new string(renderer.LineSpan(lineCount - 1)));
        Assert.Equal(0, renderer.ResetVersion);
    }

    [Fact]
    public void IncompleteLine_StaysInTheTail_AndNeverSplitsTheLineBefore()
    {
        var renderer = new TranscriptRenderer();
        renderer.AppendRaw("abc");
        Assert.Equal(0, renderer.LineCount);
        Assert.Equal("abc", new string(renderer.TailSpan));

        renderer.AppendRaw("def\nghi");
        Assert.Equal(1, renderer.LineCount);
        Assert.Equal("abcdef", new string(renderer.LineSpan(0)));
        Assert.Equal(7, renderer.TailStart);
        Assert.Equal("ghi", new string(renderer.TailSpan));
    }

    [Fact]
    public void FoldStartingInsideALine_IsKeptAtItsOffset_AndClampedToTheTranscript()
    {
        var renderer = new TranscriptRenderer();
        var payload = new string('y', 1000);
        for (var index = 0; index < 4000; index += 1)
            renderer.AppendRaw($"{index:D5}-{payload}\n");
        var foldStart = renderer.CurrentLength - 1_500_000;
        renderer.AddFold(foldStart, renderer.CurrentLength, "tool", "preview");
        for (var index = 4000; index < 4200; index += 1)
            renderer.AppendRaw($"{index:D5}-{payload}\n");

        var fold = Assert.Single(renderer.Folds);
        Assert.Equal(foldStart, fold.Start);
        Assert.Equal("tool", fold.Label);
        Assert.True(fold.End <= renderer.CurrentLength);
    }

    [Fact]
    public void Clear_ResetsOffsetsAndBumpsTheResetVersion()
    {
        var renderer = new TranscriptRenderer();
        renderer.AppendRaw("first\nsecond\n");
        var version = renderer.ResetVersion;
        renderer.Clear();

        Assert.Equal(0, renderer.LineCount);
        Assert.Equal(0, renderer.CurrentLength);
        Assert.Equal(version + 1, renderer.ResetVersion);
        renderer.AppendRaw("again\n");
        Assert.Equal(0, renderer.LineAt(0).Start);
        Assert.Equal("again", new string(renderer.LineSpan(0)));
    }

    /** 锚点靠"偏移 → 事件序号"确认重放之后还是同一行; 尾部未闭合的行属于最后一条事件。 */
    [Fact]
    public void SeqAt_MapsOffsetsToTheEventThatProducedThem()
    {
        var renderer = new TranscriptRenderer();
        renderer.MarkEvent(10);
        renderer.AppendRaw("a\nb\n");
        renderer.MarkEvent(20);
        renderer.AppendRaw("c\n");
        renderer.AppendRaw("tail");

        Assert.Equal(10, renderer.SeqAt(0));
        Assert.Equal(10, renderer.SeqAt(2));
        Assert.Equal(20, renderer.SeqAt(4));
        Assert.Equal(20, renderer.SeqAt(renderer.CurrentLength));
    }

    /**
     * 补页靠"整块前置"而不是"清空重放": 前置之后既有内容的文本一字不变, 只是偏移整体右移。
     * 这条性质一旦破坏, 用户补页时看到的那一行就会漂走(旧实现把字符差加到行号上, 直接甩到底部)。
     */
    [Fact]
    public void PrependFrom_KeepsExistingTextAndShiftsEveryOffset()
    {
        var renderer = new TranscriptRenderer();
        renderer.AppendRaw("current-1\ncurrent-2\n");
        var before = new[] { new string(renderer.LineSpan(0)), new string(renderer.LineSpan(1)) };

        var page = new TranscriptRenderer();
        page.AppendRaw("older-1\n");
        page.AppendRaw("older-2\n");
        var delta = renderer.PrependFrom(page);

        Assert.Equal(page.CurrentLength, delta);
        Assert.Equal(4, renderer.LineCount);
        Assert.Equal("older-1", new string(renderer.LineSpan(0)));
        Assert.Equal("older-2", new string(renderer.LineSpan(1)));
        Assert.Equal(before[0], new string(renderer.LineSpan(2)));
        Assert.Equal(before[1], new string(renderer.LineSpan(3)));
        // 行切片必须首尾相接: 偏移没有漂, 也没有丢字符。
        var expected = 0;
        for (var index = 0; index < renderer.LineCount; index += 1)
        {
            Assert.Equal(expected, renderer.LineAt(index).Start);
            expected += renderer.LineAt(index).Length + 1;
        }
        Assert.Equal(renderer.CurrentLength, expected);
    }

    /** 页尾停在半行时, 前置必须先补一个换行: 否则它和本文第一行黏成一行。 */
    [Fact]
    public void PrependFrom_SeparatesAHalfLineAtThePageBoundary()
    {
        var renderer = new TranscriptRenderer();
        renderer.AppendRaw("current\n");
        var page = new TranscriptRenderer();
        page.AppendRaw("older-half");

        renderer.PrependFrom(page);

        Assert.Equal("older-half", new string(renderer.LineSpan(0)));
        Assert.Equal("current", new string(renderer.LineSpan(1)));
    }

    /** 没有未闭合构造时页边界才是干净接缝; 停在工具调用里必须能识别出来。 */
    [Fact]
    public void HasOpenConstruct_ReportsWhetherTheBoundaryIsClean()
    {
        var clean = new TranscriptRenderer();
        clean.AppendRaw("plain text\n");
        Assert.False(clean.HasOpenConstruct);

        var open = new TranscriptRenderer();
        var call = new ToolCallPayload(1, 1, ToolCallId.Create("call-1"), "read", "{\"file_path\":\"a\"}");
        open.AppendSessionEvent(new SessionEvent { Seq = 0, Time = 0, Type = call.Type, Data = call }, replay: true);
        Assert.True(open.HasOpenConstruct);
    }

    /**
     * 静默表必须与真实渲染行为一致: 表里每个类型都画不出字符。
     * 这张表是补页的性能捷径(不解析载荷), 一旦某个类型改成会画东西, 它就会被静默吞掉 ——
     * 这条用例就是防那种漂移的, 而不是针对某个特定会话。
     */
    [Theory]
    [InlineData(SessionEventTypes.TurnStart)]
    [InlineData(SessionEventTypes.StepStart)]
    [InlineData(SessionEventTypes.StepEnd)]
    [InlineData(SessionEventTypes.RequestHeader)]
    [InlineData(SessionEventTypes.RequestContext)]
    [InlineData(SessionEventTypes.SessionEndSeed)]
    [InlineData(SessionEventTypes.AgentInboxSpliced)]
    [InlineData(SessionEventTypes.WorkingDirectoryChange)]
    [InlineData(SessionEventTypes.FileChanged)]
    [InlineData(SessionEventTypes.ToolDisclosed)]
    [InlineData(SafetyDecisionAuditPayload.EventType)]
    public void SilentEventTypes_ProduceNoCharacters(string type)
    {
        var renderer = new TranscriptRenderer();
        renderer.AppendRaw("before\n");
        var before = renderer.CurrentLength;

        // 用一个"不认识类型"的原始载荷: 渲染器不许靠载荷内容决定要不要画。
        var raw = new UnknownSessionEventPayload(type, default);
        renderer.AppendSessionEvent(new SessionEvent { Seq = 7, Time = 0, Type = type, Data = raw }, replay: true);

        Assert.Equal(before, renderer.CurrentLength);
        Assert.Equal(1, renderer.LineCount);
        // 行->事件序号的映射仍要前进: 锚点靠它判断"还是同一行"。
        Assert.Equal(7, renderer.SeqAt(before));
    }
}

