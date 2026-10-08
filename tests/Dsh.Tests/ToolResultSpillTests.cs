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
}
