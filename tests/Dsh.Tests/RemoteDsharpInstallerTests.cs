using Dsh.Gui.Services;

namespace Dsh.Tests;

/** 远端 dsharp 自动部署: RID 映射与 Release 资产 URL 的纯逻辑。 */
public sealed class RemoteDsharpInstallerTests
{
    [Theory]
    [InlineData("Linux", "x86_64", "linux-x64")]
    [InlineData("linux", "amd64", "linux-x64")]
    [InlineData("Linux", "aarch64", "linux-arm64")]
    [InlineData("Darwin", "arm64", "osx-arm64")]
    [InlineData("Darwin", "x86_64", "osx-x64")]
    public void Rid_Maps_Supported_Platforms(string os, string arch, string expected)
        => Assert.Equal(expected, RemoteDsharpInstaller.RidFor(os, arch));

    [Theory]
    [InlineData("Windows", "x86_64")]
    [InlineData("Linux", "mips")]
    public void Rid_Returns_Null_For_Unsupported(string os, string arch)
        => Assert.Null(RemoteDsharpInstaller.RidFor(os, arch));

    [Fact]
    public void Asset_Url_Uses_Latest_By_Default()
    {
        var options = new RemoteDsharpInstaller.Options("https://host/releases", "latest", null);
        Assert.Equal("https://host/releases/latest/download/dsharp-linux-x64.tar.gz", RemoteDsharpInstaller.AssetUrl(options, "linux-x64"));
    }

    [Fact]
    public void Asset_Url_Uses_Tag_For_Pinned_Version()
    {
        var options = new RemoteDsharpInstaller.Options("https://host/releases/", "1.2.3", null);
        Assert.Equal("https://host/releases/download/v1.2.3/dsharp-linux-x64.tar.gz", RemoteDsharpInstaller.AssetUrl(options, "linux-x64"));
        Assert.Equal("https://host/releases/download/v1.2.3/dsharp-osx-arm64.tar.gz", RemoteDsharpInstaller.AssetUrl(options with { Version = "v1.2.3" }, "osx-arm64"));
    }
}
