using Dsh.Interaction;

namespace Dsh.Tests;

/** mention token 识别与替换: 光标必须在 @token 区间内才视为活跃; 替换时保留 token 后的提示词并补尾随空格。 */
public sealed class MentionTokenTests
{
    [Fact]
    public void MentionToken_ActiveWhenCaretInsideToken()
    {
        var active = MentionResolver.TryGetActiveToken("see @READ for details", 8, out var start, out var end);

        Assert.True(active);
        Assert.Equal(4, start);
        Assert.Equal(9, end);
    }

    [Fact]
    public void MentionToken_InactiveWhenCaretBeyondToken()
    {
        var active = MentionResolver.TryGetActiveToken("@README.md 继续输入提示词", 12, out _, out _);

        Assert.False(active);
    }

    [Fact]
    public void MentionToken_InactiveWhenCaretBeforeToken()
    {
        var active = MentionResolver.TryGetActiveToken("总结 @README.md", 2, out _, out _);

        Assert.False(active);
    }

    [Fact]
    public void MentionToken_ActiveWhenCaretInsideEarlierTokenOfLongerPrompt()
    {
        var text = "@src 请修改这个文件";
        var active = MentionResolver.TryGetActiveToken(text, 3, out var start, out var end);

        Assert.True(active);
        Assert.Equal(0, start);
        Assert.Equal(4, end);
    }

    [Fact]
    public void MentionToken_ActiveWithEmptyTokenRightAfterAt()
    {
        var active = MentionResolver.TryGetActiveToken("look @", 6, out var start, out var end);

        Assert.True(active);
        Assert.Equal(5, start);
        Assert.Equal(6, end);
    }

    [Fact]
    public void MentionToken_InactiveWithoutAt()
    {
        Assert.False(MentionResolver.TryGetActiveToken("plain text", 3, out _, out _));
    }

    [Fact]
    public void ReplaceMention_KeepsTextAfterTokenAndAppendsSpace()
    {
        var (text, caret) = MentionResolver.ReplaceActiveToken("@REA 请总结这个文件", 0, 4, "README.md");

        Assert.Equal("@README.md  请总结这个文件", text);
        Assert.Equal(11, caret);
        Assert.False(MentionResolver.TryGetActiveToken(text, caret, out _, out _));
    }

    [Fact]
    public void ReplaceMention_KeepsTextBeforeToken()
    {
        var (text, _) = MentionResolver.ReplaceActiveToken("总结一下 @REA 这个文件", 5, 9, "README.md");

        Assert.Equal("总结一下 @README.md  这个文件", text);
    }
}
