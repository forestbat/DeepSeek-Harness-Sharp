using Dsh.Boot;
using Dsh.Fleet;
using Dsh.Fleet.Iroh;
using Dsh.RemoteHost;
using Dsh.Transport;

namespace Dsh.Tests;

[Collection(SerialProcessCollection.Name)]
public sealed class IrohHostEndToEndTests
{
    [Fact]
    public async Task EndpointOnlyMachine_DeploysFreshHostAndPreservesItsPtyAcrossReconnect()
    {
        var executable = FleetSshEndToEndTests.RequireEnvironment();
        var ct = TestContext.Current.CancellationToken;
        await using var server = await TestSshServer.StartAsync(ct);
        var root = TempTree.CreateSocketDirectory("ih");
        var source = server.Machine("iroh-host", root);
        try
        {
            var receiver = IrohRemoteInstaller.ParseStatus(await new SshTransport(source).RunAsync($"{RemoteShell.Prelude(source)}{RemoteShell.Quote(executable)} iroh-remote enable --ssh-port {source.Port}", ct));
            var endpoint = source with { Kind = MachineKinds.Iroh, Host = "192.0.2.1", EndpointId = receiver.EndpointId };
            var options = FleetConnectionTests.TestOptions with
            {
                SshTransportAdapter = new IrohSshTransport(),
                Deploy = new DeploySettings { Via = DownloadVia.Client },
                ArchiveFactory = (_, rid, token) => FleetSshEndToEndTests.BuildArchiveAsync(executable, rid, server.Root, token),
            };
            await using var connection = new MachineConnection(endpoint, options);
            var states = FleetConnectionTests.RecordStates(connection);
            connection.Start();
            await connection.WaitSettledAsync(ct);
            Assert.True(connection.State == MachineConnectionState.Connected, connection.Detail);
            var first = await connection.WaitConnectedAsync(ct);
            var identity = LocalEndpointToken.Read(RemoteHostEndpoint.EndpointFile(root));
            var session = await first.Client.Pty.StartPtyAsync(new RemotePtyStartRequest("/bin/sh", [], root, Title: "iroh-host-shell"), ct);
            await first.Client.Files.WriteFileAsync(Path.Combine(root, "rpc.txt"), "file-through-iroh"u8.ToArray(), ct);
            Assert.Equal("file-through-iroh", System.Text.Encoding.UTF8.GetString(await first.Client.Files.ReadFileAsync(Path.Combine(root, "rpc.txt"), ct)));
            await using (var capture = RemoteHostPtyTests.TerminalCapture.Attach(first.Client, session))
            {
                await capture.SendAsync("printf '%s\\n' \"$$\" > original.pid; echo original-$((6*7))\n");
                await capture.WaitForAsync("original-42", TimeSpan.FromSeconds(5));
            }
            var process = connection.LinkProcess!;
            process.Kill();
            await FleetConnectionTests.WaitForAsync(() => states.Contains(MachineConnectionState.Reconnecting) && connection.State == MachineConnectionState.Connected && connection.LinkProcess != process);
            var reconnected = await connection.WaitConnectedAsync(ct);
            Assert.Contains(await reconnected.Client.ListHostSessionsAsync(ct), item => item.SessionId == session);
            await using (var capture = RemoteHostPtyTests.TerminalCapture.Attach(reconnected.Client, session))
            {
                await capture.SendAsync("printf '%s\\n' \"$$\" > resumed.pid; echo resumed-$((8*9))\n");
                await capture.WaitForAsync("resumed-72", TimeSpan.FromSeconds(5));
            }
            Assert.Equal(await File.ReadAllTextAsync(Path.Combine(root, "original.pid"), ct), await File.ReadAllTextAsync(Path.Combine(root, "resumed.pid"), ct));
            Assert.Equal(identity, LocalEndpointToken.Read(RemoteHostEndpoint.EndpointFile(root)));
        }
        finally
        {
            await IrohReceiver.DisableAsync(new HarnessHome(root), null, CancellationToken.None);
            RealHostProcesses.KillAll(root);
            TempTree.Delete(root);
        }
    }
}
