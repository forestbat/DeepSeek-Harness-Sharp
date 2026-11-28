using System.Formats.Tar;
using System.IO.Compression;
using System.Runtime.InteropServices;
using Dsh.Boot;
using Dsh.Fleet;
using Dsh.RemoteHost;

namespace Dsh.Tests;

[Collection(SerialProcessCollection.Name)]
public sealed class WslFleetTests : IDisposable
{
    private static readonly TimeSpan SettleTimeout = TimeSpan.FromSeconds(10);
    private readonly string _root = TempTree.CreateDirectory("wsl-fleet");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => TempTree.Delete(_root);

    [Fact]
    public void MachineStore_RoundTripsWslMachines_AndOmitsSshFields()
    {
        var store = new MachineStore(new HarnessHome(_root));
        File.WriteAllText(store.FilePath, "# header comment\nplugins: {}\n");
        var defaultDistro = MachineSpec.Wsl("wsl-default");
        var namedDistro = MachineSpec.Wsl("ubuntu", "Ubuntu-24.04", "dev", "~/work", "~/.dsh-wsl");

        store.Save(new FleetConfig([defaultDistro, namedDistro], new DeploySettings()));

        var yaml = File.ReadAllText(store.FilePath);
        Assert.Contains("# header comment", yaml, StringComparison.Ordinal);
        Assert.Contains("kind: wsl", yaml, StringComparison.Ordinal);
        Assert.Contains("wslDistro: Ubuntu-24.04", yaml, StringComparison.Ordinal);
        Assert.DoesNotContain("auth:", yaml, StringComparison.Ordinal);
        Assert.DoesNotContain("keyPath:", yaml, StringComparison.Ordinal);
        Assert.DoesNotContain("host:", yaml, StringComparison.Ordinal);

        var reloaded = store.Load();
        Assert.Equal([defaultDistro, namedDistro], reloaded.Machines);
        Assert.Equal("wsl:default", reloaded.Machines[0].Target);
        Assert.Equal("dev@wsl:Ubuntu-24.04", reloaded.Machines[1].Target);
    }

    [Theory]
    [InlineData("Ubuntu\n24", null, null)]
    [InlineData(null, "bad user", null)]
    [InlineData("Ubuntu-24.04", "dev", @"C:\repos\app")]
    [InlineData("Ubuntu-24.04", "dev", @"\\wsl$\Ubuntu-24.04\home\dev")]
    public void Validation_RejectsInvalidWslDistroUserRemotePath_AndOutboundTunnel(string? distro, string? user, string? remotePath)
    {
        var store = new MachineStore(new HarnessHome(_root));
        Assert.Throws<InvalidOperationException>(() => store.Upsert(MachineSpec.Wsl("bad", distro, user, remotePath)));
        Assert.Throws<InvalidOperationException>(() => store.Upsert(MachineSpec.Wsl("tunnel", "Ubuntu-24.04") with
        {
            OutboundTunnel = new OutboundTunnelSpec("proxyCommand", "http://127.0.0.1:3128"),
        }));
    }

    [Fact]
    public void CommandLine_IncludesDistroUserCdAndWindowsQuotedCommand()
    {
        var defaultMachine = MachineSpec.Wsl("default-wsl");
        var namedMachine = MachineSpec.Wsl("ubuntu", "Ubuntu-24.04", "alice");

        Assert.Equal("wsl.exe --cd ~ --exec /bin/sh -c \"echo ok\"", new WslTransport(defaultMachine).CommandLine("echo ok"));
        Assert.Equal("wsl.exe -d Ubuntu-24.04 -u alice --cd ~ --exec /bin/sh -c \"echo 'hi'\"", new WslTransport(namedMachine).CommandLine("echo 'hi'"));
    }

    [Fact]
    public async Task WslTransport_ListsDistros_CapturesCommands_AndUploadsFiles()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Skip("POSIX shell shim only");
            return;
        }

        var wslHome = Directory.CreateDirectory(Path.Combine(_root, "wsl-home")).FullName;
        var logFile = Path.Combine(_root, "invocations.log");
        var shim = CreateWslShim(_root, wslHome, logFile);

        var distros = await WslTransport.ListDistrosAsync(Ct, shim);
        Assert.Equal(["Ubuntu-24.04", "Debian"], distros);

        var machine = MachineSpec.Wsl("ubuntu", "Ubuntu-24.04", "dev");
        var transport = new WslTransport(machine, shim);

        var probe = await transport.CaptureAsync("printf '%s|%s' \"$WSL_UTF8\" \"$HOME\"", Ct);
        Assert.Equal((0, false, $"1|{wslHome}"), (probe.ExitCode, probe.TransportFailed, probe.StdOut));

        var failed = await transport.CaptureAsync("echo nope >&2; exit 7", Ct);
        Assert.Equal((7, false), (failed.ExitCode, failed.TransportFailed));
        await Assert.ThrowsAsync<IOException>(() => transport.RunAsync("echo boom >&2; exit 3", Ct));

        var localPayload = Path.Combine(_root, "payload.bin");
        byte[] expectedBytes = [0x00, 0x01, 0x42, 0x7F, 0xFF, (byte)'\n'];
        await File.WriteAllBytesAsync(localPayload, expectedBytes, Ct);
        await transport.UploadAsync(localPayload, "nested/data.bin", Ct);
        Assert.Equal(expectedBytes, await File.ReadAllBytesAsync(Path.Combine(wslHome, "nested", "data.bin"), Ct));

        var log = await File.ReadAllTextAsync(logFile, Ct);
        Assert.Contains("-d Ubuntu-24.04 -u dev --cd ~ --exec /bin/sh -c", log, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WslTransport_Doctor_AndHostLink_SurfaceStdoutOnlyErrors_AndMissingExecutable()
    {
        var missingOptions = new FleetOptions { WslExecutablePath = Path.Combine(_root, "missing-wsl.exe") };
        var missingChecks = await FleetDoctor.CheckAsync(MachineSpec.Wsl("missing-exe", "Ubuntu-24.04"), missingOptions, Ct);
        var missingCheck = Assert.Single(missingChecks);
        Assert.Equal(("wsl", DoctorOutcome.Failed), (missingCheck.Name, missingCheck.Outcome));
        Assert.Contains("WSL is not installed", missingCheck.Detail, StringComparison.Ordinal);

        if (OperatingSystem.IsWindows())
            return;

        var errorShim = Path.Combine(_root, "wsl-stdout-error.sh");
        File.WriteAllText(errorShim, "#!/bin/sh\nprintf 'Wsl/Service/WSL_E_DISTRO_NOT_FOUND\\n'\nexit 1\n");
        File.SetUnixFileMode(errorShim, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        var machine = MachineSpec.Wsl("missing-distro", "NonExistent");
        var transport = new WslTransport(machine, errorShim);
        var captured = await transport.CaptureAsync("true", Ct);
        Assert.Equal(1, captured.ExitCode);
        Assert.Contains("WSL_E_DISTRO_NOT_FOUND", captured.FailureDetail, StringComparison.Ordinal);

        var runError = await Assert.ThrowsAsync<IOException>(() => transport.RunAsync("true", Ct));
        Assert.Contains("WSL_E_DISTRO_NOT_FOUND", runError.Message, StringComparison.Ordinal);

        var doctorChecks = await FleetDoctor.CheckAsync(machine, new FleetOptions { WslExecutablePath = errorShim }, Ct);
        var doctorCheck = Assert.Single(doctorChecks);
        Assert.Equal(("wsl", DoctorOutcome.Failed), (doctorCheck.Name, doctorCheck.Outcome));
        Assert.Contains("WSL_E_DISTRO_NOT_FOUND", doctorCheck.Detail, StringComparison.Ordinal);

        var linkError = await Assert.ThrowsAsync<IOException>(() => HostLink.OpenRemoteAsync(
            machine,
            commandPath: "dsharp",
            new FleetOptions { WslExecutablePath = errorShim, ClientName = "test" },
            Ct));
        Assert.Contains("WSL_E_DISTRO_NOT_FOUND", linkError.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FleetCli_RejectsWslWithIncompatibleSshFlags()
    {
        var result = await FleetCliProcess.RunAsync(
            RealHostProcesses.Executable,
            _root,
            "machine",
            "add",
            "wsl-bad",
            "--wsl",
            "--distro",
            "Ubuntu-24.04",
            "--auth",
            "key");
        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("--wsl cannot be combined with --auth", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Installer_Doctor_AndProxyProbe_WorkOverWslTransport()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Skip("POSIX shell shim only");
            return;
        }

        var wslHome = Directory.CreateDirectory(Path.Combine(_root, "wsl-home")).FullName;
        var harness = TempTree.CreateSocketDirectory("wslh");
        try
        {
            var shim = CreateWslShim(_root, wslHome);
            var machine = MachineSpec.Wsl("wsl-box", "Ubuntu-24.04", harnessHome: harness);
            var options = new FleetOptions { WslExecutablePath = shim };
            var transport = WslTransport.Create(machine, options);
            var rid = RuntimeInformation.RuntimeIdentifier;

            var deployed = await new RemoteDsharpInstaller(transport).EnsureAsync(
                new DeploySettings { Via = DownloadVia.Client },
                null,
                Ct,
                (_, targetRid, _) => CreateFakeDsharpArchiveAsync(_root, targetRid));
            Assert.False(deployed.AlreadyPresent);
            Assert.Equal(Path.Combine(wslHome, ".dsharp", rid, "dsharp"), deployed.CommandPath);

            var reused = await new RemoteDsharpInstaller(transport).EnsureAsync(
                new DeploySettings { Via = DownloadVia.Client },
                null,
                Ct);
            Assert.True(reused.AlreadyPresent);

            var checks = await FleetDoctor.CheckAsync(machine, options, Ct, transport);
            var byName = checks.ToDictionary(check => check.Name, check => check);
            Assert.Equal(DoctorOutcome.Ok, byName["wsl"].Outcome);
            Assert.Equal(DoctorOutcome.Ok, byName["platform"].Outcome);
            Assert.Equal(DoctorOutcome.Ok, byName["dsharp"].Outcome);

            var probe = await new FleetProxyProbe(options).ProbeSshAsync(machine, Guid.NewGuid(), Ct);
            Assert.Equal(ProxyProbeOutcome.Unsupported, probe.Outcome);
        }
        finally
        {
            TempTree.Delete(harness);
        }
    }

    [Fact]
    public async Task MachineConnection_DeploysAndConnectsEndToEndOverWslTransport()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Skip("POSIX shell shim only");
            return;
        }

        var wslHome = Directory.CreateDirectory(Path.Combine(_root, "wsl-e2e-home")).FullName;
        var harness = TempTree.CreateCompactSocketDirectory();
        TempTree.AssertFitsMacOsSocketLimit(RemoteHostEndpoint.SocketPath(harness));
        try
        {
            var shim = CreateWslShim(_root, wslHome);
            var machine = MachineSpec.Wsl("wsl-e2e", "Ubuntu-24.04", harnessHome: harness);
            var options = FleetConnectionTests.TestOptions with
            {
                Deploy = new DeploySettings { Via = DownloadVia.Client },
                WslExecutablePath = shim,
                ArchiveFactory = (_, rid, token) => FleetSshEndToEndTests.BuildArchiveAsync(RealHostProcesses.Executable, rid, _root, token),
            };
            await using var connection = new MachineConnection(machine, options);
            var states = FleetConnectionTests.RecordStates(connection);
            connection.Start();
            using (var settle = CancellationTokenSource.CreateLinkedTokenSource(Ct))
            {
                settle.CancelAfter(SettleTimeout);
                await connection.WaitSettledAsync(settle.Token);
            }

            Assert.True(connection.State == MachineConnectionState.Connected, connection.Detail);
            Assert.Contains(MachineConnectionState.Deploying, states);
            Assert.Equal(harness, connection.Info!.HomeRoot);

            var doctor = await FleetDoctor.CheckAsync(machine, options, Ct);
            foreach (var name in new[] { "wsl", "platform", "dsharp", "host", "permissions", "connection" })
                Assert.Equal(DoctorOutcome.Ok, Assert.Single(doctor, check => check.Name == name).Outcome);
        }
        finally
        {
            RealHostProcesses.KillAll(harness);
            TempTree.Delete(harness);
        }
    }

    private static string CreateWslShim(string root, string wslHome, string? logFile = null)
    {
        var shim = Path.Combine(root, "wsl-shim.sh");
        var logLine = logFile is null ? "" : $"printf '%s\\n' \"$*\" >> {RemoteShell.Quote(logFile)}\n";
        // Simulate UTF-16LE BOM output as produced by Windows `wsl.exe --list --quiet`.
        var script = $$"""
            #!/bin/sh
            set -eu
            {{logLine}}if [ "${1:-}" = "--list" ] && [ "${2:-}" = "--quiet" ]; then
              printf '\377\376U\000b\000u\000n\000t\000u\000-\0002\0004\000.\0000\0004\000\r\000\n\000D\000e\000b\000i\000a\000n\000\r\000\n\000'
              exit 0
            fi
            while [ $# -gt 0 ]; do
              case "$1" in
                -d|-u|--cd) shift 2 ;;
                --exec) shift; break ;;
                *) shift ;;
              esac
            done
            export HOME={{RemoteShell.Quote(wslHome)}}
            cd "$HOME"
            exec "$@"
            """;
        File.WriteAllText(shim, script);
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(shim, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return shim;
    }

    private static async Task<string> CreateFakeDsharpArchiveAsync(string root, string rid)
    {
        var stage = Directory.CreateDirectory(Path.Combine(root, $"fake-{rid}")).FullName;
        var binary = Path.Combine(stage, "dsharp");
        await File.WriteAllTextAsync(binary, "#!/bin/sh\nexit 0\n");
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(binary, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        var archive = Path.Combine(root, RemoteDsharpInstaller.AssetName(rid));
        await using var file = File.Create(archive);
        await using var gzip = new GZipStream(file, CompressionMode.Compress);
        await TarFile.CreateFromDirectoryAsync(stage, gzip, includeBaseDirectory: false, Ct);
        return archive;
    }
}
