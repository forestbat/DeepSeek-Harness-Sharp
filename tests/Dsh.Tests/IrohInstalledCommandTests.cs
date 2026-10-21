using System.Diagnostics;
using Dsh.Boot;
using Dsh.Fleet;
using Dsh.Fleet.Iroh;

namespace Dsh.Tests;

/** Discovery and cleanup fixtures only; executable copies and retained identity markers do not implement or validate the iroh protocol. */
public sealed class IrohInstalledCommandTests
{
    // These variables coordinate isolated test processes and are never product configuration.
    private const string RootVariable = "DSH_IROH_DISCOVERY_ROOT";
    private const string ExpectedVariable = "DSH_IROH_DISCOVERY_EXPECTED";

    [Theory(Timeout = 30000)]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task Discovery_Uses_Only_Executable_Path_Candidates_In_An_Isolated_Process(bool installed, bool unusableFirst)
    {
        var root = TempTree.CreateDirectory("iroh discovery % path");
        try
        {
            var first = Path.Combine(root, "first");
            var second = Path.Combine(root, "second");
            Directory.CreateDirectory(first);
            Directory.CreateDirectory(second);
            var name = OperatingSystem.IsWindows() ? "iroh-ssh.exe" : "iroh-ssh";
            if (unusableFirst)
            {
                var unusable = Path.Combine(first, name);
                if (OperatingSystem.IsWindows())
                    Directory.CreateDirectory(unusable);
                else
                {
                    await File.WriteAllTextAsync(unusable, "discovery-only non-executable fixture", TestContext.Current.CancellationToken);
                    File.SetUnixFileMode(unusable, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                }
            }
            var candidate = Path.Combine(second, name);
            if (installed)
            {
                File.Copy(OperatingSystem.IsWindows() ? Path.Combine(Environment.SystemDirectory, "cmd.exe") : "/bin/sh", candidate);
                if (!OperatingSystem.IsWindows())
                    File.SetUnixFileMode(candidate, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }
            await RunChildAsync(nameof(DiscoveryChild), root, string.Join(Path.PathSeparator, first, second), installed ? candidate : "", TestContext.Current.CancellationToken);
        }
        finally
        {
            TempTree.Delete(root);
        }
    }

    [Fact(Explicit = true, Timeout = 30000)]
    public async Task DiscoveryChild()
    {
        RequireChildRoot();
        var expected = Environment.GetEnvironmentVariable(ExpectedVariable);
        if (string.IsNullOrEmpty(expected))
        {
            Assert.Null(IrohSshComponent.FindExecutable());
            Assert.Throws<FileNotFoundException>(() => IrohSshComponent.RequireExecutable());
            var machine = MachineSpec.Ssh("unavailable", "192.0.2.1", MachineSpec.DefaultSshPort, "fixture", SshAuth.Agent) with
            {
                Kind = MachineKinds.Iroh,
                EndpointId = new string('A', MachineSpec.IrohEndpointIdLength),
            };
            await Assert.ThrowsAsync<FileNotFoundException>(() => new SshTransport(machine, new IrohSshTransport()).CaptureAsync("true", TestContext.Current.CancellationToken));
        }
        else
            Assert.Equal(expected, IrohSshComponent.RequireExecutable());
    }

    [Fact(Timeout = 30000)]
    public async Task PosixInstalledSymlink_ResolvesTheExecutedFileForReceiverOwnership()
    {
        if (OperatingSystem.IsWindows())
            Assert.Skip("POSIX command symlink and executable identity boundary.");
        var root = TempTree.CreateDirectory("iroh-command-symlink");
        try
        {
            var target = Path.Combine(root, "actual-command");
            File.Copy("/bin/sh", target);
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(target, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            File.CreateSymbolicLink(Path.Combine(root, "iroh-ssh"), target);
            await RunChildAsync(nameof(DiscoveryChild), root, root, target, TestContext.Current.CancellationToken);
        }
        finally
        {
            TempTree.Delete(root);
        }
    }

    [Fact(Timeout = 30000)]
    public async Task PosixInstalledDirectorySymlink_ResolvesTheExecutedFileForReceiverOwnership()
    {
        if (OperatingSystem.IsWindows())
            Assert.Skip("POSIX installed directory and executable identity boundary.");
        var root = TempTree.CreateDirectory("iroh-directory-symlink");
        try
        {
            var directory = Directory.CreateDirectory(Path.Combine(root, "real")).FullName;
            var executable = Path.Combine(directory, "iroh-ssh");
            File.Copy("/bin/sh", executable);
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(executable, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            var alias = Path.Combine(root, "alias");
            Directory.CreateSymbolicLink(alias, directory);
            await RunChildAsync(nameof(DiscoveryChild), root, alias, executable, TestContext.Current.CancellationToken);
        }
        finally
        {
            TempTree.Delete(root);
        }
    }

    [Fact(Timeout = 30000)]
    public async Task Missing_Command_Does_Not_Prevent_Verified_Owned_Process_Cleanup_Or_Hide_Identity_Failure()
    {
        if (OperatingSystem.IsWindows())
            Assert.Skip("POSIX detached receiver ownership boundary.");
        var root = TempTree.CreateDirectory("iroh-uninstalled-cleanup");
        try
        {
            await RunChildAsync(nameof(OwnedCleanupChild), root, "", "", TestContext.Current.CancellationToken);
        }
        finally
        {
            TempTree.Delete(root);
        }
    }

    [Fact(Explicit = true, Timeout = 30000)]
    public async Task OwnedCleanupChild()
    {
        var root = RequireChildRoot();
        var ct = TestContext.Current.CancellationToken;
        Assert.Null(IrohSshComponent.FindExecutable());
        using var receiver = Process.Start(new ProcessStartInfo("/bin/cat")
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
        })!;
        try
        {
            await receiver.StandardInput.WriteLineAsync("owned-cleanup-ready".AsMemory(), ct);
            Assert.Equal("owned-cleanup-ready", await receiver.StandardOutput.ReadLineAsync(ct));
            var home = new HarnessHome(root);
            var state = IrohReceiver.ReceiverRoot(home);
            Directory.CreateDirectory(Path.Combine(state, "identity"));
            var key = Path.Combine(state, "identity", "irohssh_ed25519");
            byte[] identity = [1, 2, 3, 4];
            await File.WriteAllBytesAsync(key, identity, ct);
            await File.WriteAllTextAsync(Path.Combine(state, "mode"), "detached", ct);
            await File.WriteAllTextAsync(Path.Combine(state, "process"), $"{receiver.Id}\n{IrohReceiver.ProcessStartIdentity(receiver)}\n{receiver.MainModule!.FileName}\n", ct);
            await Assert.ThrowsAsync<FileNotFoundException>(() => IrohReceiver.DisableAsync(home, null, ct));
            Assert.True(receiver.HasExited);
            Assert.False(File.Exists(Path.Combine(state, "mode")));
            Assert.False(File.Exists(Path.Combine(state, "process")));
            Assert.Equal(identity, await File.ReadAllBytesAsync(key, ct));
        }
        finally
        {
            if (!receiver.HasExited)
                receiver.Kill();
            await receiver.WaitForExitAsync(CancellationToken.None);
        }
    }

    private static string RequireChildRoot()
    {
        var root = Environment.GetEnvironmentVariable(RootVariable);
        if (string.IsNullOrEmpty(root))
            Assert.Skip("Isolated installed-command child entry point.");
        return root!;
    }

    private static async Task RunChildAsync(string method, string root, string path, string expected, CancellationToken cancellationToken)
    {
        var filename = $"{typeof(IrohInstalledCommandTests).Assembly.GetName().Name}{(OperatingSystem.IsWindows() ? ".exe" : "")}";
        var start = new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory, filename))
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var argument in new[] { "-method", $"{typeof(IrohInstalledCommandTests).FullName}.{method}", "-explicit", "on", "-parallelMode", "none", "-trait-", "Category=LlmSmoke" })
            start.ArgumentList.Add(argument);
        start.Environment["PATH"] = path;
        start.Environment[RootVariable] = root;
        start.Environment[ExpectedVariable] = expected;
        using var child = Process.Start(start)!;
        var stdout = child.StandardOutput.ReadToEndAsync(CancellationToken.None);
        var stderr = child.StandardError.ReadToEndAsync(CancellationToken.None);
        try
        {
            await child.WaitForExitAsync(cancellationToken);
        }
        finally
        {
            if (!child.HasExited)
            {
                try
                {
                    child.Kill(entireProcessTree: true);
                }
                catch (InvalidOperationException) when (child.HasExited)
                {
                }
            }
            await child.WaitForExitAsync(CancellationToken.None);
        }
        Assert.True(child.ExitCode == 0, $"{await stdout}\n{await stderr}");
    }
}
