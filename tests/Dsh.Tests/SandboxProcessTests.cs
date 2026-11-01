using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using Dsh.Sandbox;

namespace Dsh.Tests;

public sealed class SandboxProcessTests : IDisposable
{
    private readonly string _root = TempTree.CreateDirectory("sandbox-native");
    private const long MemoryLimit = 128 * 1024 * 1024;
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(15);
    public void Dispose() => TempTree.Delete(_root);

    [Fact]
    public async Task DeniedFileWithExistingHardlink_IsRejectedBeforeLaunch()
    {
        var working = Directory.CreateDirectory(Path.Combine(_root, "working")).FullName;
        var target = Path.Combine(_root, "settings.yaml");
        var alias = Path.Combine(working, "alias.yaml");
        File.WriteAllText(target, "unchanged");
        var linked = OperatingSystem.IsWindows() ? CreateHardLink(alias, target, 0) : link(target, alias) == 0;
        Assert.True(linked, "Creating the native hardlink precondition failed.");
        var options = Options(working) with { WritablePaths = [working], DeniedWritePaths = [target] };
        await Assert.ThrowsAsync<NotSupportedException>(() => SandboxedProcess.StartAsync(options, TestContext.Current.CancellationToken));
        Assert.Equal("unchanged", File.ReadAllText(target));
    }

    [Fact]
    [Trait("Category", "SandboxNative")]
    public async Task NativeStdio_RoundTripsWithoutProtocolPollution()
    {
        if (OperatingSystem.IsWindows())
            Assert.Skip("The Windows native batch test covers redirected native IO.");
        await using var sandbox = await SandboxedProcess.StartAsync(Options("/") with
        {
            Arguments = ["-c", "IFS= read -r value; printf '%s\\n' \"$value\"; printf '%s\\n' diagnostic >&2"]
        }, TestContext.Current.CancellationToken);
        await sandbox.StandardInput.WriteAsync("native-pipe\n"u8.ToArray(), TestContext.Current.CancellationToken);
        await sandbox.StandardInput.FlushAsync(TestContext.Current.CancellationToken);
        var output = new StreamReader(sandbox.StandardOutput).ReadToEndAsync(TestContext.Current.CancellationToken);
        var error = new StreamReader(sandbox.StandardError).ReadToEndAsync(TestContext.Current.CancellationToken);
        var exitCode = await sandbox.WaitForExitAsync(TestContext.Current.CancellationToken).WaitAsync(Deadline, TestContext.Current.CancellationToken);
        Assert.True(exitCode == 0, $"Sandbox PID {sandbox.Id} exited with {exitCode}: {await error}");
        Assert.Equal("native-pipe\n", await output);
        Assert.Equal("diagnostic\n", await error);
        Assert.False(sandbox.WasMemoryLimitExceeded);
    }

    [Fact]
    [Trait("Category", "SandboxNative")]
    public async Task LinuxNativeIo_ProtectsDeniedFileAliasesReplacementDeletionAndAncestors()
    {
        RequireLinuxPython();
        var working = Directory.CreateDirectory(Path.Combine(_root, "working")).FullName;
        var protectedDirectory = Directory.CreateDirectory(Path.Combine(working, "protected")).FullName;
        var target = Path.Combine(protectedDirectory, "settings.yaml");
        var secret = Path.Combine(_root, "not-granted.txt");
        File.WriteAllText(target, "unchanged");
        File.WriteAllText(secret, "host-only");
        File.CreateSymbolicLink(Path.Combine(working, "existing-alias"), target);
        var unrelated = Path.Combine(working, "unrelated.txt");
        File.WriteAllText(unrelated, "before");
        Assert.Equal(0, link(unrelated, Path.Combine(working, "unrelated-alias.txt")));
        var runtime = Directory.CreateDirectory(Path.Combine(working, "runtime")).FullName;
        File.WriteAllText(Path.Combine(runtime, "library.txt"), "read-only");
        const string script = """
            import os, sys
            working, protected, target, secret, runtime = sys.argv[1:]
            def blocked(action):
                try:
                    action()
                except OSError:
                    return
                raise RuntimeError('native operation unexpectedly succeeded')
            blocked(lambda: open(secret, 'rb'))
            blocked(lambda: open(target, 'wb'))
            blocked(lambda: open(os.path.join(working, 'existing-alias'), 'wb'))
            blocked(lambda: open(os.path.join(runtime, 'library.txt'), 'wb'))
            blocked(lambda: os.rename(runtime, os.path.join(working, 'moved-runtime')))
            with open(os.path.join(working, 'unrelated-alias.txt'), 'w') as output:
                output.write('unrelated-write-allowed')
            allowed = os.path.join(working, 'allowed.txt')
            with open(allowed, 'w') as output:
                output.write('allowed')
            blocked(lambda: os.replace(allowed, target))
            blocked(lambda: os.unlink(target))
            blocked(lambda: os.rename(protected, os.path.join(working, 'moved')))
            blocked(lambda: os.link(target, os.path.join(working, 'hard-alias')))
            alias = os.path.join(working, 'symlink-alias')
            os.symlink(target, alias)
            blocked(lambda: open(alias, 'wb'))
            print('protected')
            """;
        await using var sandbox = await SandboxedProcess.StartAsync(PythonOptions(working) with
        {
            Arguments = ["-I", "-c", script, working, protectedDirectory, target, secret, runtime],
            WritablePaths = [working], DeniedWritePaths = [target], ReadOnlyPaths = [runtime]
        }, TestContext.Current.CancellationToken);
        var output = new StreamReader(sandbox.StandardOutput).ReadToEndAsync(TestContext.Current.CancellationToken);
        var error = new StreamReader(sandbox.StandardError).ReadToEndAsync(TestContext.Current.CancellationToken);
        Assert.Equal(0, await sandbox.WaitForExitAsync(TestContext.Current.CancellationToken).WaitAsync(Deadline, TestContext.Current.CancellationToken));
        Assert.Equal("protected\n", await output);
        Assert.Equal(string.Empty, await error);
        Assert.Equal("unchanged", File.ReadAllText(target));
        Assert.Equal("allowed", File.ReadAllText(Path.Combine(working, "allowed.txt")));
        Assert.Equal("unrelated-write-allowed", File.ReadAllText(unrelated));
        Assert.Equal("read-only", File.ReadAllText(Path.Combine(runtime, "library.txt")));
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
        await using var sandbox = await SandboxedProcess.StartAsync(PythonOptions("/") with
        {
            Arguments = ["-I", "-c", script, port.ToString(System.Globalization.CultureInfo.InvariantCulture)], AllowNetwork = allowNetwork
        }, TestContext.Current.CancellationToken);
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
            Assert.Skip("This test exercises the Linux cgroup and PID namespace boundary.");
        var sleep = new[] { "/usr/bin/sleep", "/bin/sleep" }.First(File.Exists);
        await using var sandbox = await SandboxedProcess.StartAsync(Options("/") with
        {
            Arguments = ["-c", "\"$1\" 60 & printf '%s\\n' ready; exit 0", "sandbox", sleep],
            RuntimePaths = UnixRuntimePaths().Append(sleep).ToArray(), AllowChildProcesses = true
        }, TestContext.Current.CancellationToken);
        var output = new StreamReader(sandbox.StandardOutput).ReadToEndAsync(TestContext.Current.CancellationToken);
        Assert.Equal(0, await sandbox.WaitForExitAsync(TestContext.Current.CancellationToken).WaitAsync(Deadline, TestContext.Current.CancellationToken));
        Assert.Equal("ready\n", await output.WaitAsync(Deadline, TestContext.Current.CancellationToken));
        Assert.False(sandbox.WasMemoryLimitExceeded);
    }

    [Fact]
    [Trait("Category", "SandboxNative")]
    public async Task LinuxWaitCancellation_KillsOwnedTreeWithoutInferringOom()
    {
        if (!OperatingSystem.IsLinux())
            Assert.Skip("This test exercises exact cgroup cancellation.");
        var sleep = new[] { "/usr/bin/sleep", "/bin/sleep" }.First(File.Exists);
        await using var sandbox = await SandboxedProcess.StartAsync(Options("/") with
        {
            Executable = sleep, Arguments = ["60"]
        }, TestContext.Current.CancellationToken);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => sandbox.WaitForExitAsync(cancelled.Token).WaitAsync(Deadline, TestContext.Current.CancellationToken));
        Assert.False(sandbox.WasMemoryLimitExceeded);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("Category", "SandboxNative")]
    public async Task LinuxNativeFork_EnforcesTheRequestedChildPolicy(bool allowChildren)
    {
        RequireLinuxPython();
        const string script = """
            import os
            try:
                child = os.fork()
            except PermissionError:
                print('blocked')
            else:
                if child == 0:
                    os._exit(0)
                os.waitpid(child, 0)
                print('forked')
            """;
        await using var sandbox = await SandboxedProcess.StartAsync(PythonOptions("/") with
        {
            Arguments = ["-I", "-c", script], AllowChildProcesses = allowChildren
        }, TestContext.Current.CancellationToken);
        var output = new StreamReader(sandbox.StandardOutput).ReadToEndAsync(TestContext.Current.CancellationToken);
        Assert.Equal(0, await sandbox.WaitForExitAsync(TestContext.Current.CancellationToken).WaitAsync(Deadline, TestContext.Current.CancellationToken));
        Assert.Equal(allowChildren ? "forked\n" : "blocked\n", await output);
    }

    [Fact]
    [Trait("Category", "SandboxNative")]
    public async Task LinuxNativeMemoryPressure_ReportsTheOwnedCgroupOomCounter()
    {
        RequireLinuxPython();
        const string script = """
            blocks = []
            while True:
                data = bytearray(8 * 1024 * 1024)
                for offset in range(0, len(data), 4096):
                    data[offset] = 1
                blocks.append(data)
            """;
        await using var sandbox = await SandboxedProcess.StartAsync(PythonOptions("/") with
        {
            Arguments = ["-I", "-c", script], MemoryLimitBytes = 32 * 1024 * 1024
        }, TestContext.Current.CancellationToken);
        Assert.NotEqual(0, await sandbox.WaitForExitAsync(TestContext.Current.CancellationToken).WaitAsync(Deadline, TestContext.Current.CancellationToken));
        Assert.True(sandbox.WasMemoryLimitExceeded);
    }

    [Fact]
    public async Task LoaderInjectionEnvironment_IsRejectedBeforeLaunchingTheTrustedWrapper()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => SandboxedProcess.StartAsync(Options(_root) with
        {
            Environment = new Dictionary<string, string?> { ["LD_PRELOAD"] = "/not-a-permitted-loader" }
        }, TestContext.Current.CancellationToken));
    }

    [Fact]
    [Trait("Category", "SandboxNative")]
    public async Task WindowsWritableDirectory_IsRefusedBeforeNativeIo_AndRetainsHostAuthority()
    {
        if (!OperatingSystem.IsWindows())
            Assert.Skip("This test exercises AppContainer ACLs, mandatory labels, explicit handles, and a Windows job.");
        var working = Directory.CreateDirectory(Path.Combine(_root, "working")).FullName;
        var protectedDirectory = Directory.CreateDirectory(Path.Combine(working, "protected")).FullName;
        var target = Path.Combine(protectedDirectory, "settings.yaml");
        var alias = Path.Combine(working, "hard-alias.yaml");
        File.WriteAllText(target, "unchanged");
        var script = Path.Combine(_root, "native-boundary.cmd");
        File.WriteAllText(script, $"""
            @echo off
            (echo forbidden>"{target}") 2>nul && exit /b 91
            (del /q "{target}") 2>nul
            if not exist "{target}" exit /b 92
            echo replacement>"{working}\replacement.yaml"
            (move /y "{working}\replacement.yaml" "{target}") >nul 2>nul && exit /b 93
            (move "{protectedDirectory}" "{working}\moved") >nul 2>nul && exit /b 94
            (mklink /H "{alias}" "{target}") >nul 2>nul
            if exist "{alias}" (echo forbidden>"{alias}") 2>nul && exit /b 95
            echo allowed>"{working}\allowed.txt"
            echo protected
            exit /b 0
            """);
        await Assert.ThrowsAsync<NotSupportedException>(() => SandboxedProcess.StartAsync(Options(working) with
        {
            Arguments = ["/D", "/Q", "/C", script], ReadOnlyPaths = [script], WritablePaths = [working], DeniedWritePaths = [target]
        }, TestContext.Current.CancellationToken));
        Assert.Equal("unchanged", File.ReadAllText(target));
        Assert.False(File.Exists(Path.Combine(working, "allowed.txt")));
        Assert.False(File.Exists(Path.Combine(working, "replacement.yaml")));
        Assert.False(File.Exists(alias));
        Assert.False(Directory.Exists(Path.Combine(working, "moved")));
        File.WriteAllText(target, "host-can-write-after-disposal");
        Assert.Equal("host-can-write-after-disposal", File.ReadAllText(target));
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
        await using var sandbox = await SandboxedProcess.StartAsync(Options(working) with
        {
            Arguments = ["-c", script, "sandbox", target, alias, allowed], WritablePaths = [working], DeniedWritePaths = [target]
        }, TestContext.Current.CancellationToken);
        var output = new StreamReader(sandbox.StandardOutput).ReadToEndAsync(TestContext.Current.CancellationToken);
        var error = new StreamReader(sandbox.StandardError).ReadToEndAsync(TestContext.Current.CancellationToken);
        var exit = await sandbox.WaitForExitAsync(TestContext.Current.CancellationToken).WaitAsync(Deadline, TestContext.Current.CancellationToken);
        Assert.True(exit == 0, $"Sandbox PID {sandbox.Id} exited with {exit}: {await error}");
        Assert.Equal("protected\n", await output);
        Assert.Equal("unchanged", File.ReadAllText(target));
        Assert.Equal("allowed", File.ReadAllText(allowed));
        Assert.False(sandbox.WasMemoryLimitExceeded);
    }

    [Fact]
    [Trait("Category", "SandboxNative")]
    public async Task MacImageReplacement_RetainsTheOwnedPid_WhileNativeForkRemainsDenied()
    {
        if (!OperatingSystem.IsMacOS())
            Assert.Skip("This consumer requires native macOS exec and fork enforcement.");
        var working = Directory.CreateDirectory(Path.Combine(_root, "working")).FullName;
        await using var sandbox = await SandboxedProcess.StartAsync(Options(working) with
        {
            Executable = "/usr/bin/perl",
            RuntimePaths = ["/bin/bash"],
            Arguments = ["-e", """defined(fork()) and die "fork escaped"; exec @ARGV or die "exec failed: $!";""",
                "--", "/bin/bash", "-c", "printf '%s\\nfork-denied\\n' \"$$\""]
        }, TestContext.Current.CancellationToken);
        var output = new StreamReader(sandbox.StandardOutput).ReadToEndAsync(TestContext.Current.CancellationToken);
        var error = new StreamReader(sandbox.StandardError).ReadToEndAsync(TestContext.Current.CancellationToken);
        var exit = await sandbox.WaitForExitAsync(TestContext.Current.CancellationToken).WaitAsync(Deadline, TestContext.Current.CancellationToken);
        Assert.True(exit == 0, $"Sandbox PID {sandbox.Id} exited with {exit}: {await error}");
        Assert.Equal($"{sandbox.Id}\nfork-denied\n", await output);
        Assert.False(sandbox.WasMemoryLimitExceeded);
    }

    [Fact]
    [Trait("Category", "SandboxNative")]
    public async Task MacPinnedRuntimeTree_WithWritableWorkspace_PreservesReadonlyData()
    {
        if (!OperatingSystem.IsMacOS())
            Assert.Skip("This consumer requires native macOS readonly runtime-tree pinning.");
        var working = Directory.CreateDirectory(Path.Combine(_root, "working")).FullName;
        var writable = Path.Combine(working, "candidate.txt");
        var readOnly = Path.Combine(_root, "readonly.txt");
        await File.WriteAllTextAsync(writable, "before", TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(readOnly, "protected", TestContext.Current.CancellationToken);
        await using var sandbox = await SandboxedProcess.StartAsync(Options(working) with
        {
            Executable = "/usr/bin/perl",
            RuntimePaths = [AppContext.BaseDirectory, System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory()],
            ReadOnlyPaths = [readOnly], WritablePaths = [working],
            Arguments = ["-e", """open(my $denied, '>>', $ARGV[0]) and die "readonly escaped"; open(my $allowed, '>', $ARGV[1]) or die "workspace denied"; print $allowed "written"; close($allowed) or die "close failed";""",
                "--", readOnly, writable]
        }, TestContext.Current.CancellationToken);
        var output = new StreamReader(sandbox.StandardOutput).ReadToEndAsync(TestContext.Current.CancellationToken);
        var error = new StreamReader(sandbox.StandardError).ReadToEndAsync(TestContext.Current.CancellationToken);
        var exit = await sandbox.WaitForExitAsync(TestContext.Current.CancellationToken).WaitAsync(Deadline, TestContext.Current.CancellationToken);
        Assert.True(exit == 0, $"Sandbox PID {sandbox.Id} exited with {exit}: {await error}");
        _ = await output;
        Assert.Equal("written", await File.ReadAllTextAsync(writable, TestContext.Current.CancellationToken));
        Assert.Equal("protected", await File.ReadAllTextAsync(readOnly, TestContext.Current.CancellationToken));
    }

    [Fact]
    [Trait("Category", "SandboxNative")]
    public async Task MacNativeIo_DeniesReplacementAncestorRenameHardlinkAndReadOnlySubtreeWrites()
    {
        if (!OperatingSystem.IsMacOS() || !File.Exists("/usr/bin/perl"))
            Assert.Skip("This test requires the macOS system Perl executable to issue native IO without spawning children.");
        var working = Directory.CreateDirectory(Path.Combine(_root, "working")).FullName;
        var locked = Directory.CreateDirectory(Path.Combine(working, "locked")).FullName;
        var readOnly = Directory.CreateDirectory(Path.Combine(working, "runtime")).FullName;
        var target = Path.Combine(locked, "settings.yaml");
        File.WriteAllText(target, "unchanged");
        File.WriteAllText(Path.Combine(readOnly, "runtime.txt"), "runtime");
        const string script = """
            use strict;
            use warnings;
            my ($root, $target, $runtime) = @ARGV;
            sub blocked { my ($name, $result) = @_; die "$name unexpectedly succeeded\n" if $result; }
            blocked("direct write", open(my $direct, ">>", $target));
            blocked("unlink", unlink($target));
            blocked("ancestor rename", rename("$root/locked", "$root/moved"));
            open(my $replacement, ">", "$root/replacement") or die "replacement setup failed\n";
            print {$replacement} "replacement";
            close($replacement) or die "replacement close failed\n";
            blocked("replace", rename("$root/replacement", $target));
            blocked("hardlink", link($target, "$root/alias"));
            blocked("read-only write", open(my $read_only, ">>", "$runtime/runtime.txt"));
            blocked("read-only rename", rename($runtime, "$root/moved-runtime"));
            open(my $allowed, ">", "$root/allowed.txt") or die "allowed write failed\n";
            print {$allowed} "allowed";
            close($allowed) or die "allowed close failed\n";
            print "protected\n";
            """;
        await using var sandbox = await SandboxedProcess.StartAsync(Options(working) with
        {
            Executable = "/usr/bin/perl", Arguments = ["-f", "-e", script, "--", working, target, readOnly],
            ReadOnlyPaths = [readOnly], WritablePaths = [working], DeniedWritePaths = [target]
        }, TestContext.Current.CancellationToken);
        var output = new StreamReader(sandbox.StandardOutput).ReadToEndAsync(TestContext.Current.CancellationToken);
        var error = new StreamReader(sandbox.StandardError).ReadToEndAsync(TestContext.Current.CancellationToken);
        var exit = await sandbox.WaitForExitAsync(TestContext.Current.CancellationToken).WaitAsync(Deadline, TestContext.Current.CancellationToken);
        Assert.True(exit == 0, $"Sandbox PID {sandbox.Id} exited with {exit}: {await error}");
        Assert.Equal("protected\n", await output);
        Assert.Equal("unchanged", File.ReadAllText(target));
        Assert.Equal("runtime", File.ReadAllText(Path.Combine(readOnly, "runtime.txt")));
        Assert.Equal("allowed", File.ReadAllText(Path.Combine(working, "allowed.txt")));
        Assert.False(sandbox.WasMemoryLimitExceeded);
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
        await using var sandbox = await SandboxedProcess.StartAsync(Options("/") with
        {
            Executable = "/usr/bin/nc", Arguments = ["-G", "2", "-w", "2", "127.0.0.1", port.ToString(System.Globalization.CultureInfo.InvariantCulture)],
            AllowNetwork = allowNetwork
        }, TestContext.Current.CancellationToken);
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
        MemoryLimitBytes = MemoryLimit
    };

    private static SandboxProcessOptions PythonOptions(string workingDirectory) => Options(workingDirectory) with { Executable = "/usr/bin/python3" };
    private static string[] UnixRuntimePaths() => new[] { "/lib", "/lib64", "/usr/lib" }.Where(Directory.Exists)
        .Concat(OperatingSystem.IsMacOS() ? ["/bin/bash"] : []).ToArray();
    private static void RequireLinuxPython()
    {
        if (!OperatingSystem.IsLinux() || !File.Exists("/usr/bin/python3"))
            Assert.Skip("This native boundary test requires Linux with the distribution's Python 3 executable.");
    }

    [DllImport("libc", SetLastError = true)]
    private static extern int link([MarshalAs(UnmanagedType.LPUTF8Str)] string target, [MarshalAs(UnmanagedType.LPUTF8Str)] string alias);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLink(string alias, string target, nint attributes);
}
