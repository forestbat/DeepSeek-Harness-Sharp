using System.Text;
using Dsh.Tui;

namespace Dsh.Tests;

/** 括号粘贴(DECSET 2004)解析: ESC[200~ … ESC[201~ 应作为一个粘贴事件原子交付。 */
public sealed class TerminalPasteTests
{
    private static void Append(TerminalInputParser parser, string text)
        => parser.Append(Encoding.UTF8.GetBytes(text));

    [Fact]
    public void Parser_EmitsBracketedPasteAsSingleEvent()
    {
        var parser = new TerminalInputParser();
        Append(parser, "\u001b[200~/tmp/shot.png\u001b[201~");

        Assert.True(parser.TryParse(out var input));
        Assert.True(input.IsPaste);
        Assert.Equal("/tmp/shot.png", input.Paste);
    }

    [Fact]
    public void Parser_EmptyPasteIsDistinguishable()
    {
        var parser = new TerminalInputParser();
        Append(parser, "\u001b[200~\u001b[201~");

        Assert.True(parser.TryParse(out var input));
        Assert.True(input.IsPaste);
        Assert.Equal("", input.Paste);
    }

    [Fact]
    public void Parser_TextAfterPasteIsParsedSeparately()
    {
        var parser = new TerminalInputParser();
        Append(parser, "\u001b[200~hi\u001b[201~x");

        Assert.True(parser.TryParse(out var paste));
        Assert.True(paste.IsPaste);
        Assert.Equal("hi", paste.Paste);

        Assert.True(parser.TryParse(out var key));
        Assert.False(key.IsPaste);
        Assert.Equal('x', key.Key.KeyChar);
    }

    [Fact]
    public void Parser_WaitsForClosingMarker()
    {
        var parser = new TerminalInputParser();
        Append(parser, "\u001b[200~partial");

        Assert.False(parser.TryParse(out _));
    }
}

/** 粘贴图片路径的归一化: 引号/file:// 前缀/MSYS 风格 /C:/ 都要还原成本机可读路径。 */
public sealed class ImagePathNormalizationTests
{
    [Fact]
    public void StripsSurroundingQuotes()
    {
        var expected = OperatingSystem.IsWindows() ? @"a\b.png" : "a/b.png";
        Assert.Equal(expected, ChatPane.NormalizePath("\"a/b.png\""));
    }

    [Fact]
    public void FileUrlBecomesLocalPath()
    {
        var path = Path.Combine(Path.GetTempPath(), "x.png");
        Assert.Equal(path, ChatPane.NormalizePath(new Uri(path).AbsoluteUri));
    }

    [Fact]
    public void WindowsDriveWithLeadingSlashIsRestored()
    {
        if (!OperatingSystem.IsWindows())
            return;
        Assert.Equal(@"C:\work\x.png", ChatPane.NormalizePath("/C:/work/x.png"));
        Assert.Equal(@"C:\work\x.png", ChatPane.NormalizePath("/c/work/x.png"));
        Assert.Equal(@"C:\work\x.png", ChatPane.NormalizePath("C:/work/x.png"));
    }

    [Fact]
    public void MatchesImagePathTokenBeforeTrailingInstruction()
    {
        var tokens = ChatPane.MatchImagePathTokens(@"C:\work\pasted-image-1.png 阅读这张图上的内容");

        var token = Assert.Single(tokens);
        Assert.Equal(@"C:\work\pasted-image-1.png", token);
    }

    [Fact]
    public void MatchesUnixImagePathTokenWithTrailingText()
    {
        var tokens = ChatPane.MatchImagePathTokens("/work/red.png describe it");

        Assert.Equal("/work/red.png", Assert.Single(tokens));
    }

    [Fact]
    public void IgnoresTextWithoutImagePaths()
    {
        Assert.Empty(ChatPane.MatchImagePathTokens("/work/notes.txt see this"));
        Assert.Empty(ChatPane.MatchImagePathTokens("just some text"));
    }

    [Fact]
    public void MatchesMentionImagePath()
    {
        var tokens = ChatPane.MatchImagePathTokens("@artifacts/gpu-screenshots/after-launcher.png 再看一遍");

        Assert.Equal("artifacts/gpu-screenshots/after-launcher.png", Assert.Single(tokens));
    }

    [Fact]
    public void MentionPathIsNotReMatchedAsAbsoluteFragment()
    {
        // 关键回归: `@a/b.png` 只能算一个 token, 不能把 `/b.png` 片段再抽成"独立绝对路径"。
        var tokens = ChatPane.MatchImagePathTokens("@artifacts/gpu-screenshots/after-launcher.png");

        Assert.Single(tokens);
    }
}
