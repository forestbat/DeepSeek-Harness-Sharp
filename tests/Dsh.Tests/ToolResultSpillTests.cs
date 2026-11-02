using Dsh.Runtime;

namespace Dsh.Tests;

public sealed class ToolResultSpillTests
{
    [Fact]
    public void Spill_WritesFileAndKeepsHeadAndTail()
    {
        var home = Path.Combine(Path.GetTempPath(), $"dsh-spill-{Guid.NewGuid():N}");
        try
        {
            var ctx = new Context();
            ctx.SetOwn("dshHomePath", home);
            var spill = new ToolResultSpill(ctx, new ToolResultSpillOptions(MaxBytes: 32, HeadChars: 8, TailChars: 8));
            var text = new string('a', 100);

            Assert.True(spill.ShouldSpill(text));
            var result = spill.Spill(text, "call-1");

            Assert.Contains("spilled to", result);
            Assert.StartsWith("aaaaaaaa", result);
            Assert.EndsWith("aaaaaaaa", result);
            var file = Directory.GetFiles(Path.Combine(home, "tool-results")).Single();
            Assert.Equal(text, File.ReadAllText(file));
        }
        finally
        {
            try
            {
                Directory.Delete(home, true);
            }
            catch (IOException)
            {
            }
        }
    }

    [Fact]
    public void Spill_SmallTextUnchanged()
    {
        var ctx = new Context();
        ctx.SetOwn("dshHomePath", Path.Combine(Path.GetTempPath(), $"dsh-spill-{Guid.NewGuid():N}"));
        var spill = new ToolResultSpill(ctx, new ToolResultSpillOptions(MaxBytes: 1024));

        Assert.False(spill.ShouldSpill("short"));
        Assert.Equal("short", spill.Spill("short", "call-1"));
    }

    [Fact]
    public void Options_DefaultThresholdKeepsHeadAndTail()
    {
        var options = new ToolResultSpillOptions();

        Assert.Equal(4096, options.HeadLength);
        Assert.Equal(1024, options.TailLength);
    }

    [Fact]
    public void Options_ScaleHeadAndTailWithMaxBytes()
    {
        var options = new ToolResultSpillOptions(MaxBytes: 2048);

        Assert.Equal(1024, options.HeadLength);
        Assert.Equal(256, options.TailLength);
    }

    [Fact]
    public void Options_ExplicitHeadAndTailWin()
    {
        var options = new ToolResultSpillOptions(MaxBytes: 2048, HeadChars: 64, TailChars: 32);

        Assert.Equal(64, options.HeadLength);
        Assert.Equal(32, options.TailLength);
    }

    /** 2K 阈值下上下文里的结果应停在 2K 附近(头 1024 + 尾 256 + 落盘标记)。 */
    [Fact]
    public void Spill_KeepsContextNearThresholdAtTwoKilobytes()
    {
        var home = Path.Combine(Path.GetTempPath(), $"dsh-spill-{Guid.NewGuid():N}");
        try
        {
            var ctx = new Context();
            ctx.SetOwn("dshHomePath", home);
            var spill = new ToolResultSpill(ctx, new ToolResultSpillOptions(MaxBytes: 2048));
            var text = "HEAD" + new string('.', 3000) + "TAIL";

            Assert.True(spill.ShouldSpill(text));
            var result = spill.Spill(text, "call-2");

            Assert.StartsWith(text[..1024], result);
            Assert.EndsWith(text[^256..], result);
            Assert.True(
                result.Length <= 2048 + 256,
                $"spilled context ({result.Length} chars) should stay near the 2048-char threshold");
        }
        finally
        {
            try
            {
                Directory.Delete(home, true);
            }
            catch (IOException)
            {
            }
        }
    }
}
