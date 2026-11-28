using System.Diagnostics;
using Dsh.RemoteHost;

namespace Dsh.Fleet;

/** Connects the host protocol over an SSH stdio command; SSH authentication and process ownership live in SshTransport. */
public static class SshHostLauncher
{
    public sealed class Connection : IAsyncDisposable
    {
        private readonly SshCommandChannel _channel;
        public RemoteHostClient Client { get; }
        public Process Process => _channel.Process;

        internal Connection(RemoteHostClient client, SshCommandChannel channel)
        {
            Client = client;
            _channel = channel;
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                await Client.DisposeAsync().ConfigureAwait(false);
            }
            finally
            {
                _channel.Dispose();
            }
        }
    }

    public static async Task<Connection> ConnectAsync(MachineSpec machine, string? hostCommandPath, string clientName, CancellationToken cancellationToken, ISshTransportAdapter? adapter = null)
    {
        var channel = new SshTransport(machine, adapter).OpenCommand(HarnessCommand(machine, hostCommandPath));
        return await ConnectChannelAsync(channel, $"ssh {machine.Target} 失败（认证方式 {machine.Auth}）", clientName, cancellationToken).ConfigureAwait(false);
    }

    internal static async Task<Connection> ConnectChannelAsync(
        SshCommandChannel channel,
        string failurePrefix,
        string clientName,
        CancellationToken cancellationToken)
    {
        try
        {
            var client = await RemoteHostClient.ConnectAsync(channel.Stream, token: null, clientName, cancellationToken).ConfigureAwait(false);
            channel.AuthenticationCompleted();
            return new Connection(client, channel);
        }
        catch (Exception error)
        {
            var detail = channel.ErrorOutput;
            if (detail.Length == 0)
                detail = channel.BannerOutput;
            if (detail.Length == 0)
                detail = error.Message;
            channel.Dispose();
            if (error is HostProtocolMismatchException mismatch)
                throw new HostProtocolMismatchException(mismatch.LocalVersion, mismatch.RemoteVersion, detail);
            throw new IOException($"{failurePrefix}: {detail}", error);
        }
    }

    internal static string HarnessCommand(MachineSpec machine, string? hostCommandPath)
    {
        var environment = RemoteShell.HomeAssignment(machine.HarnessHome);
        if (!string.IsNullOrEmpty(hostCommandPath))
        {
            // Absolute paths may target Windows: quote whitespace in a form accepted by cmd and POSIX shells.
            var command = hostCommandPath.Any(char.IsWhiteSpace) ? $"\"{hostCommandPath}\"" : hostCommandPath;
            return $"{environment}{command} host --serve --stdio";
        }
        var launcher = string.IsNullOrEmpty(machine.RemotePath) ? "dsharp" : "./dsharp";
        var prefix = string.IsNullOrEmpty(machine.RemotePath) ? "" : $"cd {RemoteShell.Quote(machine.RemotePath)} && ";
        return $"{prefix}{environment}{launcher} host --serve --stdio";
    }
}
