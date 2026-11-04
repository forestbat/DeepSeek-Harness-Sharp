using System.ComponentModel;
using System.Diagnostics;
using System.Text;

namespace Dsh.GitInspect;

/** Outcome of one git invocation; OutputTruncated means stdout hit the caller's cap and the process was stopped early. */
public sealed record GitCommandResult(int ExitCode, string Output, string Error, bool OutputTruncated)
{
    public bool Ok => ExitCode == 0 || OutputTruncated;
}

/** A git invocation failed; the message carries git's own stderr so callers can surface it verbatim. */
public sealed class GitCommandException(string command, int exitCode, string error)
    : Exception($"git {command} failed ({exitCode}): {error}")
{
    public string Command { get; } = command;

    public int ExitCode { get; } = exitCode;

    public string Error { get; } = error;
}

/** Runs the git executable with repository discovery from the working directory, never through a shell. */
internal static class GitCommand
{
    private const string Executable = "git";
    private const int ReadBufferChars = 16 * 1024;
    private const int StartFailureExitCode = -1;

    /** Applied to every call: stable paths in non -z output, no colour codes, no prompts, no optional index locks. */
    private static readonly string[] SafetyConfig =
    [
        "-c", "core.quotePath=false",
        "-c", "color.ui=false",
        "--no-optional-locks",
    ];

    public static async Task<GitCommandResult> RunAsync(
        string workingDirectory,
        IReadOnlyList<string> arguments,
        int maxOutputChars = int.MaxValue,
        CancellationToken cancellationToken = default)
    {
        using var process = new Process();
        process.StartInfo = CreateStartInfo(workingDirectory, arguments);
        try
        {
            process.Start();
        }
        catch (Win32Exception error)
        {
            return new GitCommandResult(StartFailureExitCode, "", $"cannot start git: {error.Message}", false);
        }
        // git never reads stdin for these commands; closing it turns `--stdin` reads into an empty input.
        process.StandardInput.Close();
        var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        var (output, truncated) = await ReadCappedAsync(process.StandardOutput, maxOutputChars, cancellationToken);
        if (truncated)
            process.Kill(entireProcessTree: true);
        await process.WaitForExitAsync(cancellationToken);
        return new GitCommandResult(process.ExitCode, output, (await errorTask).Trim(), truncated);
    }

    /** Runs git and throws GitCommandException on failure; the common path for commands whose failure is exceptional. */
    public static async Task<string> RunCheckedAsync(
        string workingDirectory,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken = default)
    {
        var result = await RunAsync(workingDirectory, arguments, cancellationToken: cancellationToken);
        return result.Ok ? result.Output : throw new GitCommandException(arguments[0], result.ExitCode, result.Error);
    }

    internal static async Task CopyOutputAsync(string workingDirectory, IReadOnlyList<string> arguments, Stream destination, CancellationToken cancellationToken)
    {
        using var process = new Process { StartInfo = CreateStartInfo(workingDirectory, arguments) };
        process.Start();
        process.StandardInput.Close();
        using var abort = cancellationToken.Register(() => Stop(process));
        var errors = process.StandardError.ReadToEndAsync(cancellationToken);
        try
        {
            await process.StandardOutput.BaseStream.CopyToAsync(destination, cancellationToken).ConfigureAwait(false);
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            if (process.ExitCode != 0)
                throw new GitCommandException(arguments[0], process.ExitCode, (await errors.ConfigureAwait(false)).Trim());
            await errors.ConfigureAwait(false);
        }
        finally
        {
            Stop(process);
            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
        }
    }

    private static void Stop(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
        }
    }

    private static ProcessStartInfo CreateStartInfo(string workingDirectory, IReadOnlyList<string> arguments)
    {
        var info = new ProcessStartInfo(Executable)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = workingDirectory,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var argument in SafetyConfig.Concat(arguments))
            info.ArgumentList.Add(argument);
        info.Environment["GIT_TERMINAL_PROMPT"] = "0";
        info.Environment["GIT_OPTIONAL_LOCKS"] = "0";
        return info;
    }

    private static async Task<(string Output, bool Truncated)> ReadCappedAsync(
        StreamReader reader,
        int maxChars,
        CancellationToken cancellationToken)
    {
        var builder = new StringBuilder();
        var buffer = new char[ReadBufferChars];
        int read;
        while ((read = await reader.ReadAsync(buffer, cancellationToken)) > 0)
        {
            var room = maxChars - builder.Length;
            if (read > room)
            {
                builder.Append(buffer, 0, room);
                return (builder.ToString(), true);
            }
            builder.Append(buffer, 0, read);
        }
        return (builder.ToString(), false);
    }
}
