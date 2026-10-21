using Dsh.Boot;
using Dsh.Fleet;
using Dsh.Fleet.Iroh;

namespace Dsh.Tests;

public sealed class IrohSshTransportTests
{
    private static MachineSpec Machine => MachineSpec.Ssh("home", "bootstrap.invalid", 22, "dev", SshAuth.Agent) with
    {
        Kind = MachineKinds.Iroh,
        EndpointId = new string('a', MachineSpec.IrohEndpointIdLength),
    };

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("127.0.0.1:22")]
    [InlineData("host.example.org")]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaag")]
    public async Task InvalidEndpointCannotFallBackToOrdinaryTcp(string? endpoint)
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() => new SshTransport(Machine with { EndpointId = endpoint }, new IrohSshTransport()).CaptureAsync("true", TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData("ProxyCommand=nc localhost 22")]
    [InlineData("proxycommand nc localhost 22")]
    [InlineData("ProxyJump=jump")]
    public async Task ExplicitIrohCannotBeOverriddenByAnotherSshRoute(string option)
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() => new SshTransport(Machine with { SshOptions = [option] }, new IrohSshTransport()).CaptureAsync("true", TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData("not-a-url")]
    [InlineData("file:///etc/passwd")]
    [InlineData("https://user:secret@relay.example.org/")]
    public void InvalidRelayDoesNotReplaceSavedMachine(string relay)
    {
        var root = TempTree.CreateDirectory("iroh-config");
        try
        {
            var store = new MachineStore(new HarnessHome(root));
            store.Upsert(Machine);

            Assert.Throws<InvalidOperationException>(() => store.Upsert(Machine with { RelayUrls = [relay] }));

            Assert.Null(Assert.Single(store.Load().Machines).RelayUrls);
        }
        finally
        {
            TempTree.Delete(root);
        }
    }
}
