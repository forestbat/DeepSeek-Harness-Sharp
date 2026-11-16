using System.Diagnostics;
using System.Net.Sockets;
using Dsh.Ma2a.Services;

namespace Dsh.Ma2a.Tests;

public sealed class LocalTransferBoundaryTests : IDisposable
{
    private readonly string _directory = CreateDirectory();

    private static string CreateDirectory()
    {
        var ancestor = new DirectoryInfo(AppContext.BaseDirectory);
        while (ancestor is not null && !Directory.Exists(Path.Combine(ancestor.FullName, ".git")) && !File.Exists(Path.Combine(ancestor.FullName, ".git")))
            ancestor = ancestor.Parent;
        if (ancestor is null) throw new InvalidOperationException("Transfer boundary tests require a repository-local build.");
        return Directory.CreateDirectory(Path.Combine(ancestor.FullName, ".ma2a-tests", Guid.NewGuid().ToString("N"))).FullName;
    }

    [Theory]
    [Trait("Platform", "UnixOnly")]
    [InlineData(Ma2aFileTransfer.PartialFileSuffix)]
    [InlineData($"{Ma2aFileTransfer.PartialFileSuffix}.meta")]
    [InlineData($"{Ma2aFileTransfer.PartialFileSuffix}.meta.new")]
    [InlineData($"{Ma2aFileTransfer.PartialFileSuffix}.lock")]
    public async Task DownloadRefusesLinkedSidecarsBeforeContactingRuntime(string suffix)
    {
        var protectedFile = Path.Combine(_directory, "protected.txt");
        await File.WriteAllTextAsync(protectedFile, "preserve", TestContext.Current.CancellationToken);
        var destination = Path.Combine(_directory, "result.bin");
        File.CreateSymbolicLink($"{destination}{suffix}", protectedFile);
        var client = new Ma2aClient(Path.Combine(_directory, "runtime"));
        await Assert.ThrowsAsync<NotSupportedException>(() => Ma2aFileTransfer.DownloadAsync(client, new string('1', 64),
            "data:/file", destination, new string('2', 32), cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal("preserve", await File.ReadAllTextAsync(protectedFile, TestContext.Current.CancellationToken));
    }

    [Fact]
    [Trait("Platform", "UnixOnly")]
    public async Task UploadRefusesAFifoBeforeOpeningOrContactingRuntime()
    {
        var path = Path.Combine(_directory, "input.fifo");
        var start = new ProcessStartInfo("mkfifo") { UseShellExecute = false };
        start.ArgumentList.Add(path);
        using var mkfifo = Process.Start(start) ?? throw new InvalidOperationException("FIFO creation did not start.");
        await mkfifo.WaitForExitAsync(TestContext.Current.CancellationToken);
        Assert.Equal(0, mkfifo.ExitCode);
        var client = new Ma2aClient(Path.Combine(_directory, "runtime"));
        await Assert.ThrowsAsync<NotSupportedException>(() => Ma2aFileTransfer.UploadAsync(client, new string('1', 64),
            "data:/file", path, new string('2', 32), cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task NativeCliResumeFilesArePreservedIndependentlyOfDshState()
    {
        var destination = Path.Combine(_directory, "result.bin");
        var rustPart = $"{destination}.ma2a-part";
        var rustMetadata = $"{rustPart}.meta";
        var rustLock = $"{rustPart}.lock";
        foreach (var path in new[] { rustPart, rustMetadata, rustLock })
            await File.WriteAllTextAsync(path, "native-cli-state", TestContext.Current.CancellationToken);
        var client = new Ma2aClient(Path.Combine(_directory, "runtime"));
        var error = await Record.ExceptionAsync(() => Ma2aFileTransfer.DownloadAsync(client, new string('1', 64),
            "data:/file", destination, new string('2', 32), cancellationToken: TestContext.Current.CancellationToken));
        Assert.NotNull(error);
        Assert.True(File.Exists($"{destination}{Ma2aFileTransfer.PartialFileSuffix}"));
        foreach (var path in new[] { rustPart, rustMetadata, rustLock })
            Assert.Equal("native-cli-state", await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken));
    }

    [Fact]
    [Trait("Platform", "UnixOnly")]
    public async Task ExistingDownloadIdentityRemainsReadableWithItsOriginalJsonFields()
    {
        var destination = Path.Combine(_directory, "resumed.bin");
        var partial = $"{destination}{Ma2aFileTransfer.PartialFileSuffix}";
        var identity = $$"""{"Endpoint":"{{new string('1', 64)}}","Path":"data:/file","Size":4,"ModifiedNanoseconds":18446744073709551615}""";
        await File.WriteAllBytesAsync(partial, new byte[] { 0, 255 }, TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync($"{partial}.meta", identity, TestContext.Current.CancellationToken);
        var repository = Directory.GetParent(_directory)!.Parent!.FullName;
        var controlPath = Path.Combine(repository, "run-v1", "control.sock");
        Assert.False(File.Exists(controlPath));
        _ = new UnixDomainSocketEndPoint(controlPath);
        var client = new Ma2aClient(repository);

        await Assert.ThrowsAsync<SocketException>(() => Ma2aFileTransfer.DownloadAsync(client, new string('1', 64),
            "data:/file", destination, new string('2', 32), cancellationToken: TestContext.Current.CancellationToken));

        Assert.False(TestContext.Current.CancellationToken.IsCancellationRequested);
        Assert.Equal(new byte[] { 0, 255 }, await File.ReadAllBytesAsync(partial, TestContext.Current.CancellationToken));
        Assert.Equal(identity, await File.ReadAllTextAsync($"{partial}.meta", TestContext.Current.CancellationToken));
        Assert.False(File.Exists(destination));
    }

    public void Dispose() => Directory.Delete(_directory, recursive: true);
}
