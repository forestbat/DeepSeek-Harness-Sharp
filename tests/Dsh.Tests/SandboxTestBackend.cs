using System.Diagnostics;
using Dsh.Sandbox;

namespace Dsh.Tests;

internal static class SandboxTestBackend
{
    public static async Task RequireAsync()
    {
        if (OperatingSystem.IsMacOS())
        {
            if (!File.Exists("/usr/bin/sandbox-exec"))
                Assert.Skip("The macOS sandbox-exec backend is not installed.");
            return;
        }
        if (OperatingSystem.IsWindows())
            return;
        if (!OperatingSystem.IsLinux())
        {
            Assert.Skip("No sandbox backend exists for this platform.");
            return;
        }
        string? backend;
        try { backend = SandboxedProcess.BackendExecutablePath; }
        catch (PlatformNotSupportedException)
        {
            Assert.Skip("The Linux bubblewrap backend is not installed.");
            return;
        }
        var start = new ProcessStartInfo(backend!)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var argument in new[] { "--ro-bind", "/", "/", "--unshare-user", "--unshare-pid", "--unshare-net", "--die-with-parent", "--", "/bin/true" })
            start.ArgumentList.Add(argument);
        using var probe = TestProcessScope.Start(start) ?? throw new InvalidOperationException("The backend capability probe did not start.");
        var output = probe.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
        var error = probe.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
        await probe.WaitForExitAsync(TestContext.Current.CancellationToken);
        _ = await output;
        var diagnostic = await error;
        if (probe.ExitCode != 0 && (diagnostic.Contains("Operation not permitted", StringComparison.OrdinalIgnoreCase)
            || diagnostic.Contains("No permissions to create", StringComparison.OrdinalIgnoreCase)
            || diagnostic.Contains("Creating new namespace failed", StringComparison.OrdinalIgnoreCase)
            || diagnostic.Contains("No space left on device", StringComparison.OrdinalIgnoreCase)))
            Assert.Skip("The installed bubblewrap backend cannot create the required namespaces in this environment.");
        Assert.True(probe.ExitCode == 0, "The installed bubblewrap capability probe failed unexpectedly.");
    }
}
