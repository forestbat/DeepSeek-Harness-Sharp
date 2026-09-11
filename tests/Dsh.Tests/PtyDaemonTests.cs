using Dsh.Pty;

namespace Dsh.Tests;

public class PtyDaemonTests
{
    private static readonly string TestRoot = Path.Combine(FindRepositoryRoot(), ".daemon-tests");

    [Fact]
    public async Task List_ReturnsEmpty_WhenNoSessionsExist()
    {
        if (OperatingSystem.IsWindows())
            return;

        var socketPath = CreateSocketPath();
        try
        {
            await using var daemon = new PtyDaemon(socketPath: socketPath);
            await daemon.StartAsync();

            var sessions = await PtyDaemonClient.ListAsync(socketPath, null);

            Assert.Empty(sessions);
        }
        finally
        {
            DeleteSocket(socketPath);
        }
    }

    [Fact]
    public async Task Start_ThenList_ContainsStartedSession()
    {
        if (OperatingSystem.IsWindows())
            return;

        var socketPath = CreateSocketPath();
        try
        {
            await using var daemon = new PtyDaemon(socketPath: socketPath);
            await daemon.StartAsync();

            var started = await PtyDaemonClient.StartAsync(new PtyDaemonStartParams
            {
                FileName = "/bin/sh",
                Arguments = ["-c", "sleep 5"],
            }, socketPath, null);

            var info = Assert.Single(await PtyDaemonClient.ListAsync(socketPath, null));
            Assert.Equal(started.Id, info.Id);
            Assert.Equal("Running", info.Status);
        }
        finally
        {
            DeleteSocket(socketPath);
        }
    }

    [Fact]
    public async Task Dispose_RemovesSocketFile()
    {
        if (OperatingSystem.IsWindows())
            return;

        var socketPath = CreateSocketPath();
        var daemon = new PtyDaemon(socketPath: socketPath);
        await daemon.StartAsync();
        Assert.True(File.Exists(socketPath));

        await daemon.DisposeAsync();

        Assert.False(File.Exists(socketPath));
    }

    private static string CreateSocketPath()
    {
        Directory.CreateDirectory(TestRoot);
        var name = $"d{Guid.NewGuid():N}"[..8] + ".sock";
        return Path.Combine(TestRoot, name);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "DeepSeek-Harness-Sharp.sln")))
            directory = directory.Parent;
        return directory?.FullName ?? AppContext.BaseDirectory;
    }

    private static void DeleteSocket(string socketPath)
    {
        if (File.Exists(socketPath))
            File.Delete(socketPath);
    }
}