using Dsh.RemoteHost;

namespace Dsh.Host;

/**
 * Bridges one stdio connection to the resident host daemon; ending the bridge leaves host sessions alive.
 * 桥断开时两侧的异常都要写出来: 静默吞掉会让人只看到"连接已关闭", 无法判断是 daemon 连不上还是通道断了。
 */
public static class HostBridge
{
    public static async Task RunAsync(Stream duplex, string? root, CancellationToken cancellationToken)
    {
        using var daemon = await RemoteHostConnector.ConnectAsync(root, cancellationToken).ConfigureAwait(false);
        var toDaemon = PumpAsync(duplex, daemon, "client->daemon", cancellationToken);
        var toClient = PumpAsync(daemon, duplex, "daemon->client", cancellationToken);
        var first = await Task.WhenAny(toDaemon, toClient).ConfigureAwait(false);
        await Console.Error.WriteLineAsync($"dsharp: stdio bridge ended on {(ReferenceEquals(first, toDaemon) ? "client->daemon" : "daemon->client")}").ConfigureAwait(false);
    }

    private static async Task PumpAsync(Stream from, Stream to, string direction, CancellationToken cancellationToken)
    {
        var buffer = new byte[16384];
        try
        {
            int read;
            while ((read = await from.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
            {
                if (Environment.GetEnvironmentVariable("DSH_BRIDGE_TRACE") is { Length: > 0 })
                    await Console.Error.WriteLineAsync($"dsharp: {direction} {read}B {(direction.StartsWith("client") ? BitConverter.ToString(buffer, 0, Math.Min(8, read)) : "")}").ConfigureAwait(false);
                await to.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                await to.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            if (Environment.GetEnvironmentVariable("DSH_BRIDGE_TRACE") is { Length: > 0 })
                await Console.Error.WriteLineAsync($"dsharp: {direction} EOF").ConfigureAwait(false);
        }
        catch (Exception error)
        {
            await Console.Error.WriteLineAsync($"dsharp: stdio bridge {direction} failed: {error.GetType().Name}: {error.Message}").ConfigureAwait(false);
        }
    }
}
