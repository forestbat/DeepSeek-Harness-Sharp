namespace Dsh.Fleet.Iroh;

/** Converts a reachable SSH machine only after deploying the receiver and executing a command through its iroh tunnel. */
public static class IrohRemoteInstaller
{
    public static async Task<MachineSpec> EnableAndSaveAsync(MachineSpec machine, FleetOptions options, Action<MachineSpec> persist, Action<string>? progress, CancellationToken cancellationToken)
    {
        if (machine.Kind != MachineKinds.Ssh)
            throw new ArgumentException("Automatic iroh setup requires a reachable normal SSH machine; use dsharp iroh-remote enable on the target for local setup.", nameof(machine));
        MachineSpec.ValidateRelayUrls(machine.RelayUrls ?? []);
        _ = IrohSshComponent.RequireExecutable();
        progress?.Invoke("Deploying the current dsharp receiver over the existing SSH connection …");
        var transport = new SshTransport(machine);
        var commandPath = await new RemoteDsharpInstaller(transport, options.SshTransportAdapter).EnsureReceiverAsync(options.Deploy, progress, cancellationToken, options.ArchiveFactory, options.LocalProxy, options.LocalHome).ConfigureAwait(false);
        var connection = (await transport.RunAsync("printf '%s' \"$SSH_CONNECTION\"", cancellationToken).ConfigureAwait(false))
            .Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (connection is not [_, _, _, var portText] || !int.TryParse(portText, out var sshPort) || sshPort is <= 0 or > ushort.MaxValue)
            throw new IOException("Cannot determine the target sshd's listening port from the SSH connection; run dsharp iroh-remote enable --ssh-port on the target.");
        var receiverCommand = $"{RemoteShell.Prelude(machine)}{RemoteShell.Quote(commandPath)} iroh-remote";
        var previous = ParseStatus(await transport.RunAsync($"{receiverCommand} info", cancellationToken).ConfigureAwait(false));
        var startingReceiver = false;
        var activation = Guid.NewGuid().ToString("N");
        try
        {
            var endpoint = previous.EndpointId;
            if (!previous.Running)
            {
                var arguments = $"enable --ssh-port {sshPort} --activation {activation}";
                foreach (var relay in machine.RelayUrls ?? [])
                    arguments = $"{arguments} --relay-url {RemoteShell.Quote(relay)}";
                progress?.Invoke("Starting the target's user-owned iroh receiver …");
                startingReceiver = true;
                var enabled = ParseStatus(await transport.RunAsync($"{receiverCommand} {arguments}", cancellationToken).ConfigureAwait(false));
                if (!enabled.Running)
                    throw new IOException("The target did not start its iroh receiver.");
                endpoint = enabled.EndpointId;
            }
            var converted = machine with
            {
                Kind = MachineKinds.Iroh,
                EndpointId = endpoint,
                Port = sshPort,
                Proxy = null,
                SshOptions = machine.SshOptions?.Where(option => !IsProxyOverride(option)).ToArray(),
                RelayUrls = machine.RelayUrls?.ToArray(),
            };
            progress?.Invoke($"Verifying SSH authentication and a real remote command through iroh endpoint {endpoint} …");
            var observed = ParseStatus(await new SshTransport(converted, options.SshTransportAdapter).RunAsync($"{receiverCommand} info", cancellationToken).ConfigureAwait(false));
            if (!observed.Running || !string.Equals(observed.EndpointId, endpoint, StringComparison.Ordinal))
                throw new IOException("The receiver reached through iroh did not report the expected running persistent endpoint.");
            cancellationToken.ThrowIfCancellationRequested();
            persist(converted);
            return converted;
        }
        catch (Exception failure)
        {
            if (startingReceiver)
                await RollbackAsync(transport, receiverCommand, activation, failure).ConfigureAwait(false);
            throw;
        }
    }

    private static async Task RollbackAsync(IRemoteMachineTransport transport, string receiverCommand, string activation, Exception failure)
    {
        using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try
        {
            await transport.RunAsync($"{receiverCommand} disable --activation {activation}", cleanup.Token).ConfigureAwait(false);
        }
        catch (Exception rollbackFailure)
        {
            throw new IOException($"{failure.Message} Rollback could not disable the newly started receiver: {rollbackFailure.Message}. Run dsharp iroh-remote disable on the target.", new AggregateException(failure, rollbackFailure));
        }
    }

    internal static IrohReceiverStatus ParseStatus(string output)
    {
        var lines = output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        var endpoints = lines.Where(line => line.StartsWith("endpointId=", StringComparison.Ordinal)).ToArray();
        var states = lines.Where(line => line.StartsWith("running=", StringComparison.Ordinal)).ToArray();
        if (endpoints.Length != 1 || states.Length != 1 || !bool.TryParse(states[0]["running=".Length..], out var running))
            throw new IOException("The target did not return exactly one endpointId= and running= receiver status.");
        var endpoint = endpoints[0]["endpointId=".Length..].Trim();
        if ((running || endpoint.Length > 0) && (endpoint.Length != MachineSpec.IrohEndpointIdLength || endpoint.Any(c => !char.IsAsciiHexDigit(c))))
            throw new IOException("The target returned an invalid iroh endpoint ID; expected exactly 64 hexadecimal characters.");
        return new IrohReceiverStatus(endpoint.ToLowerInvariant(), running, "");
    }

    private static bool IsProxyOverride(string option)
    {
        var name = option.Split(['=', ' ', '\t'], 2, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        return string.Equals(name, "ProxyCommand", StringComparison.OrdinalIgnoreCase)
            || string.Equals(name, "ProxyJump", StringComparison.OrdinalIgnoreCase);
    }

}
