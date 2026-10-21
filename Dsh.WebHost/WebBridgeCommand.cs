using System.Globalization;
using System.Net.Sockets;
using Dsh.Boot;
using Dsh.Fleet;
using Dsh.Fleet.Iroh;
using Dsh.Transport;
using Dsh.Runtime;

namespace Dsh.WebBridge;

/**
 * `dsharp web [--port N] [--print-token]` (also `dsharp host web`): runs the bridge until cancelled, or prints the running bridge's access token.
 * The bridge serves this computer and every machine of the harness home's machine list (the fleet section of settings.yaml).
 */
public static class WebBridgeCommand
{
    public const string PortOption = "--port";
    public const string PrintTokenOption = "--print-token";

    private const string Usage = "usage: dsharp web [--port N] [--print-token]";

    private readonly record struct WebBridgeArguments(int Port, bool PrintToken, string? Error);

    /**
     * ensureHostAsync runs before the bridge starts listening, e.g. to start the host daemon of this harness home.
     * startLocalHost starts the host of another local machine (another harness home) when it is not reachable; only the executable knows how to launch itself.
     */
    public static async Task<int> RunAsync(
        string root,
        IReadOnlyList<string> arguments,
        TextWriter output,
        TextWriter error,
        Func<Task> ensureHostAsync,
        CancellationToken cancellationToken,
        Func<MachineSpec, CancellationToken, Task>? startLocalHost = null,
        OutboundProxy? localProxy = null,
        WebBridgeRuntime? runtime = null)
    {
        var (port, printToken, problem) = Parse(arguments);
        if (problem is not null)
        {
            await error.WriteLineAsync($"dsharp: {problem} ({Usage})").ConfigureAwait(false);
            return 2;
        }

        if (printToken)
            return await PrintTokenAsync(root, output, error, cancellationToken).ConfigureAwait(false);
        await ensureHostAsync().ConfigureAwait(false);
        using var ownedRuntime = runtime is null ? new WebBridgeRuntime(localProxy) : null;
        var owner = runtime ?? ownedRuntime!;
        var options = Options(root, port, startLocalHost, localProxy ?? owner.Proxy);
        return await owner.RunAsync(options, (fleet, token) => ServeAsync(options, fleet, output, error, token), cancellationToken).ConfigureAwait(false);
    }

    private static WebBridgeArguments Parse(IReadOnlyList<string> arguments)
    {
        var port = WebBridgeEndpoint.DefaultPort;
        var printToken = false;
        for (var index = 0; index < arguments.Count; index++)
        {
            switch (arguments[index])
            {
                case PrintTokenOption:
                    printToken = true;
                    break;
                case PortOption when index + 1 < arguments.Count
                    && ushort.TryParse(arguments[index + 1], NumberStyles.None, CultureInfo.InvariantCulture, out var parsed):
                    port = parsed;
                    index++;
                    break;
                default:
                    return new WebBridgeArguments(port, printToken, $"unexpected or invalid argument \"{arguments[index]}\"");
            }
        }

        return new WebBridgeArguments(port, printToken, null);
    }

    /** Prints the token alone on the first line (for scripts), then the one-time login URL that carries it in the fragment. */
    private static async Task<int> PrintTokenAsync(string root, TextWriter output, TextWriter error, CancellationToken cancellationToken)
    {
        var portFile = WebBridgeEndpoint.PortFile(root);
        var token = LocalEndpointToken.Read(portFile);
        var portText = File.Exists(portFile) ? await File.ReadAllTextAsync(portFile, cancellationToken).ConfigureAwait(false) : "";
        if (token is null || !int.TryParse(portText.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var port))
        {
            await error.WriteLineAsync($"dsharp: no web bridge is running for {root}; start one with `dsharp web`").ConfigureAwait(false);
            return 1;
        }

        await output.WriteLineAsync(token).ConfigureAwait(false);
        await output.WriteLineAsync($"{WebBridgeEndpoint.LocalUrl(port)}#token={token}").ConfigureAwait(false);
        return 0;
    }

    /** This computer first (the default target), then the stored machines, deployed with the stored preferences. */
    private static WebBridgeOptions Options(string root, int port, Func<MachineSpec, CancellationToken, Task>? startLocalHost, OutboundProxy? localProxy)
    {
        var config = new MachineStore(new HarnessHome(root)).Load();
        var fleet = new FleetOptions { ClientName = WebBridgeServer.DefaultClientName, Deploy = config.Deploy, StartLocalHost = startLocalHost, LocalProxy = localProxy ?? new OutboundProxy(), SshTransportAdapter = new IrohSshTransport() };
        return new WebBridgeOptions(root, port, [MachineSpec.Local(root), .. config.Machines], fleet);
    }

    private static async Task<int> ServeAsync(WebBridgeOptions options, FleetClient fleet, TextWriter output, TextWriter error, CancellationToken cancellationToken)
    {
        WebBridgeServer server;
        try
        {
            server = WebBridgeServer.Start(options, fleet);
        }
        catch (IOException failure) when (failure.InnerException is Microsoft.AspNetCore.Connections.AddressInUseException)
        {
            await error.WriteLineAsync($"dsharp: port {options.Port} on 127.0.0.1 is already in use; pick another with {PortOption} N").ConfigureAwait(false);
            return 1;
        }

        await using (server)
        {
            await PrintBannerAsync(output, options, server.Port).ConfigureAwait(false);
            try
            {
                await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }

        return 0;
    }

    /** Where to open the page on this computer, how to log in, which machines it reaches, and how a phone or another computer gets to the loopback-only port. */
    private static async Task PrintBannerAsync(TextWriter output, WebBridgeOptions options, int port)
    {
        var url = WebBridgeEndpoint.LocalUrl(port);
        var login = $"ssh -N -L {port}:127.0.0.1:{port} {Environment.UserName}@{Environment.MachineName}";
        await output.WriteLineAsync($"dsharp web bridge listening on {url} (loopback only)").ConfigureAwait(false);
        await output.WriteLineAsync($"machines: {string.Join(", ", (options.Machines ?? []).Select(machine => $"{machine.Name} ({machine.Target})"))}").ConfigureAwait(false);
        await output.WriteLineAsync($"web access token: {WebBridgeEndpoint.TokenFile(options.HostRoot)} (new on every start; show it with `dsharp web {PrintTokenOption}`)").ConfigureAwait(false);
        await output.WriteLineAsync($"first login: open {url}#token=<token>, or paste the token into the login page").ConfigureAwait(false);
        await output.WriteLineAsync($"from another computer: {login}, then open {url} there").ConfigureAwait(false);
        await output.WriteLineAsync($"through a jump host: ssh -N -J <user>@<jump-host> -L {port}:127.0.0.1:{port} {Environment.UserName}@{Environment.MachineName}").ConfigureAwait(false);
        await output.WriteLineAsync($"from a phone or tablet: forward local port {port} to 127.0.0.1:{port} on this computer in an SSH client app (e.g. Termius, JuiceSSH, Blink), then open {url} in the phone's browser and paste the token").ConfigureAwait(false);
    }
}
