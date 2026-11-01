using System.Diagnostics;
using Dsh.ScriptHost;

namespace Dsh.Tests;

internal static class PolicyProcessIsolation
{
    internal const string Argument = "--isolated-policy-test";
    private const string Active = "Dsh.Tests.PolicyProcessIsolation.Active";

    internal static int Run(string[] arguments)
    {
        var position = Array.IndexOf(arguments, Argument);
        AppContext.SetData(Active, true);
        return global::Xunit.Runner.InProc.SystemConsole.ConsoleRunner.Run(
            ["-method", arguments[position + 1], "-parallelMode", "none"]).GetAwaiter().GetResult();
    }

    internal static async Task<bool> RunIfNeededAsync(string method, CancellationToken signal)
    {
        if (AppContext.GetData(Active) is true) return false;
        var command = PtcScriptHostClient.ResolveHostCommand()
            ?? throw new InvalidOperationException("The real test executable is unavailable.");
        var start = new ProcessStartInfo(command.FileName)
        {
            UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true,
        };
        foreach (var argument in command.Arguments)
            start.ArgumentList.Add(argument == PtcScriptHost.HostArgument ? Argument : argument);
        start.ArgumentList.Add(method);
        using var process = TestProcessScope.Start(start)
            ?? throw new InvalidOperationException("The isolated policy test did not start.");
        try
        {
            var output = process.StandardOutput.ReadToEndAsync(signal);
            var errors = process.StandardError.ReadToEndAsync(signal);
            await process.WaitForExitAsync(signal);
            var stdout = await output;
            var stderr = await errors;
            Assert.True(process.ExitCode == 0, $"Isolated policy test exited {process.ExitCode}: {stdout} {stderr}");
            return true;
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(CancellationToken.None);
            }
        }
    }
}
