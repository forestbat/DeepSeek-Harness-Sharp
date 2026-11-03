using System.Diagnostics;
using System.Text.RegularExpressions;

namespace Dsh.Tests;

internal static partial class ManagedStackDiagnostics
{
    private static readonly TimeSpan CaptureTimeout = TimeSpan.FromSeconds(10);

    internal static async Task ReportAsync(Task? pending, string label, int? ownedProcessId = null)
    {
        try
        {
            await Console.Error.WriteLineAsync($"{label} task={pending?.Status.ToString() ?? "not-recorded"} target={(ownedProcessId is not null ? "owned-child" : "test-host")} pool-threads={ThreadPool.ThreadCount} pool-pending={ThreadPool.PendingWorkItemCount}").ConfigureAwait(false);
            var (exitCode, output) = await CollectAsync(ownedProcessId ?? Environment.ProcessId).ConfigureAwait(false);
            await Console.Error.WriteLineAsync($"OWNED_STACK_CAPTURE exit={exitCode}").ConfigureAwait(false);
            if (exitCode == 0)
                await WriteFramesAsync(output).ConfigureAwait(false);
        }
        catch (Exception error)
        {
            await Console.Error.WriteLineAsync($"OWNED_STACK_CAPTURE unavailable={error.GetType().Name}").ConfigureAwait(false);
        }
    }

    private static async Task<(int ExitCode, string Output)> CollectAsync(int processId)
    {
        var startInfo = new ProcessStartInfo("dotnet-stack")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        startInfo.ArgumentList.Add("report");
        startInfo.ArgumentList.Add("--process-id");
        startInfo.ArgumentList.Add(processId.ToString());
        using var deadline = new CancellationTokenSource(CaptureTimeout);
        using var collector = TestProcessScope.Start(startInfo);
        var output = collector.StandardOutput.ReadToEndAsync(deadline.Token);
        var error = collector.StandardError.ReadToEndAsync(deadline.Token);
        try
        {
            await Task.WhenAll(output, error, collector.WaitForExitAsync(deadline.Token)).ConfigureAwait(false);
        }
        catch
        {
            if (!collector.HasExited)
                collector.Kill();
            throw;
        }
        return (collector.ExitCode, await output.ConfigureAwait(false));
    }

    private static async Task WriteFramesAsync(string output)
    {
        using var reader = new StringReader(output);
        var thread = 0;
        while (reader.ReadLine() is { } line)
        {
            if (line.StartsWith("Thread (", StringComparison.Ordinal))
                await Console.Error.WriteLineAsync($"OWNED_STACK_THREAD {++thread}").ConfigureAwait(false);
            var frame = FrameName().Match(line);
            if (frame.Success)
                await Console.Error.WriteLineAsync($"OWNED_STACK_FRAME {frame.Groups[1].Value}").ConfigureAwait(false);
        }
    }

    [GeneratedRegex("""^\s*[^!\r\n]+!((?:System|Dsh|Interop|Microsoft)\.[A-Za-z0-9_.+<>`]+)(?:\(|$)""")]
    private static partial Regex FrameName();
}
