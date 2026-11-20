using Dsh.Tui;

namespace Dsh.Tests;

/**
 * 转录窗口化: 超过驻留上限时只丢最老的整行, 行起点/折叠区间/用户消息位置一起平移。
 * 判据是"裁完之后按新偏移取到的仍然是同一行文本", 而不是"内存降下来了" —— 后者靠 MaxRetainedChars 保证。
 */
public sealed class TranscriptTrimTests
{
    [Fact]
    public void AppendingPastTheBudget_DropsTheOldestWholeLinesAndKeepsOffsetsConsistent()
    {
        var renderer = new TranscriptRenderer();
        var line = new string('x', 1000);
        const int lineCount = 12000;   // 12,000,000 字符 > 4M 上限, 至少要裁一次
        for (var index = 0; index < lineCount; index += 1)
            renderer.AppendRaw($"{index:D5}-{line}\n");

        Assert.True(renderer.DroppedChars > 0, "超过驻留上限后应当裁过头");
        Assert.True(renderer.CurrentLength <= 4 * 1024 * 1024, $"裁过之后仍驻留 {renderer.CurrentLength} 字符");
        Assert.True(renderer.TrimVersion > 0);

        // 每一行的起点都必须仍然指向以它的编号开头的文本。
        var lines = renderer.CompletedLines;
        var starts = renderer.LineStarts;
        Assert.Equal(lines.Count, starts.Count);
        Assert.NotEmpty(lines);
        for (var index = 0; index < lines.Count; index += 1)
        {
            var prefix = lines[index][..5];
            Assert.Equal(prefix, $"{int.Parse(prefix):D5}");
            Assert.True(starts[index] >= 0);
            if (index > 0)
                Assert.Equal(starts[index - 1] + lines[index - 1].Length + 1, starts[index]);
        }

        // 最后一行仍然是最后追加的那一条, 说明裁头没有动尾部。
        Assert.StartsWith($"{lineCount - 1:D5}-", lines[^1]);
    }

    [Fact]
    public void Fold_CrossingTheTrimBoundary_IsClampedInsteadOfDropped()
    {
        var renderer = new TranscriptRenderer();
        var line = new string('y', 1000);
        for (var index = 0; index < 3500; index += 1)
            renderer.AppendRaw($"{index:D5}-{line}\n");
        Assert.Equal(0, renderer.TrimVersion);
        // 折叠从"距当前尾部 150 万字符"处开始: 下一次裁头的切点会落在它中间。
        var foldStart = renderer.CurrentLength - 1_500_000;
        renderer.AddFold(foldStart, renderer.CurrentLength, "tool", "preview");

        for (var index = 3500; index < 4500; index += 1)
            renderer.AppendRaw($"{index:D5}-{line}\n");

        Assert.True(renderer.TrimVersion > 0);
        var fold = Assert.Single(renderer.Folds);
        Assert.Equal(0, fold.Start);
        Assert.True(fold.End > 0);
        Assert.True(fold.End <= renderer.CurrentLength);
        Assert.Equal("tool", fold.Label);
    }

    [Fact]
    public void TrimNeverSplitsAnIncompleteLine()
    {
        var renderer = new TranscriptRenderer();
        // 全部追加都留一个未闭合的行(没有换行): 裁剪只在"最后一行已完整"的安全点发生, 所以这里一次都不该裁。
        for (var index = 0; index < 80; index += 1)
            renderer.AppendRaw(new string('z', 100_000));

        Assert.Equal(0, renderer.TrimVersion);
        Assert.True(renderer.CurrentLength > 4 * 1024 * 1024);
        // 一行补完、再追加一整行之后, 裁头才会发生。
        renderer.AppendRaw("\n");
        renderer.AppendRaw("after-newline\n");
        Assert.True(renderer.TrimVersion > 0);
    }
}