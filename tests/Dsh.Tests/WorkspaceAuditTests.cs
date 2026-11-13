using Dsh.Gui.Services;
using Dsh.Workspace;

namespace Dsh.Tests;

[Collection(SerialProcessCollection.Name)]
public sealed class WorkspaceAuditTests
{
    [Theory]
    [InlineData("/", RemotePathStyle.Posix, "/image.png")]
    [InlineData(@"\", RemotePathStyle.Windows, @"\image.png")]
    [InlineData("/", RemotePathStyle.Windows, @"\image.png")]
    [InlineData(@"C:\", RemotePathStyle.Windows, @"C:\image.png")]
    [InlineData("C:/", RemotePathStyle.Windows, @"C:\image.png")]
    [InlineData(@"\\server\share\", RemotePathStyle.Windows, @"\\server\share\image.png")]
    [InlineData(@"/workspace\", RemotePathStyle.Posix, @"/workspace\/image.png")]
    [InlineData("", RemotePathStyle.Posix, "image.png")]
    public void RelativePaths_PreserveTheRemoteDirectory(string cwd, RemotePathStyle style, string expected)
        => Assert.Equal(expected, RemotePaths.Resolve(cwd, "image.png", style));

    [Fact(Timeout = 60000)]
    public async Task ImageReference_FromPosixRoot_ReadsTheActualRemoteFile()
    {
        if (OperatingSystem.IsWindows())
            Assert.Skip("The remote filesystem for this integration scenario must use POSIX paths.");
        var signal = TestContext.Current.CancellationToken;
        await using var host = await HarnessHostFixture.CreateAsync("w", signal);
        var client = await host.ConnectAsync("workspace-path-audit", signal);
        var bytes = await File.ReadAllBytesAsync(
            Path.Combine(TempTree.RepositoryRoot, "src", "Dsh.Gui", "Assets", "logo-mark.png"), signal);
        var imagePath = Path.Combine(host.Environment.App.Home.Root, "workspace.png");
        await File.WriteAllBytesAsync(imagePath, bytes, signal);
        Assert.Equal(bytes, await client.Files.ReadFileAsync(imagePath, signal));

        var (text, images) = await PromptImages.ExtractRemoteAsync(
            client.Files, "/", RemotePathStyle.Posix, $"Describe @{Path.GetRelativePath("/", imagePath)}", signal);

        var image = Assert.Single(images);
        Assert.Equal("Describe", text);
        Assert.Equal("workspace.png", image.Name);
        Assert.Equal("image/png", image.MediaType);
        Assert.Equal(bytes, Convert.FromBase64String(image.Base64));
    }
}
