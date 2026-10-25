using Dsh.RemoteHost;
using Dsh.Transport;

namespace Dsh.Tests;

/** host.file.* and host.fs.* against a real host: chunked binary transfers, atomic replacement, directory operations and the path boundary. */
[Collection(SerialProcessCollection.Name)]
public sealed class RemoteHostFileTransferTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact(Explicit = true)]
    public async Task BinaryFileLargerThanOneChunk_RoundTripsThroughUploadAndDownload()
    {
        await using var host = await HarnessHostFixture.CreateAsync("mft", TestContext.Current.CancellationToken);
        var client = await host.ConnectAsync("files", Ct);
        var directory = Path.Combine(host.Environment.App.Home.Root, "transfer");
        await client.Files.CreateDirectoryAsync(Path.Combine(directory, "nested"), Ct);
        var content = new byte[(HostFileSystem.TransferChunkBytes * 5 / 2) + 17];
        new Random(20261015).NextBytes(content);
        var target = Path.Combine(directory, "nested", "blob.bin");

        var stat = await client.Files.UploadFileAsync(target, new MemoryStream(content), cancellationToken: Ct);
        using var downloaded = new MemoryStream();
        var copied = await client.Files.DownloadFileAsync(target, downloaded, Ct);

        Assert.True(client.Supports(FilesProtocol.Capability));
        Assert.Equal((true, false, (long)content.Length), (stat.Exists, stat.IsDirectory, stat.Size));
        Assert.Equal(content.Length, copied);
        Assert.Equal(content, downloaded.ToArray());
        Assert.Equal(content, await File.ReadAllBytesAsync(target, Ct));
        Assert.Equal(["blob.bin"], Directory.GetFileSystemEntries(Path.GetDirectoryName(target)!).Select(Path.GetFileName));
    }

    [Fact(Explicit = true)]
    public async Task Upload_ReplacesTheTargetOnlyOnCommit_AndRejectsOutOfOrderChunks()
    {
        await using var host = await HarnessHostFixture.CreateAsync("mfa", TestContext.Current.CancellationToken);
        var client = await host.ConnectAsync("files", Ct);
        var target = Path.Combine(host.Environment.App.Home.Root, "config.json");
        await client.Files.WriteFileAsync(target, "old"u8.ToArray(), Ct);

        var upload = await client.Files.BeginUploadAsync(target, cancellationToken: Ct);
        var received = await client.Files.UploadChunkAsync(upload.UploadId, 0, "new-"u8.ToArray(), Ct);
        await Assert.ThrowsAsync<JsonRpcException>(() => client.Files.UploadChunkAsync(upload.UploadId, 0, "new-"u8.ToArray(), Ct));
        var midway = await client.Files.ReadFileAsync(target, Ct);
        await client.Files.UploadChunkAsync(upload.UploadId, received, "content"u8.ToArray(), Ct);
        var committed = await client.Files.CommitUploadAsync(upload.UploadId, 11, Ct);

        Assert.Equal(4, received);
        Assert.Equal("old"u8.ToArray(), midway);
        Assert.Equal(11, committed.Size);
        Assert.Equal("new-content"u8.ToArray(), await client.Files.ReadFileAsync(target, Ct));
        Assert.DoesNotContain(Directory.GetFiles(host.Environment.App.Home.Root), path => path.EndsWith(".part", StringComparison.Ordinal));
    }

    [Fact(Explicit = true)]
    public async Task UnfinishedUpload_IsDiscardedWhenItsConnectionEnds()
    {
        await using var host = await HarnessHostFixture.CreateAsync("mfu", TestContext.Current.CancellationToken);
        var directory = Path.Combine(host.Environment.App.Home.Root, "drop");
        Directory.CreateDirectory(directory);
        var uploader = await host.ConnectAsync("uploader", Ct);
        var upload = await uploader.Files.BeginUploadAsync(Path.Combine(directory, "half.bin"), cancellationToken: Ct);
        await uploader.Files.UploadChunkAsync(upload.UploadId, 0, new byte[1024], Ct);
        Assert.Single(Directory.GetFiles(directory));

        await uploader.DisposeAsync();

        await WaitUntilAsync(() => Directory.GetFiles(directory).Length == 0);
        var other = await host.ConnectAsync("other", Ct);
        Assert.False((await other.Files.StatAsync(Path.Combine(directory, "half.bin"), Ct)).Exists);
    }

    [Fact(Explicit = true)]
    public async Task DirectoryOperations_StatMkdirRenameDelete()
    {
        await using var host = await HarnessHostFixture.CreateAsync("mfd", TestContext.Current.CancellationToken);
        var client = await host.ConnectAsync("files", Ct);
        var root = Path.Combine(host.Environment.App.Home.Root, "ops");
        await client.Files.CreateDirectoryAsync(Path.Combine(root, "a", "b"), Ct);
        await client.Files.WriteFileAsync(Path.Combine(root, "a", "b", "f.txt"), "x"u8.ToArray(), Ct);
        await client.Files.WriteFileAsync(Path.Combine(root, "other.txt"), "y"u8.ToArray(), Ct);

        await client.Files.RenameAsync(Path.Combine(root, "a", "b", "f.txt"), Path.Combine(root, "a", "g.txt"), cancellationToken: Ct);
        await Assert.ThrowsAsync<JsonRpcException>(() => client.Files.RenameAsync(Path.Combine(root, "a", "g.txt"), Path.Combine(root, "other.txt"), cancellationToken: Ct));
        await client.Files.RenameAsync(Path.Combine(root, "a", "g.txt"), Path.Combine(root, "other.txt"), overwrite: true, Ct);
        await Assert.ThrowsAsync<JsonRpcException>(() => client.Files.DeleteAsync(Path.Combine(root, "a"), cancellationToken: Ct));
        await client.Files.DeleteAsync(Path.Combine(root, "a"), recursive: true, Ct);

        var directory = await client.Files.StatAsync(root, Ct);
        var file = await client.Files.StatAsync(Path.Combine(root, "other.txt"), Ct);
        Assert.Equal((true, true), (directory.Exists, directory.IsDirectory));
        Assert.Equal((true, false, 1L), (file.Exists, file.IsDirectory, file.Size));
        Assert.Equal("x"u8.ToArray(), await client.Files.ReadFileAsync(Path.Combine(root, "other.txt"), Ct));
        Assert.False((await client.Files.StatAsync(Path.Combine(root, "a"), Ct)).Exists);
    }

    [Fact(Explicit = true)]
    public async Task PathsOutsideTheAllowedRoots_AreRejected_EvenThroughSymbolicLinks()
    {
        if (OperatingSystem.IsWindows())
            Assert.Skip("uses POSIX system paths");
        await using var host = await HarnessHostFixture.CreateAsync("mfp", TestContext.Current.CancellationToken);
        var client = await host.ConnectAsync("files", Ct);
        var link = Path.Combine(host.Environment.App.Home.Root, "etc-link");
        File.CreateSymbolicLink(link, "/etc");

        var direct = await Assert.ThrowsAsync<JsonRpcException>(() => client.Files.ReadFileAsync("/etc/hostname", Ct));
        var throughLink = await Assert.ThrowsAsync<JsonRpcException>(() => client.Files.ReadFileAsync(Path.Combine(link, "hostname"), Ct));
        await Assert.ThrowsAsync<JsonRpcException>(() => client.Files.DownloadChunkAsync("/etc/hostname", 0, cancellationToken: Ct));
        await Assert.ThrowsAsync<JsonRpcException>(() => client.Files.BeginUploadAsync("/etc/dsh-probe", cancellationToken: Ct));
        await Assert.ThrowsAsync<JsonRpcException>(() => client.Files.ListDirectoryAsync("/", Ct));
        var linkStat = await client.Files.StatAsync(link, Ct);

        Assert.Contains("outside", direct.Message, StringComparison.Ordinal);
        Assert.Contains("outside", throughLink.Message, StringComparison.Ordinal);
        Assert.True(linkStat.IsSymbolicLink);
        Assert.Null((await client.Files.ListDirectoryAsync("~", Ct)).Parent);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        while (!condition())
            await Task.Delay(20, timeout.Token);
    }
}


