using Dsh.Boot;
using Dsh.Fleet;
using Dsh.Fleet.Iroh;

namespace Dsh.Tests;

public sealed class IrohBootstrapEndToEndTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RejectedIrohHostKey_PreservesSshConfiguration_AndPreexistingReceiver(bool preexistingReceiver)
    {
        var executable = FleetSshEndToEndTests.RequireEnvironment();
        _ = IrohSshComponent.RequireExecutable();
        var ct = TestContext.Current.CancellationToken;
        await using var server = await TestSshServer.StartAsync(ct);
        var root = TempTree.CreateSocketDirectory("ib");
        var original = server.Machine("bootstrap", root);
        var transport = new SshTransport(original);
        try
        {
            Assert.Equal("ordinary-ssh", await transport.RunAsync("printf '%s' ordinary-ssh", ct));
            if (preexistingReceiver)
                await transport.RunAsync($"{RemoteShell.Prelude(original)}{RemoteShell.Quote(executable)} iroh-remote enable --ssh-port {original.Port}", ct);
            var previous = await IrohReceiver.InfoAsync(new HarnessHome(root), ct);
            var wrongKey = Path.Combine(server.Root, "untrusted_key");
            await TestSshServer.RunAsync("ssh-keygen", ["-q", "-t", "ed25519", "-N", "", "-f", wrongKey], ct);
            var hosts = Path.Combine(server.Root, "rejected_hosts");
            File.Copy(Path.Combine(server.Root, "known_hosts"), hosts);
            await File.AppendAllTextAsync(hosts, $"iroh-* {await File.ReadAllTextAsync($"{wrongKey}.pub", ct)}", ct);
            var source = original with { SshOptions = [$"UserKnownHostsFile={hosts}", .. original.SshOptions!] };
            var store = new MachineStore(new HarnessHome(Path.Combine(root, "client")));
            var options = FleetConnectionTests.TestOptions with
            {
                SshTransportAdapter = new IrohSshTransport(),
                Deploy = new DeploySettings { Via = DownloadVia.Client },
                ArchiveFactory = (_, rid, token) => FleetSshEndToEndTests.BuildArchiveAsync(executable, rid, server.Root, token),
            };
            store.Save(new FleetConfig([source], options.Deploy));
            var saved = await File.ReadAllBytesAsync(store.FilePath, ct);
            await Assert.ThrowsAsync<IOException>(() => IrohRemoteInstaller.EnableAndSaveAsync(source, options, converted => store.Upsert(converted), null, ct));
            Assert.Equal(saved, await File.ReadAllBytesAsync(store.FilePath, ct));
            var status = await IrohReceiver.InfoAsync(new HarnessHome(root), ct);
            Assert.Equal(preexistingReceiver, status.Running);
            if (preexistingReceiver)
                Assert.Equal(previous.EndpointId, status.EndpointId);
            Assert.Equal("ordinary-ssh-after-failure", await transport.RunAsync("printf '%s' ordinary-ssh-after-failure", ct));
        }
        finally
        {
            await IrohReceiver.DisableAsync(new HarnessHome(root), null, CancellationToken.None);
            TempTree.Delete(root);
        }
    }
}
