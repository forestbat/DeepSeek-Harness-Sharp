using System.Globalization;
using System.Net.WebSockets;
using System.Text.Json;
using Dsh.Fleet;
using Dsh.RemoteHost;
using Dsh.Transport;
using Dsh.WebBridge;

namespace Dsh.Tests;

/**
 * Hub mode over real ssh: the bridge deploys dsharp to a user-mode sshd remote and every browser shares the Fleet connection, without per-browser SSH tunnels.
 * Same sshd recipe as FleetSshEndToEndTests: test keys and known_hosts in the test directory, so the real ~/.ssh and ~/.dsharp stay untouched.
 */
[Collection(SerialProcessCollection.Name)]
public sealed class WebBridgeSshTunnelTests
{
    private const string RemoteMachine = "e2e";

    private static readonly TimeSpan DeployTimeout = TimeSpan.FromSeconds(OperatingSystem.IsMacOS() ? 20 : 5);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task BrowserSockets_ShareOneDeployedSshConnection_AndTerminalSurvivesInitiatingPageClose()
    {
        var ct = TestContext.Current.CancellationToken;
        var executable = FleetSshEndToEndTests.RequireEnvironment();
        var guard = FleetSshEndToEndTests.RealHomeGuard.Capture();
        await using var server = await TestSshServer.StartAsync(ct);
        var bridgeHome = RealHostProcesses.CreateHome("wt");
        var remoteHome = TempTree.CreateSocketDirectory("wr");
        try
        {
            var machine = server.Machine(RemoteMachine, remoteHome);
            var options = FleetConnectionTests.TestOptions with
            {
                Deploy = new DeploySettings { Via = DownloadVia.Client },
                ArchiveFactory = (_, rid, token) => FleetSshEndToEndTests.BuildArchiveAsync(executable, rid, server.Root, token),
            };
            await using var bridge = WebBridgeServer.Start(new WebBridgeOptions(bridgeHome, 0, [MachineSpec.Local(bridgeHome), machine], options));
            var access = new BridgeAccess(bridge.Port, LocalEndpointToken.Read(WebBridgeEndpoint.PortFile(bridgeHome))!);
            await access.WaitForMachinesAsync(states => states[RemoteMachine] == "Connected", DeployTimeout, ct);
            Assert.Equal(1, SshChildren(machine.Port));

            var cookies = await access.LoginAsync(ct);
            using var first = await access.ConnectWebSocketAsync(cookies, access.Origin, ct, RemoteMachine);
            using var second = await access.ConnectWebSocketAsync(cookies, access.Origin, ct, RemoteMachine);
            foreach (var socket in new[] { first, second })
            {
                await WebBridgeHubTests.HelloAsync(socket);
                Assert.Equal(remoteHome, (await WebSocketRpc.RequestAsync(socket, 2, HostProtocol.MethodInfo, null)).GetProperty("HomeRoot").GetString());
            }

            Assert.Equal(1, SshChildren(machine.Port));
            var started = await WebSocketRpc.RequestAsync(first, 3, PtyProtocol.MethodPtyStart, $$"""{"FileName":"/bin/sh","Arguments":["-c","echo tunnel-$((6*7)); sleep 30"],"Cwd":{{JsonSerializer.Serialize(remoteHome)}}}""");
            var ptyId = started.GetProperty("PtyId").GetString()!;
            await WebSocketRpc.SendAsync(second, 4, PtyProtocol.MethodPtyAttach, $$"""{"PtyId":"{{ptyId}}","ViewId":"ssh-terminal"}""");
            await WebBridgeHubTests.ReadTerminalUntilAsync(second, ptyId, "tunnel-42");

            await first.CloseAsync(WebSocketCloseStatus.NormalClosure, "", ct);
            Assert.Equal(1, SshChildren(machine.Port));
        }
        finally
        {
            RealHostProcesses.KillAll(remoteHome);
            TempTree.Delete(remoteHome);
            TempTree.Delete(bridgeHome);
        }

        guard.AssertUnchanged();
    }

    /** Live SSH transports this test process started for the sshd: exactly one shared Fleet connection, independent of open browser pages. */
    private static int SshChildren(int port)
    {
        var parent = Environment.ProcessId.ToString(CultureInfo.InvariantCulture);
        var portText = port.ToString(CultureInfo.InvariantCulture);
        return RealHostProcesses.ProcessListing("ps", "-A", "-o", "ppid=", "-o", "stat=", "-o", "args=")
            .Select(line => line.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            .Count(fields => fields is [var ppid, var state, var program, .. var arguments]
                && ppid == parent && !state.StartsWith('Z') && Path.GetFileName(program) == "ssh"
                && arguments.Zip(arguments.Skip(1)).Any(pair => pair is ("-p", var value) && value == portText));
    }
}
