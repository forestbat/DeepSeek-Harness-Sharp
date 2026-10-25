using System.Runtime.Versioning;
using Dsh.Fleet;
using Dsh.RemoteHost;

namespace Dsh.Tests;

/** 远端 dsharp 自动部署: RID 映射、Release 资产 URL、版本复用规则, 以及停旧宿主的脚本在真实 shell 里只停该二进制的宿主。 */
public sealed class RemoteDsharpInstallerTests
{
    [Theory]
    [InlineData("Linux", "x86_64", "linux-x64")]
    [InlineData("linux", "amd64", "linux-x64")]
    [InlineData("Linux", "aarch64", "linux-arm64")]
    [InlineData("Darwin", "arm64", "osx-arm64")]
    [InlineData("Darwin", "x86_64", "osx-x64")]
    [InlineData("Windows", "x86_64", "win-x64")]
    [InlineData("Windows", "AMD64", "win-x64")]
    [InlineData("Windows", "arm64", "win-arm64")]
    public void Rid_Maps_Supported_Platforms(string os, string arch, string expected)
        => Assert.Equal(expected, RemoteDsharpInstaller.RidFor(os, arch));

    [Theory]
    [InlineData("plan9", "x86_64")]
    [InlineData("Linux", "mips")]
    [InlineData("Windows", "mips")]
    public void Rid_Returns_Null_For_Unsupported(string os, string arch)
        => Assert.Null(RemoteDsharpInstaller.RidFor(os, arch));

    [Fact]
    public void Asset_Url_Uses_Latest_By_Default()
    {
        var settings = new DeploySettings { ReleaseBaseUrl = "https://host/releases" };
        Assert.Equal("https://host/releases/latest/download/dsharp-linux-x64.tar.gz", RemoteDsharpInstaller.AssetUrl(settings, "linux-x64"));
    }

    [Fact]
    public void Asset_Url_Uses_Tag_For_Pinned_Version()
    {
        var settings = new DeploySettings { ReleaseBaseUrl = "https://host/releases/", ReleaseVersion = "1.2.3" };
        Assert.Equal("https://host/releases/download/v1.2.3/dsharp-linux-x64.tar.gz", RemoteDsharpInstaller.AssetUrl(settings, "linux-x64"));
        Assert.Equal("https://host/releases/download/v1.2.3/dsharp-osx-arm64.tar.gz", RemoteDsharpInstaller.AssetUrl(settings with { ReleaseVersion = "v1.2.3" }, "osx-arm64"));
    }

    [Fact]
    public async Task Protocol_Marker_Is_Used_For_Reuse_And_Release_Is_Only_Diagnostic()
    {
        var machine = MachineSpec.Ssh("fake", "host.invalid", 22, "dev", SshAuth.Agent);
        var installer = new RemoteDsharpInstaller(new FakeTransport(machine, HostProtocol.Version, "9.9.9"));
        var installation = await installer.InspectAsync(TestContext.Current.CancellationToken);
        var result = await installer.EnsureAsync(
            new DeploySettings { ReleaseBaseUrl = "https://example.invalid" }, null, TestContext.Current.CancellationToken);

        Assert.Equal(HostProtocol.Version, installation.Protocol);
        Assert.Equal("9.9.9", installation.Version);
        Assert.True(result.AlreadyPresent);
    }

    [Fact]
    public void ProtocolMismatch_DistinguishesRemoteOlderAndNewer()
    {
        Assert.False(new HostProtocolMismatchException(2, 1).RemoteIsNewer);
        Assert.True(new HostProtocolMismatchException(2, 3).RemoteIsNewer);
    }

    [Fact]
    public async Task MissingProtocolMarker_ForcesOneRedeploy()
    {
        var machine = MachineSpec.Ssh("fake", "host.invalid", 22, "dev", SshAuth.Agent);
        var transport = new FakeTransport(machine, null, "1.0");
        var result = await new RemoteDsharpInstaller(transport).EnsureAsync(
            new DeploySettings { Via = DownloadVia.Client }, null, TestContext.Current.CancellationToken,
            (_, _, _) => Task.FromResult("archive.tar.gz"));

        Assert.False(result.AlreadyPresent);
        Assert.Contains(transport.Commands, command => command.Contains(".dsharp-protocol", StringComparison.Ordinal));
    }


    /** 真实 shell: 只停“旧二进制路径 + host serve”的进程, 同机另一个 dsharp 的宿主与执行脚本的 shell 都不受影响。 */
    [Fact(Timeout = 5000)]
    public async Task Stop_Command_Stops_Only_The_Host_Of_That_Binary()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Skip("POSIX shell only");
            return;
        }

        var root = TempTree.CreateDirectory("stop");
        try
        {
            var old = FakeHostBinary(Path.Combine(root, "old"));
            var other = FakeHostBinary(Path.Combine(root, "other"));
            using var oldHost = TestProcessScope.Start(old, ["host", "serve"]);
            using var otherHost = TestProcessScope.Start(other, ["host", "serve"]);
            await Task.Delay(200, TestContext.Current.CancellationToken);

            using var shell = TestProcessScope.Start("/bin/sh", ["-c", RemoteDsharpInstaller.StopHostCommand(old) + "echo done"]);
            await shell.WaitForExitAsync(TestContext.Current.CancellationToken);

            Assert.Equal(0, shell.ExitCode);
            Assert.True(oldHost.HasExited, "the host of the replaced binary must be stopped");
            Assert.False(otherHost.HasExited, "a host started from another binary must keep running");
            otherHost.Kill();
        }
        finally
        {
            TempTree.Delete(root);
        }
    }


    /** A stand-in executable whose command line looks like `<path> host serve` while it runs. */
    [UnsupportedOSPlatform("windows")]
    private static string FakeHostBinary(string directory)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "dsharp");
        File.WriteAllText(path, "#!/bin/sh\nwhile :; do sleep 1; done\n");
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return path;
    }

    private sealed class FakeTransport(MachineSpec machine, int? protocol, string release) : IRemoteMachineTransport
    {
        public List<string> Commands { get; } = [];

        public MachineSpec Machine => machine;

        public Task<RemoteProcessResult> CaptureAsync(string command, CancellationToken cancellationToken)
            => Task.FromResult(command.Contains("home=", StringComparison.Ordinal)
                ? new RemoteProcessResult(0,
                    $"home=/home/dev\npath=/home/dev/.dsharp/linux-x64/dsharp\nversion={release}\nprotocol={protocol}\n", "")
                : new RemoteProcessResult(0, "Linux\nx86_64\n", ""));

        public Task<string> RunAsync(string command, CancellationToken cancellationToken)
        {
            Commands.Add(command);
            if (command.Contains("home=", StringComparison.Ordinal))
                return Task.FromResult($"home=/home/dev\npath=/home/dev/.dsharp/linux-x64/dsharp\nversion={release}\nprotocol={protocol}\n");
            return Task.FromResult(command.Contains("tar -xf", StringComparison.Ordinal) ? "/home/dev/.dsharp/linux-x64" : "");
        }

        public Task UploadAsync(string localPath, string remotePath, CancellationToken cancellationToken) => Task.CompletedTask;

        public string CommandLine(string remoteCommand) => remoteCommand;
    }
}
