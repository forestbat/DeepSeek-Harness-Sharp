using Dsh.Fleet;
using Dsh.Fleet.Iroh;

namespace Dsh.Tests;

public sealed class IrohMultiplexingTests
{
    [Fact]
    public async Task ExistingTcpMaster_CannotExecuteCommandsForAnIrohEndpoint()
    {
        _ = FleetSshEndToEndTests.RequireEnvironment();
        _ = IrohSshComponent.RequireExecutable();
        var ct = TestContext.Current.CancellationToken;
        await using var server = await TestSshServer.StartAsync(ct);
        // OpenSSH adds a random suffix while binding, so the socket itself uses the shallow unique path.
        var socket = TempTree.CreateSocketDirectory("m");
        Directory.Delete(socket);
        var source = server.Machine("master", server.Home);
        var master = source with
        {
            SshOptions = [.. source.SshOptions!.Where(option => !option.StartsWith("Control", StringComparison.OrdinalIgnoreCase)),
                "ControlMaster=auto", "ControlPersist=60", $"ControlPath={socket}"],
        };
        try
        {
            Assert.Equal("tcp-master-ok", await new SshTransport(master).RunAsync("printf '%s' tcp-master-ok", ct));
            await TestSshServer.RunAsync("ssh", ["-S", socket, "-O", "check", $"{source.User}@{source.ConnectionHost}"], ct);
            var endpoint = master with { Kind = MachineKinds.Iroh, Host = "", EndpointId = new string('0', MachineSpec.IrohEndpointIdLength), RelayUrls = ["http://127.0.0.1:9"] };
            using var attempt = CancellationTokenSource.CreateLinkedTokenSource(ct);
            attempt.CancelAfter(TimeSpan.FromSeconds(5));
            try
            {
                var observed = await new SshTransport(endpoint, new IrohSshTransport()).CaptureAsync("printf '%s' multiplexed-tcp-bypass; touch route-bypassed", attempt.Token);
                Assert.NotEqual(0, observed.ExitCode);
            }
            catch (OperationCanceledException) when (attempt.IsCancellationRequested && !ct.IsCancellationRequested)
            {
            }
            Assert.False(File.Exists(Path.Combine(server.Home, "route-bypassed")));
            Assert.Equal("tcp-master-still-ok", await new SshTransport(master).RunAsync("printf '%s' tcp-master-still-ok", ct));
        }
        finally
        {
            if (File.Exists(socket))
                await TestSshServer.RunAsync("ssh", ["-S", socket, "-O", "exit", $"{source.User}@{source.ConnectionHost}"], CancellationToken.None);
            File.Delete(socket);
        }
    }
}
