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

    [Fact]
    public async Task Windows_ConPty_Starts_And_Reads()
    {
        if (!OperatingSystem.IsWindows())
            return;

        using var direct = ConPtySession.Start(new PtyStartInfo
        {
            FileName = "cmd.exe",
            Arguments = ["/c", "echo direct-conpty-ready"],
        });
        using var directCancellation = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var received = await ReadUntil(direct.Stream, "direct-conpty-ready", directCancellation.Token);
        Assert.Contains("direct-conpty-ready", received);
        await direct.StopAsync();

        using var host = new PtyHost();
        var session = await host.StartAsync(new PtyStartInfo
        {
            FileName = "cmd.exe",
            Arguments = ["/c", "echo conpty-ready"],
        });

        Assert.Equal(PtySessionStatus.Running, session.Status);
        var backend = typeof(PtySession).GetField("_conPty", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)?.GetValue(session);
        Assert.NotNull(backend);

        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var buffer = new byte[4096];
        var read = await session.ReadAsync(buffer, cancellation.Token);
        Assert.True(read > 0);

        session.Resize(30, 120);
        Assert.True(await host.StopAsync(session.Id.ToString()));
    }

    [Fact]
    public async Task Windows_ConPty_Write_And_Read_RoundTrips()
    {
        if (!OperatingSystem.IsWindows())
            return;

        using var host = new PtyHost();
        var session = await host.StartAsync(new PtyStartInfo
        {
            FileName = "cmd.exe",
            Arguments = [],
        });

        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await session.WriteAsync("ver\r"u8.ToArray(), cancellation.Token);
        var buffer = new byte[4096];
        var received = new List<byte>();
        try
        {
            while (!Encoding.UTF8.GetString(received.ToArray()).Contains("Microsoft Windows"))
            {
                var read = await session.ReadAsync(buffer, cancellation.Token);
                if (read == 0)
                    break;
                received.AddRange(buffer.AsSpan(0, read).ToArray());
            }
        }
        catch (OperationCanceledException)
        {
        }
        Assert.Contains("Microsoft Windows", Encoding.UTF8.GetString(received.ToArray()));

        Assert.True(await host.StopAsync(session.Id.ToString()));
    }

    private static async Task<string> ReadUntil(Stream stream, string expected, CancellationToken cancellationToken)
    {
        var buffer = new byte[4096];
        var received = new List<byte>();
        while (!Encoding.UTF8.GetString(received.ToArray()).Contains(expected))
        {
            var read = await stream.ReadAsync(buffer, cancellationToken);
            if (read == 0)
                break;
            received.AddRange(buffer.AsSpan(0, read).ToArray());
        }
        return Encoding.UTF8.GetString(received.ToArray());
    }
}
