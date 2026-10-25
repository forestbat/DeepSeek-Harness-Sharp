using Dsh.Boot;
using Dsh.Fleet;
using Dsh.Fleet.Iroh;

namespace Dsh.Tests;

public sealed class IrohOutboundRouteTests
{
    private const string ConsumerUrl = "http://example.invalid/iroh-optional-consumer";

    [Theory]
    [InlineData("proxyCommand")]
    [InlineData("localForward")]
    public async Task SatelliteTransport_CarriesRealHttpConsumersThroughTheSelectedMachine(string mode)
    {
        var executable = FleetSshEndToEndTests.RequireEnvironment();
        var ct = TestContext.Current.CancellationToken;
        await using var ssh = await TestSshServer.StartAsync(ct);
        await using var remote = new TestForwardProxy();
        var root = TempTree.CreateSocketDirectory("io94");
        var source = ssh.Machine("iroh-outbound", root);
        var home = new HarnessHome(root);
        try
        {
            var receiver = IrohRemoteInstaller.ParseStatus(await new SshTransport(source).RunAsync($"{RemoteShell.Prelude(source)}{RemoteShell.Quote(executable)} iroh-remote enable --ssh-port {source.Port}", ct));
            var machine = source with
            {
                Kind = MachineKinds.Iroh,
                Host = "192.0.2.1",
                EndpointId = receiver.EndpointId,
                OutboundTunnel = new(mode, $"http://127.0.0.1:{remote.Port}"),
            };
            new MachineStore(home).Upsert(machine);
            HarnessSettings.SaveBorrowedOutboundMachine(home, machine.Name);
            using var fleet = new FleetService(home, adapter: new IrohSshTransport());
            await using var lease = await fleet.OutboundRoute.AcquireAsync(ct);
            using var consumer = new HttpClient(fleet.OutboundRoute.Proxy.CreateHandler());
            Assert.Equal(TestForwardProxy.ProxiedBody, await consumer.GetStringAsync(ConsumerUrl, ct));
            Assert.Contains($"GET {ConsumerUrl} HTTP/1.1", remote.Requests);
        }
        finally
        {
            await IrohReceiver.DisableAsync(home, null, CancellationToken.None);
            TempTree.Delete(root);
        }
    }
}
