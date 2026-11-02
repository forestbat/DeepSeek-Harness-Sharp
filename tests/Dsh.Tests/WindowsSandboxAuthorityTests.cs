using System.Runtime.Versioning;
using System.Security.AccessControl;
using Dsh.Sandbox;

namespace Dsh.Tests;

[Collection(SerialProcessCollection.Name)]
[SupportedOSPlatform("windows")]
public sealed class WindowsSandboxAuthorityTests : IDisposable
{
    private readonly string _root = TempTree.CreateDirectory("windows-sandbox-authority");
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(30);
    public void Dispose() => TempTree.Delete(_root);

    [Fact]
    [Trait("Category", "SandboxNative")]
    public async Task PathGrants_EnforceReadsAndWrites_AndAreRemovedAfterDisposal()
    {
        if (!OperatingSystem.IsWindows())
            Assert.Skip("This consumer requires Windows AppContainer and native filesystem ACLs.");
        var working = Directory.CreateDirectory(Path.Combine(_root, "working")).FullName;
        var input = Path.Combine(_root, "readable.txt");
        var secret = Path.Combine(_root, "not-granted.txt");
        var denied = Path.Combine(working, "settings.yaml");
        File.WriteAllText(input, "readable");
        File.WriteAllText(secret, "host-only");
        File.WriteAllText(denied, "unchanged");
        var originalDirectoryAcl = ReadDacl(working);
        var originalInputAcl = ReadDacl(input);
        var originalDeniedAcl = ReadDacl(denied);
        // Send shell syntax through stdin instead of encoding it as a CRT command-line argument.
        const string commands = """
            echo stage=read 1>&2
            type "..\readable.txt" || exit /b 90
            (type "..\not-granted.txt" >nul 2>&1) && exit /b 91
            (echo forbidden>>"..\readable.txt") 2>nul && exit /b 93
            echo stage=write 1>&2
            (echo forbidden>>"settings.yaml") 2>nul && exit /b 92
            echo allowed>allowed.txt
            echo diagnostic 1>&2
            exit /b 0
            """;
        await using (var consumer = await SandboxedProcess.StartAsync(new SandboxProcessOptions
        {
            Executable = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe"),
            WorkingDirectory = working,
            ReadOnlyPaths = [input],
            WritablePaths = [working],
            DeniedWritePaths = [denied],
            Arguments = ["/D", "/Q"],
        }, TestContext.Current.CancellationToken))
        {
            var output = new StreamReader(consumer.StandardOutput).ReadToEndAsync(TestContext.Current.CancellationToken);
            var error = new StreamReader(consumer.StandardError).ReadToEndAsync(TestContext.Current.CancellationToken);
            await consumer.StandardInput.WriteAsync(System.Text.Encoding.ASCII.GetBytes($"{commands.ReplaceLineEndings("\r\n")}\r\n"), TestContext.Current.CancellationToken);
            await consumer.StandardInput.FlushAsync(TestContext.Current.CancellationToken);
            await consumer.StandardInput.DisposeAsync();
            var code = await consumer.WaitForExitAsync(TestContext.Current.CancellationToken).WaitAsync(Deadline, TestContext.Current.CancellationToken);
            var stdout = await output;
            var stderr = await error;
            var diagnostic = System.Text.RegularExpressions.Regex.Replace(stderr, """(?:[A-Za-z]:[\\/]|/)[^\s"'<>]+""", "<path>");
            var failure = $"The real Windows path consumer exited with {code} (0x{unchecked((uint)code):x8}); stdoutLength={stdout.Length}; stderr={diagnostic[..Math.Min(diagnostic.Length, 2048)]}";
            Assert.True(code == 0, failure);
            Assert.True(stdout.Contains("readable", StringComparison.Ordinal), failure);
            Assert.True(stderr.Contains("diagnostic", StringComparison.Ordinal), failure);
        }
        Assert.Equal("allowed", File.ReadAllText(Path.Combine(working, "allowed.txt")).Trim());
        Assert.Equal("unchanged", File.ReadAllText(denied));
        Assert.Equal("readable", File.ReadAllText(input));
        Assert.Equal("host-only", File.ReadAllText(secret));
        Assert.Equal(originalDirectoryAcl, ReadDacl(working));
        Assert.Equal(originalInputAcl, ReadDacl(input));
        Assert.Equal(originalDeniedAcl, ReadDacl(denied));
        File.WriteAllText(denied, "host-write-after-disposal");
        Assert.Equal("host-write-after-disposal", File.ReadAllText(denied));
    }

    private static string ReadDacl(string path) => Directory.Exists(path)
        ? new DirectoryInfo(path).GetAccessControl(AccessControlSections.Access).GetSecurityDescriptorSddlForm(AccessControlSections.Access)
        : new FileInfo(path).GetAccessControl(AccessControlSections.Access).GetSecurityDescriptorSddlForm(AccessControlSections.Access);
}
