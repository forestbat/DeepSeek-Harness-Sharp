using System.Diagnostics;
using Dsh.Boot;
using Dsh.Fleet;
using Dsh.Fleet.Iroh;

namespace Dsh.Tests;

public sealed class IrohReceiverTests
{
    [Fact]
    public async Task Unconfigured_Info_And_Repeated_Disable_Do_Not_Require_A_Component_Or_Create_State()
    {
        var root = TempTree.CreateDirectory("iroh-unconfigured");
        try
        {
            var home = new HarnessHome(root);
            var info = await IrohReceiver.InfoAsync(home, TestContext.Current.CancellationToken);
            var disabled = await IrohReceiver.DisableAsync(home, null, TestContext.Current.CancellationToken);
            var repeated = await IrohReceiver.DisableAsync(home, null, TestContext.Current.CancellationToken);
            Assert.False(info.Running);
            Assert.Empty(info.EndpointId);
            Assert.False(disabled.Running);
            Assert.False(repeated.Running);
            Assert.False(Directory.Exists(IrohReceiver.ReceiverRoot(home)));
        }
        finally
        {
            TempTree.Delete(root);
        }
    }

    [Theory(Timeout = 5000)]
    [InlineData(false, false, true)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    public async Task Disable_Stops_Only_The_Process_With_Matching_Creation_Time_And_Executable(bool staleTime, bool wrongExecutable, bool shouldStop)
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Skip("POSIX detached receiver ownership boundary");
            return;
        }
        var root = TempTree.CreateDirectory("iroh-owned-process");
        using var receiver = TestProcessScope.Start(new ProcessStartInfo("/bin/cat")
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
        })!;
        using var unrelated = TestProcessScope.Start("/bin/sleep", ["120"])!;
        try
        {
            const string ReadyLine = "receiver-ready";
            await receiver.StandardInput.WriteLineAsync(ReadyLine.AsMemory(), TestContext.Current.CancellationToken);
            Assert.Equal(ReadyLine, await receiver.StandardOutput.ReadLineAsync(TestContext.Current.CancellationToken));
            var home = new HarnessHome(root);
            var state = IrohReceiver.ReceiverRoot(home);
            Directory.CreateDirectory(state);
            File.WriteAllText(Path.Combine(state, "mode"), "detached");
            var startIdentity = $"{IrohReceiver.ProcessStartIdentity(receiver)}{(staleTime ? "-stale" : "")}";
            var executable = wrongExecutable ? "/not-the-receiver" : receiver.MainModule!.FileName;
            File.WriteAllText(Path.Combine(state, "process"), $"{receiver.Id}\n{startIdentity}\n{executable}\n");
            var info = await IrohReceiver.InfoAsync(home, TestContext.Current.CancellationToken);
            Assert.Equal(shouldStop, info.Running);
            var result = await IrohReceiver.DisableAsync(home, null, TestContext.Current.CancellationToken);
            Assert.False(result.Running);
            Assert.Equal(shouldStop, receiver.HasExited);
            Assert.False(unrelated.HasExited);
            Assert.False(File.Exists(Path.Combine(state, "mode")));
            Assert.False(File.Exists(Path.Combine(state, "process")));
            Assert.False((await IrohReceiver.DisableAsync(home, null, TestContext.Current.CancellationToken)).Running);
        }
        finally
        {
            if (!receiver.HasExited)
                receiver.Kill();
            if (!unrelated.HasExited)
                unrelated.Kill();
            await receiver.WaitForExitAsync(CancellationToken.None);
            await unrelated.WaitForExitAsync(CancellationToken.None);
            TempTree.Delete(root);
        }
    }

    [Fact]
    public async Task Corrupt_Process_Identity_Cannot_Stop_An_Unverified_Process()
    {
        var root = TempTree.CreateDirectory("iroh-invalid-process");
        try
        {
            var home = new HarnessHome(root);
            var state = IrohReceiver.ReceiverRoot(home);
            Directory.CreateDirectory(state);
            File.WriteAllText(Path.Combine(state, "mode"), "detached");
            File.WriteAllText(Path.Combine(state, "process"), "invalid\n");
            await Assert.ThrowsAsync<IOException>(() => IrohReceiver.DisableAsync(home, null, TestContext.Current.CancellationToken));
            Assert.True(File.Exists(Path.Combine(state, "mode")));
        }
        finally
        {
            TempTree.Delete(root);
        }
    }

    [Fact(Timeout = 5000)]
    public async Task Incomplete_Systemd_Registration_Can_Be_Disabled_Without_Leaving_A_Blocked_Receiver()
    {
        if (!OperatingSystem.IsLinux() || !File.Exists("/usr/bin/systemctl"))
            Assert.Skip("Requires a systemd user manager.");
        var ct = TestContext.Current.CancellationToken;
        using var manager = TestProcessScope.Start(new ProcessStartInfo("/usr/bin/systemctl", ["--user", "show", "--property=Version"])
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        })!;
        var stdout = manager.StandardOutput.ReadToEndAsync(ct);
        var stderr = manager.StandardError.ReadToEndAsync(ct);
        await manager.WaitForExitAsync(ct);
        await stdout;
        await stderr;
        if (manager.ExitCode != 0)
            Assert.Skip("Requires a reachable systemd user manager.");
        var root = TempTree.CreateDirectory("iroh-incomplete-systemd");
        try
        {
            var home = new HarnessHome(root);
            var state = IrohReceiver.ReceiverRoot(home);
            Directory.CreateDirectory(state);
            var unit = Path.Combine(state, $"{IrohReceiver.ServiceName(state)}.service");
            await File.WriteAllTextAsync(unit, "[Service]\nExecStart=/bin/true\n", ct);
            await File.WriteAllTextAsync(Path.Combine(state, "mode"), "systemd", ct);
            Assert.False((await IrohReceiver.DisableAsync(home, null, ct)).Running);
            Assert.False(File.Exists(unit));
            Assert.False(File.Exists(Path.Combine(state, "mode")));
            Assert.False((await IrohReceiver.DisableAsync(home, null, ct)).Running);
        }
        finally
        {
            TempTree.Delete(root);
        }
    }

    [Fact]
    public void Bootstrap_Parses_Only_One_Explicit_Hex_Endpoint()
    {
        var endpoint = new string('A', 64);
        Assert.Equal(endpoint.ToLowerInvariant(), IrohRemoteInstaller.ParseStatus($"service=user\r\nendpointId={endpoint}\r\nrunning=true\r\n").EndpointId);
        Assert.Throws<IOException>(() => IrohRemoteInstaller.ParseStatus($"endpointId={endpoint}\nendpointId={endpoint}\nrunning=true"));
        Assert.Throws<IOException>(() => IrohRemoteInstaller.ParseStatus("endpointId=localhost:22\nrunning=true"));
        Assert.Throws<IOException>(() => IrohRemoteInstaller.ParseStatus($"progress: endpointId={endpoint}\nrunning=true"));
        Assert.Throws<IOException>(() => IrohRemoteInstaller.ParseStatus($"endpointId={endpoint}\nrunning=unknown"));
        Assert.Throws<IOException>(() => IrohRemoteInstaller.ParseStatus("endpointId=\nrunning=true"));
        Assert.False(IrohRemoteInstaller.ParseStatus("endpointId=\nrunning=false").Running);
    }

}
