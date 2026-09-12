using Dsh.Compaction;
using Dsh.Llm;

namespace Dsh.Tests;

public class SessionTitleExtractionTests
{
    [Fact]
    public void Extract_Pulls_Title_And_Strips_Line()
    {
        var summary = new ContentBlock[]
        {
            new TextBlock("## Primary Request and Intent\n- fix the renderer\n\nTITLE: 渲染错位修复\n"),
        };

        var (blocks, title) = SessionTitleExtraction.Extract(summary);

        Assert.Equal("渲染错位修复", title);
        var text = Assert.IsType<TextBlock>(Assert.Single(blocks));
        Assert.DoesNotContain("TITLE:", text.Text);
        Assert.Contains("fix the renderer", text.Text);
    }

    [Fact]
    public void Extract_No_Title_Keeps_Blocks()
    {
        var summary = new ContentBlock[]
        {
            new TextBlock("## Current Work\n- nothing"),
        };

        var (blocks, title) = SessionTitleExtraction.Extract(summary);

        Assert.Null(title);
        Assert.Single(blocks);
    }

    [Fact]
    public void Extract_Caps_Overlong_Title()
    {
        var summary = new ContentBlock[]
        {
            new TextBlock("TITLE: 这是一个非常非常非常长的标题超过了三十个字符限制需要被截断处理一下"),
        };

        var (_, title) = SessionTitleExtraction.Extract(summary);

        Assert.NotNull(title);
        Assert.True(title!.Length <= 30);
    }
}
