using Dsh.RemoteHost;

namespace Dsh.Tests;

/** 客户端侧远端路径解析: 风格由**远端**平台决定, 与客户端本地 OS 无关。 */
public sealed class RemotePathsTests
{
    [Theory]
    [InlineData("/tmp/s.png", "/tmp/s.png")]
    [InlineData("~/a/b.png", "~/a/b.png")]
    [InlineData("sub/s.png", "/home/u/proj/sub/s.png")]
    [InlineData("\"shot.png\"", "/home/u/proj/shot.png")]
    public void Resolve_Posix(string token, string expected)
        => Assert.Equal(expected, RemotePaths.Resolve("/home/u/proj", token, RemotePathStyle.Posix));

    [Theory]
    [InlineData(@"C:\x\y.png", @"C:\x\y.png")]
    [InlineData("D:/x/y.png", "D:/x/y.png")]
    [InlineData(@"sub\s.png", @"C:\work\proj\sub\s.png")]
    [InlineData("sub/s.png", @"C:\work\proj\sub/s.png")]
    public void Resolve_Windows(string token, string expected)
        => Assert.Equal(expected, RemotePaths.Resolve(@"C:\work\proj", token, RemotePathStyle.Windows));

    [Fact]
    public void Resolve_EmptyCwd_LeavesRelative()
    {
        Assert.Equal("sub/s.png", RemotePaths.Resolve("", "sub/s.png", RemotePathStyle.Posix));
        Assert.Equal("sub\\s.png", RemotePaths.Resolve("", "sub\\s.png", RemotePathStyle.Windows));
    }

    [Fact]
    public void StyleFor_MapsPlatforms()
    {
        Assert.Equal(RemotePathStyle.Windows, RemotePaths.StyleFor("windows"));
        Assert.Equal(RemotePathStyle.Windows, RemotePaths.StyleFor("Windows"));
        Assert.Equal(RemotePathStyle.Posix, RemotePaths.StyleFor("linux"));
        Assert.Equal(RemotePathStyle.Posix, RemotePaths.StyleFor("macos"));
    }

    [Fact]
    public void DirectoryNameAndFileName_ArePosix()
    {
        Assert.Equal("/home/u/proj/a", RemotePaths.DirectoryName("/home/u/proj/a/b.png", RemotePathStyle.Posix));
        Assert.Equal("/", RemotePaths.DirectoryName("/a.png", RemotePathStyle.Posix));
        Assert.Equal("", RemotePaths.DirectoryName("b.png", RemotePathStyle.Posix));
        Assert.Equal("b.png", RemotePaths.FileName("/home/u/proj/a/b.png", RemotePathStyle.Posix));
        Assert.Equal(".png", RemotePaths.Extension("/home/u/proj/a/b.png", RemotePathStyle.Posix));
    }

    [Fact]
    public void DirectoryNameAndFileName_AreWindows()
    {
        Assert.Equal(@"C:\work\proj\a", RemotePaths.DirectoryName(@"C:\work\proj\a\b.png", RemotePathStyle.Windows));
        Assert.Equal(@"C:\work\proj\a", RemotePaths.DirectoryName(@"C:\work\proj\a/b.png", RemotePathStyle.Windows));
        Assert.Equal("b.png", RemotePaths.FileName(@"C:\work\proj\a\b.png", RemotePathStyle.Windows));
        Assert.Equal(".png", RemotePaths.Extension(@"C:\work\proj\a\b.png", RemotePathStyle.Windows));
    }
}
