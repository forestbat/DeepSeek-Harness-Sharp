using System.Net;
using System.Net.Sockets;
using Dsh.Sandbox;

namespace Dsh.Tests;

public sealed class SandboxProcessTests : IDisposable
{
    private readonly string _root = TempTree.CreateDirectory("sandbox-native");
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(15);
    public void Dispose() => TempTree.Delete(_root);

    private static async Task<SandboxProcess> StartNativeAsync(SandboxProcessOptions options)
    {
        await SandboxTestBackend.RequireAsync();
        return await SandboxedProcess.StartAsync(options, TestContext.Current.CancellationToken);
    }


    [Fact]
    [Trait("Category", "SandboxNative")]
    public async Task NativeStdio_RoundTripsWithoutProtocolPollution()
    {
        if (OperatingSystem.IsWindows())
            Assert.Skip("The Windows native batch test covers redirected native IO.");
        await using var sandbox = await StartNativeAsync(Options("/") with
        {
            Arguments = ["-c", "IFS= read -r value; printf '%s\\n' \"$value\"; printf '%s\\n' diagnostic >&2"]
        });
        await sandbox.StandardInput.WriteAsync("native-pipe\n"u8.ToArray(), TestContext.Current.CancellationToken);
        await sandbox.StandardInput.FlushAsync(TestContext.Current.CancellationToken);
        var output = new StreamReader(sandbox.StandardOutput).ReadToEndAsync(TestContext.Current.CancellationToken);
        var error = new StreamReader(sandbox.StandardError).ReadToEndAsync(TestContext.Current.CancellationToken);
        var exitCode = await sandbox.WaitForExitAsync(TestContext.Current.CancellationToken).WaitAsync(Deadline, TestContext.Current.CancellationToken);
        Assert.True(exitCode == 0, $"Sandbox PID {sandbox.Id} exited with {exitCode}: {await error}");
        Assert.Equal("native-pipe\n", await output);
        Assert.Equal("diagnostic\n", await error);
    }

    [Fact]
    [Trait("Category", "SandboxNative")]
    public async Task LinuxNativeIo_EnforcesDeclaredPathReadsAndWrites()
    {
        RequireLinuxPython();
        var working = Directory.CreateDirectory(Path.Combine(_root, "working")).FullName;
        var input = Path.Combine(_root, "readable.txt");
        var secret = Path.Combine(_root, "not-granted.txt");
        var denied = Path.Combine(working, "settings.yaml");
        File.WriteAllText(input, "readable");
        File.WriteAllText(secret, "host-only");
        File.WriteAllText(denied, "unchanged");
        const string script = """
            import os, sys
            root, readable, secret, denied = sys.argv[1:]
            with open(readable) as source:
                assert source.read() == 'readable'
            def blocked(path, mode):
                try:
                    with open(path, mode):
                        pass
                except OSError:
                    return
                raise RuntimeError('ungranted operation succeeded')
            blocked(secret, 'r')
            blocked(readable, 'w')
            blocked(denied, 'w')
            with open(os.path.join(root, 'allowed.txt'), 'w') as target:
                target.write('allowed')
            print('path-policy')
            """;
        await using var sandbox = await StartNativeAsync(PythonOptions(working) with
        {
            Arguments = ["-I", "-c", script, working, input, secret, denied],
            ReadOnlyPaths = [input], WritablePaths = [working], DeniedWritePaths = [denied]
        });
        var output = new StreamReader(sandbox.StandardOutput).ReadToEndAsync(TestContext.Current.CancellationToken);
        var error = new StreamReader(sandbox.StandardError).ReadToEndAsync(TestContext.Current.CancellationToken);
        var exit = await sandbox.WaitForExitAsync(TestContext.Current.CancellationToken).WaitAsync(Deadline, TestContext.Current.CancellationToken);
        Assert.True(exit == 0, $"The real Linux path consumer exited with {exit}: {await error}");
        Assert.Equal("path-policy\n", await output);
        Assert.Equal("allowed", File.ReadAllText(Path.Combine(working, "allowed.txt")));
        Assert.Equal("unchanged", File.ReadAllText(denied));
        Assert.Equal("readable", File.ReadAllText(input));
        Assert.Equal("host-only", File.ReadAllText(secret));
    }


    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("Category", "SandboxNative")]
    public async Task LinuxNativeSocket_UsesTheRequestedNetworkNamespace(bool allowNetwork)
    {
        RequireLinuxPython();
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        const string script = """
            import socket, sys
            try:
                with socket.socket() as connection:
                    connection.settimeout(2)
                    connection.connect(('127.0.0.1', int(sys.argv[1])))
            except OSError:
                print('blocked')
            else:
                print('connected')
            """;
        await using var sandbox = await StartNativeAsync(PythonOptions("/") with
        {
            Arguments = ["-I", "-c", script, port.ToString(System.Globalization.CultureInfo.InvariantCulture)], AllowNetwork = allowNetwork
        });
        var output = new StreamReader(sandbox.StandardOutput).ReadToEndAsync(TestContext.Current.CancellationToken);
        Assert.Equal(0, await sandbox.WaitForExitAsync(TestContext.Current.CancellationToken).WaitAsync(Deadline, TestContext.Current.CancellationToken));
        Assert.Equal(allowNetwork ? "connected\n" : "blocked\n", await output);
        Assert.Equal(allowNetwork, listener.Pending());
    }

    [Fact]
    [Trait("Category", "SandboxNative")]
    public async Task LinuxRootExit_TerminatesDescendantsHoldingStdio()
    {
        if (!OperatingSystem.IsLinux())
            Assert.Skip("This test exercises the Linux PID namespace boundary.");
        var sleep = new[] { "/usr/bin/sleep", "/bin/sleep" }.First(File.Exists);
        await using var sandbox = await StartNativeAsync(Options("/") with
        {
            Arguments = ["-c", "\"$1\" 60 & printf '%s\\n' ready; exit 0", "sandbox", sleep],
            RuntimePaths = UnixRuntimePaths().Append(sleep).ToArray(), AllowChildProcesses = true
        });
        var output = new StreamReader(sandbox.StandardOutput).ReadToEndAsync(TestContext.Current.CancellationToken);
        Assert.Equal(0, await sandbox.WaitForExitAsync(TestContext.Current.CancellationToken).WaitAsync(Deadline, TestContext.Current.CancellationToken));
        Assert.Equal("ready\n", await output.WaitAsync(Deadline, TestContext.Current.CancellationToken));
    }

    [Fact]
    [Trait("Category", "SandboxNative")]
    public async Task LinuxWaitCancellation_KillsOwnedTree()
    {
        if (!OperatingSystem.IsLinux())
            Assert.Skip("This test exercises Linux sandbox cancellation.");
        var sleep = new[] { "/usr/bin/sleep", "/bin/sleep" }.First(File.Exists);
        await using var sandbox = await StartNativeAsync(Options("/") with
        {
            Executable = sleep, Arguments = ["60"]
        });
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => sandbox.WaitForExitAsync(cancelled.Token).WaitAsync(Deadline, TestContext.Current.CancellationToken));
    }


    [Fact]
    [Trait("Category", "SandboxNative")]
    public async Task LoaderInjectionEnvironment_IsRejectedBeforeLaunchingTheTrustedWrapper()
    {
        await SandboxTestBackend.RequireAsync();
        await Assert.ThrowsAsync<ArgumentException>(() => SandboxedProcess.StartAsync(Options(_root) with
        {
            Environment = new Dictionary<string, string?> { ["LD_PRELOAD"] = "/not-a-permitted-loader" }
        }, TestContext.Current.CancellationToken));
    }


    [Fact]
    [Trait("Category", "SandboxNative")]
    public async Task MacNativeIo_DeniesCanonicalAndSymbolicAliasWrites()
    {
        if (!OperatingSystem.IsMacOS())
            Assert.Skip("This test exercises native shell IO under Seatbelt without spawning a child process.");
        var working = Directory.CreateDirectory(Path.Combine(_root, "working")).FullName;
        var target = Path.Combine(working, "settings.yaml");
        var alias = Path.Combine(working, "settings-alias");
        var allowed = Path.Combine(working, "allowed.txt");
        File.WriteAllText(target, "unchanged");
        File.CreateSymbolicLink(alias, target);
        const string script = """
            if printf forbidden > "$1"; then exit 91; fi
            if printf forbidden > "$2"; then exit 92; fi
            printf allowed > "$3"
            printf 'protected\n'
            """;
        await using var sandbox = await StartNativeAsync(Options(working) with
        {
            Arguments = ["-c", script, "sandbox", target, alias, allowed], WritablePaths = [working], DeniedWritePaths = [target]
        });
        var output = new StreamReader(sandbox.StandardOutput).ReadToEndAsync(TestContext.Current.CancellationToken);
        var error = new StreamReader(sandbox.StandardError).ReadToEndAsync(TestContext.Current.CancellationToken);
        var exit = await sandbox.WaitForExitAsync(TestContext.Current.CancellationToken).WaitAsync(Deadline, TestContext.Current.CancellationToken);
        Assert.True(exit == 0, $"Sandbox PID {sandbox.Id} exited with {exit}: {await error}");
        Assert.Equal("protected\n", await output);
        Assert.Equal("unchanged", File.ReadAllText(target));
        Assert.Equal("allowed", File.ReadAllText(allowed));
    }

    [Fact]
    [Trait("Category", "SandboxNative")]
    public async Task MacImageReplacement_RetainsTheOwnedPid_WhileNativeForkRemainsDenied()
    {
        if (!OperatingSystem.IsMacOS())
            Assert.Skip("This consumer requires native macOS exec and fork enforcement.");
        var working = Directory.CreateDirectory(Path.Combine(_root, "working")).FullName;
        await using var sandbox = await StartNativeAsync(Options(working) with
        {
            Executable = "/usr/bin/perl",
            RuntimePaths = ["/bin/bash"],
            Arguments = ["-e", """defined(fork()) and die "fork escaped"; exec @ARGV or die "exec failed: $!";""",
                "--", "/bin/bash", "-c", "printf '%s\\nfork-denied\\n' \"$$\""]
        });
        var output = new StreamReader(sandbox.StandardOutput).ReadToEndAsync(TestContext.Current.CancellationToken);
        var error = new StreamReader(sandbox.StandardError).ReadToEndAsync(TestContext.Current.CancellationToken);
        var exit = await sandbox.WaitForExitAsync(TestContext.Current.CancellationToken).WaitAsync(Deadline, TestContext.Current.CancellationToken);
        Assert.True(exit == 0, $"Sandbox PID {sandbox.Id} exited with {exit}: {await error}");
        Assert.Equal($"{sandbox.Id}\nfork-denied\n", await output);
    }

    [Fact]
    [Trait("Category", "SandboxNative")]
    public async Task MacRuntimeTree_WithWritableWorkspace_PreservesReadonlyData()
    {
        if (!OperatingSystem.IsMacOS())
            Assert.Skip("This consumer requires native macOS readonly runtime-tree grants.");
        var working = Directory.CreateDirectory(Path.Combine(_root, "working")).FullName;
        var writable = Path.Combine(working, "candidate.txt");
        var readOnly = Path.Combine(_root, "readonly.txt");
        await File.WriteAllTextAsync(writable, "before", TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(readOnly, "protected", TestContext.Current.CancellationToken);
        await using var sandbox = await StartNativeAsync(Options(working) with
        {
            Executable = "/usr/bin/perl",
            RuntimePaths = [AppContext.BaseDirectory, System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory()],
            ReadOnlyPaths = [readOnly], WritablePaths = [working],
            Arguments = ["-e", """open(my $denied, '>>', $ARGV[0]) and die "readonly escaped"; open(my $allowed, '>', $ARGV[1]) or die "workspace denied"; print $allowed "written"; close($allowed) or die "close failed";""",
                "--", readOnly, writable]
        });
        var output = new StreamReader(sandbox.StandardOutput).ReadToEndAsync(TestContext.Current.CancellationToken);
        var error = new StreamReader(sandbox.StandardError).ReadToEndAsync(TestContext.Current.CancellationToken);
        var exit = await sandbox.WaitForExitAsync(TestContext.Current.CancellationToken).WaitAsync(Deadline, TestContext.Current.CancellationToken);
        Assert.True(exit == 0, $"Sandbox PID {sandbox.Id} exited with {exit}: {await error}");
        _ = await output;
        Assert.Equal("written", await File.ReadAllTextAsync(writable, TestContext.Current.CancellationToken));
        Assert.Equal("protected", await File.ReadAllTextAsync(readOnly, TestContext.Current.CancellationToken));
    }


    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("Category", "SandboxNative")]
    public async Task MacNativeSocket_UsesTheRequestedSeatbeltNetworkPolicy(bool allowNetwork)
    {
        if (!OperatingSystem.IsMacOS() || !File.Exists("/usr/bin/nc"))
            Assert.Skip("This test requires macOS with its native nc executable.");
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        await using var sandbox = await StartNativeAsync(Options("/") with
        {
            Executable = "/usr/bin/nc", Arguments = ["-G", "2", "-w", "2", "127.0.0.1", port.ToString(System.Globalization.CultureInfo.InvariantCulture)],
            AllowNetwork = allowNetwork
        });
        var output = new StreamReader(sandbox.StandardOutput).ReadToEndAsync(TestContext.Current.CancellationToken);
        var error = new StreamReader(sandbox.StandardError).ReadToEndAsync(TestContext.Current.CancellationToken);
        await sandbox.StandardInput.DisposeAsync();
        if (allowNetwork)
        {
            using var connection = await listener.AcceptTcpClientAsync(TestContext.Current.CancellationToken).AsTask().WaitAsync(Deadline, TestContext.Current.CancellationToken);
            await connection.GetStream().WriteAsync("native-network\n"u8.ToArray(), TestContext.Current.CancellationToken);
        }
        var exit = await sandbox.WaitForExitAsync(TestContext.Current.CancellationToken).WaitAsync(Deadline, TestContext.Current.CancellationToken);
        if (allowNetwork)
        {
            Assert.Equal(0, exit);
            Assert.Equal("native-network\n", await output);
        }
        else
        {
            const int firstSignaledExit = 128;
            Assert.True(exit > 0 && exit < firstSignaledExit, $"Native network payload PID {sandbox.Id} exited with {exit}: {await error}");
            Assert.Equal(string.Empty, await output);
        }
    }

    [Fact]
    [Trait("Category", "SandboxNative")]
    public async Task MacChildProcesses_FailClosedInsteadOfClaimingProcessGroupOwnership()
    {
        if (!OperatingSystem.IsMacOS())
            Assert.Skip("The unsupported ownership combination is macOS-specific.");
        await Assert.ThrowsAsync<PlatformNotSupportedException>(() => SandboxedProcess.StartAsync(
            Options("/") with { AllowChildProcesses = true }, TestContext.Current.CancellationToken));
    }

    private static SandboxProcessOptions Options(string workingDirectory) => new()
    {
        Executable = OperatingSystem.IsWindows() ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe") : "/bin/sh",
        WorkingDirectory = workingDirectory,
        RuntimePaths = OperatingSystem.IsWindows() ? [] : UnixRuntimePaths(),
    };

    private static SandboxProcessOptions PythonOptions(string workingDirectory) => Options(workingDirectory) with { Executable = "/usr/bin/python3" };
    private static string[] UnixRuntimePaths() => new[] { "/lib", "/lib64", "/usr/lib" }.Where(Directory.Exists)
        .Concat(OperatingSystem.IsMacOS() ? ["/bin/bash"] : []).ToArray();
    private static void RequireLinuxPython()
    {
        if (!OperatingSystem.IsLinux() || !File.Exists("/usr/bin/python3"))
            Assert.Skip("This native boundary test requires Linux with the distribution's Python 3 executable.");
    }

}
