using Dsh.Sandbox;

namespace Dsh.Tests;

public sealed class SandboxPathInputTests : IDisposable
{
    private readonly string _root = TempTree.CreateDirectory("sandbox-path-inputs");
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(15);
    public void Dispose() => TempTree.Delete(_root);

    [Fact]
    [Trait("Category", "SandboxNative")]
    public async Task LinuxSymlinkedExecutable_ReadOnlyParentPreservesTheLinkAndTarget()
    {
        if (!OperatingSystem.IsLinux())
            Assert.Skip("Linux read-only mounts preserve links supplied by a granted directory.");
        RequireUnixPerl();
        var code = Directory.CreateDirectory(Path.Combine(_root, "code")).FullName;
        var alias = Path.Combine(code, "runner");
        File.CreateSymbolicLink(alias, "/usr/bin/perl");
        const string script = """
            my $alias = $ARGV[0];
            die "the granted executable link was not preserved\n" unless -l $alias && -r $alias;
            die "the read-only executable link was removable\n" if unlink($alias);
            print "protected-link\n";
            """;
        await AssertSuccessfulAsync(Options(code) with
        {
            Executable = alias,
            Arguments = ["-f", "-e", script, "--", alias],
            RuntimePaths = RuntimePaths().Append(code).ToArray()
        }, "protected-link\n");
        Assert.True(File.Exists(alias));
    }


    private static async Task AssertSuccessfulAsync(SandboxProcessOptions options, string expected)
    {
        await SandboxTestBackend.RequireAsync();
        await using var sandbox = await SandboxedProcess.StartAsync(options, TestContext.Current.CancellationToken);
        var output = new StreamReader(sandbox.StandardOutput).ReadToEndAsync(TestContext.Current.CancellationToken);
        var error = new StreamReader(sandbox.StandardError).ReadToEndAsync(TestContext.Current.CancellationToken);
        var exit = await sandbox.WaitForExitAsync(TestContext.Current.CancellationToken).WaitAsync(Deadline, TestContext.Current.CancellationToken);
        Assert.True(exit == 0, $"Sandbox exited with {exit}: {await error}");
        Assert.Equal(expected, await output);
    }

    private static SandboxProcessOptions Options(string working) => new()
    {
        Executable = "/usr/bin/perl", WorkingDirectory = working, RuntimePaths = RuntimePaths()
    };

    private static string[] RuntimePaths() => new[] { "/lib", "/lib64", "/usr/lib" }.Where(Directory.Exists).ToArray();

    private static void RequireUnixPerl()
    {
        if ((!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS()) || !File.Exists("/usr/bin/perl"))
            Assert.Skip("This native IO regression requires Linux or macOS with the system Perl executable.");
    }

}
