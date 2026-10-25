using System.Diagnostics;

namespace Dsh.Tests;

/** Runs an exact non-model test selector with process-local proxy and trust inputs. */
internal static class OutboundProxyProcess
{
    private static readonly TimeSpan ExecutionTimeout = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan CleanupTimeout = TimeSpan.FromSeconds(5);
    private static readonly string[] ProxyVariables = ["HTTP_PROXY", "HTTPS_PROXY", "ALL_PROXY", "http_proxy", "https_proxy", "all_proxy"];

    internal readonly record struct Result(int ExitCode, string StandardOutput, string StandardError);

    internal static async Task<Result> RunAsync(string method, string? proxyUrl, CancellationToken signal,
        string? certificateFile = null)
    {
        using var child = TestProcessScope.Start(CreateStart(method, proxyUrl, certificateFile));
        var stdout = child.StandardOutput.ReadToEndAsync(CancellationToken.None);
        var stderr = child.StandardError.ReadToEndAsync(CancellationToken.None);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(signal);
        deadline.CancelAfter(ExecutionTimeout);
        string[] output;
        try
        {
            await child.WaitForExitAsync(deadline.Token);
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
            using var cleanup = new CancellationTokenSource(CleanupTimeout);
            await child.WaitForExitAsync(cleanup.Token);
            output = await Task.WhenAll(stdout, stderr).WaitAsync(cleanup.Token);
        }
        return new Result(child.ExitCode, output[0], output[1]);
    }

    private static ProcessStartInfo CreateStart(string method, string? proxyUrl, string? certificateFile)
    {
        var filename = $"{typeof(OutboundProxyProcess).Assembly.GetName().Name}{(OperatingSystem.IsWindows() ? ".exe" : "")}";
        var start = new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory, filename))
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = Directory.GetCurrentDirectory(),
        };
        foreach (var argument in new[] { "-method", method, "-explicit", "on", "-parallelMode", "none", "-trait-", "Category=LlmSmoke" })
            start.ArgumentList.Add(argument);
        foreach (var name in ProxyVariables)
        {
            start.Environment.Remove(name);
            if (proxyUrl is not null)
                start.Environment[name] = proxyUrl;
        }
        start.Environment.Remove("NO_PROXY");
        start.Environment.Remove("no_proxy");
        if (certificateFile is not null)
            start.Environment["SSL_CERT_FILE"] = certificateFile;
        return start;
    }
}
