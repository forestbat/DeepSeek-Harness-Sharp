using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Dsh.Fleet;
using Dsh.RemoteHost;
using Dsh.Transport;
using Dsh.WebBridge;

namespace Dsh.Tests;

/**
 * Hub mode of the web bridge over the shared real host/PTY fixture: two harness homes stand in for two machines, plus an unreachable SSH machine whose spec carries secrets.
 * Covers the machine list endpoint (login, states, no secrets), choosing the target machine of a WebSocket, and refusing unknown or offline machines.
 */
[Collection(SerialProcessCollection.Name)]
public sealed class WebBridgeHubTests
{
    internal const string SecondMachine = "second";
    internal const string OfflineMachine = "lab";
    private const string PasswordSecret = "PASSWORD-SECRET-7f3a";
    private const string ProxySecret = "PROXY-SECRET-91bd";
    private const string OptionSecret = "OPTION-SECRET-c2e8";

    /** Credentials stored in the offline machine's spec; no text the bridge sends to a browser may contain them. */
    internal static readonly string[] SecretValues = [PasswordSecret, ProxySecret, OptionSecret];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Machines_RequireLogin_AndListEveryMachineWithoutSecrets()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var hub = await HubFixture.StartAsync();
        using var anonymous = BridgeAccess.CreateHttpClient(new CookieContainer());
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync(hub.Access.Url(WebBridgeServer.MachinesPath), ct)).StatusCode);

        var body = await hub.WaitForMachinesAsync(machines => machines[MachineSpec.LocalName] == "Connected" && machines[SecondMachine] == "Connected" && machines[OfflineMachine] == "Offline");
        var listed = JsonDocument.Parse(body).RootElement.EnumerateArray().ToList();

        Assert.Equal([MachineSpec.LocalName, SecondMachine, OfflineMachine], listed.Select(machine => machine.GetProperty("Machine").GetString()));
        Assert.Equal([MachineKinds.Local, MachineKinds.Local, MachineKinds.Ssh], listed.Select(machine => machine.GetProperty("Kind").GetString()));
        Assert.All(listed.Take(2), machine => Assert.Equal(HostProtocol.ReleaseVersion, machine.GetProperty("HostVersion").GetString()));
        Assert.Equal($"dev@127.0.0.1:{hub.ClosedPort}", listed[2].GetProperty("Target").GetString());
        foreach (var secret in (string[])[.. SecretValues, "\"Password\"", "\"KeyPath\"", "\"Proxy\"", "\"SshOptions\""])
            Assert.DoesNotContain(secret, body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WebSocket_ReachesTheNamedMachine_AndRunsATerminalThere()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var hub = await HubFixture.StartAsync();
        var cookies = await hub.Access.LoginAsync(ct);
        using var local = await hub.Access.ConnectWebSocketAsync(cookies, hub.Access.Origin, ct);
        using var second = await hub.Access.ConnectWebSocketAsync(cookies, hub.Access.Origin, ct, SecondMachine);
        await HelloAsync(local);
        await HelloAsync(second);

        Assert.Equal(hub.Local.Environment.App.Home.Root, (await WebSocketRpc.RequestAsync(local, 2, HostProtocol.MethodInfo, null)).GetProperty("HomeRoot").GetString());
        Assert.Equal(hub.Second.Environment.App.Home.Root, (await WebSocketRpc.RequestAsync(second, 2, HostProtocol.MethodInfo, null)).GetProperty("HomeRoot").GetString());

        var arguments = TestShell.RunThenWait(TestShell.EchoAnswer("hub"), 30);
        var started = await WebSocketRpc.RequestAsync(second, 3, PtyProtocol.MethodPtyStart, $$"""{"FileName":{{JsonSerializer.Serialize(TestShell.FileName)}},"Arguments":{{JsonSerializer.Serialize(arguments)}},"Cwd":{{JsonSerializer.Serialize(hub.Second.Environment.App.Home.Root)}},"Title":"hub-shell"}""");
        var ptyId = started.GetProperty("PtyId").GetString()!;
        // The snapshot push can overtake attach's own response, so the reply is not awaited before reading output.
        await WebSocketRpc.SendAsync(second, 4, PtyProtocol.MethodPtyAttach, $$"""{"PtyId":"{{ptyId}}","ViewId":"hub-terminal"}""");
        await ReadTerminalUntilAsync(second, ptyId, "hub-42");

        var onSecond = await WebSocketRpc.RequestAsync(second, 5, HostProtocol.MethodSessionsList, null);
        var onLocal = await WebSocketRpc.RequestAsync(local, 3, HostProtocol.MethodSessionsList, null);
        Assert.Contains(onSecond.EnumerateArray(), session => session.GetProperty("SessionId").GetString() == ptyId && session.GetProperty("Kind").GetString() == HostSessionKinds.Terminal);
        Assert.DoesNotContain(onLocal.EnumerateArray(), session => session.GetProperty("SessionId").GetString() == ptyId);
    }

    [Fact]
    public async Task UnknownMachine_IsRefused_AndOfflineMachineHasRedactedAuthoritativeView()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var hub = await HubFixture.StartAsync();
        var cookies = await hub.Access.LoginAsync(ct);
        using var unknown = BridgeAccess.CreateWebSocket(cookies, hub.Access.Origin);
        await Assert.ThrowsAsync<WebSocketException>(() => unknown.ConnectAsync(hub.Access.MachineWebSocketUri("../local"), ct));
        Assert.Equal(HttpStatusCode.NotFound, unknown.HttpStatusCode);

        await hub.WaitForMachinesAsync(machines => machines[OfflineMachine] == "Offline");
        using var offline = await hub.Access.ConnectWebSocketAsync(cookies, hub.Access.Origin, ct, OfflineMachine);
        var view = await WebSocketRpc.ReceiveUntilAsync(offline, message => message.TryGetProperty("method", out var method)
            && method.GetString() == "bridge.view" && message.GetProperty("params").GetProperty("State").GetString() == "Offline");
        var detail = view.GetProperty("params").GetProperty("Detail").GetString() ?? "";
        Assert.False(string.IsNullOrEmpty(detail));
        Assert.All(SecretValues, secret => Assert.DoesNotContain(secret, detail, StringComparison.Ordinal));
        Assert.Equal(WebSocketState.Open, offline.State);
    }

    internal static async Task HelloAsync(ClientWebSocket socket)
    {
        await WebSocketRpc.ReceiveUntilAsync(socket, message => message.TryGetProperty("method", out var method)
            && method.GetString() == "bridge.view" && message.GetProperty("params").GetProperty("State").GetString() == "Connected");
        var hello = await WebSocketRpc.RequestAsync(socket, 1, HostProtocol.MethodHello, $$"""{"ProtocolVersion":{{HostProtocol.Version}},"ClientName":"hub-test"}""");
        Assert.True(hello.GetProperty("Ok").GetBoolean());
    }

    /** Decodes host.pty.output pushes of one PTY until the screen text contains the expected output. */
    internal static async Task ReadTerminalUntilAsync(ClientWebSocket socket, string ptyId, string expected, string? viewId = null)
    {
        var screen = new StringBuilder();
        var seen = new List<string>();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        try
        {
            while (!screen.ToString().Contains(expected, StringComparison.Ordinal))
            {
                var output = await WebSocketRpc.ReceiveUntilAsync(socket, message =>
                {
                    seen.Add(message.ToString());
                    return message.TryGetProperty("method", out var method)
                        && method.GetString() == PtyProtocol.NotificationPtyOutput
                        && message.GetProperty("params").GetProperty("PtyId").GetString() == ptyId
                        && (viewId is null || message.GetProperty("params").GetProperty("ViewId").GetString() == viewId);
                }).WaitAsync(timeout.Token);
                screen.Append(Encoding.UTF8.GetString(Convert.FromBase64String(output.GetProperty("params").GetProperty("Base64").GetString()!)));
            }
        }
        catch (OperationCanceledException)
        {
            Assert.Fail($"no {expected} in: {screen}; messages: {string.Join("\n", seen)}");
        }
    }

    /**
     * Two shared host fixtures and a bridge serving both plus an SSH machine on a closed port.
     * The SSH machine stores a password, a proxy with credentials and an extra ssh option, none of which may reach the browser.
     */
    internal sealed class HubFixture : IAsyncDisposable
    {
        private readonly WebBridgeServer _bridge;

        private HubFixture(HarnessHostFixture local, HarnessHostFixture second, int closedPort, WebBridgeServer bridge)
        {
            Local = local;
            Second = second;
            ClosedPort = closedPort;
            _bridge = bridge;
            Access = new BridgeAccess(bridge.Port, LocalEndpointToken.Read(WebBridgeEndpoint.PortFile(local.Root)) ?? throw new InvalidOperationException("bridge issued no token"));
        }

        public HarnessHostFixture Local { get; }

        public HarnessHostFixture Second { get; }

        public int ClosedPort { get; }

        public BridgeAccess Access { get; }

        public static async Task<HubFixture> StartAsync()
        {
            var local = await HarnessHostFixture.CreateAsync("hl", Ct);
            HarnessHostFixture? second = null;
            try
            {
                second = await HarnessHostFixture.CreateAsync("hs", Ct);
                var closedPort = ClosedLoopbackPort();
                MachineSpec[] machines = [MachineSpec.Local(local.Root), MachineSpec.Local(second.Root, SecondMachine), SecretBearingMachine(closedPort)];
                var options = FleetConnectionTests.TestOptions with { Deploy = new DeploySettings { AutoDeploy = false } };
                return new HubFixture(local, second, closedPort, WebBridgeServer.Start(new WebBridgeOptions(local.Root, 0, machines, options)));
            }
            catch
            {
                if (second is not null)
                    await second.DisposeAsync();
                await local.DisposeAsync();
                throw;
            }
        }

        public Task<string> WaitForMachinesAsync(Func<Dictionary<string, string>, bool> condition)
            => Access.WaitForMachinesAsync(condition, TimeSpan.FromSeconds(5), Ct);

        /** Release the real host/PTY resources even when bridge shutdown fails. */
        public async ValueTask DisposeAsync()
        {
            try
            {
                await _bridge.DisposeAsync();
            }
            finally
            {
                try
                {
                    await Second.DisposeAsync();
                }
                finally
                {
                    await Local.DisposeAsync();
                }
            }
        }

        private static MachineSpec SecretBearingMachine(int closedPort)
            => MachineSpec.Ssh(OfflineMachine, IPAddress.Loopback.ToString(), closedPort, "dev", SshAuth.Agent) with
            {
                Password = PasswordSecret,
                Proxy = $"nc -X connect -x user:{ProxySecret}@127.0.0.1:{closedPort} %h %p",
                SshOptions = ["UserKnownHostsFile=/dev/null", "GlobalKnownHostsFile=/dev/null", "ControlMaster=no", "ControlPath=none", $"SetEnv=DSH_TEST={OptionSecret}"],
            };

        /** A loopback port nothing listens on: bound once to pick it, then released. */
        private static int ClosedLoopbackPort()
        {
            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            return ((IPEndPoint)listener.LocalEndpoint).Port;
        }
    }
}

