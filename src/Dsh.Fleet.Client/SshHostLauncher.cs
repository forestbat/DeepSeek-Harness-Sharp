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
        try
        {
            var client = await RemoteHostClient.ConnectAsync(channel.Stream, token: null, clientName, cancellationToken).ConfigureAwait(false);
            channel.AuthenticationCompleted();
            return new Connection(client, channel);
        }
        catch (Exception error)
        {
            var detail = channel.ErrorOutput;
            channel.Dispose();
            throw new IOException($"ssh {machine.Target} 失败（认证方式 {machine.Auth}）: {detail}", error);
        }
    }

    private static string HarnessCommand(MachineSpec machine, string? hostCommandPath)
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
