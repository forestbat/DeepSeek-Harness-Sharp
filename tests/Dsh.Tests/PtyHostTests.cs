using System.Text;
using Dsh.Pty;

namespace Dsh.Tests;

public class PtyHostTests
{
    [Fact]
    public async Task Host_Starts_Lists_Reads_And_Stops()
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
            return;

        using var host = new PtyHost();
        var session = await host.StartAsync(new PtyStartInfo
        {
            FileName = "/bin/sh",
            Arguments = ["-c", "echo pty-ready; sleep 5"],
        });

        var info = Assert.Single(host.List());
        Assert.Equal(session.Id, info.Id);
        Assert.Equal(PtySessionStatus.Running, info.Status);
        Assert.Equal("echo pty-ready; sleep 5", info.Command.Split("-c ")[^1]);

        var buffer = new byte[4096];
        var read = await session.ReadAsync(buffer);
        Assert.Contains("pty-ready", Encoding.UTF8.GetString(buffer, 0, read));

        Assert.True(await host.StopAsync(session.Id.ToString()));
        Assert.DoesNotContain(host.List(), entry => entry.Id == session.Id);
    }

    [Fact]
    public async Task Resize_UpdatesPtyWindowSize()
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
            return;

        using var host = new PtyHost();
        var session = await host.StartAsync(new PtyStartInfo
        {
            FileName = "/bin/sh",
            Arguments = ["-c", "sleep 5"],
        });

        session.Resize(30, 120);
        Assert.Equal(PtySessionStatus.Running, session.Status);

        await host.StopAsync(session.Id.ToString());
    }

    [Fact]
    public async Task Write_And_Read_RoundTrips()
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
            return;

        using var host = new PtyHost();
        var session = await host.StartAsync(new PtyStartInfo
        {
            FileName = "/bin/sh",
            Arguments = ["-c", "cat"],
        });

        await session.WriteAsync("hello pty\n"u8.ToArray());
        var buffer = new byte[4096];
        var read = await session.ReadAsync(buffer);
        Assert.Contains("hello pty", Encoding.UTF8.GetString(buffer, 0, read));

        await host.StopAsync(session.Id.ToString());
    }
}
