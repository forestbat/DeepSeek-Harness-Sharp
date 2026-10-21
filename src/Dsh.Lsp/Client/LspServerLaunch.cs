using System.Diagnostics;
using Dsh.Lsp.Protocol;

namespace Dsh.Lsp.Client;

public sealed record LspServerLaunch(
    string Executable,
    IReadOnlyList<string> Arguments,
    string? InstallHint = null,
    IReadOnlyDictionary<string, string>? Environment = null)
{
    public bool IsAvailable() => LspExecutableResolver.Find(Executable) is not null;
}

public sealed class LspServerLaunchException(string message, Exception? inner = null) : Exception(message, inner);

public sealed record LspStartedServer(
    LspMessageConnection Connection,
    Task<string> StandardError);

public static class LspServerProcess
{
    public static LspStartedServer Start(LspServerLaunch launch, string workingDirectory, TimeSpan? requestTimeout = null)
    {
        var executable = LspExecutableResolver.Find(launch.Executable)
            ?? throw new LspServerLaunchException(
                $"language server '{launch.Executable}' was not found on PATH. {launch.InstallHint}".Trim());
        var startInfo = CreateStartInfo(executable, launch, workingDirectory);
        var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        if (!process.Start())
            throw new LspServerLaunchException($"failed to start language server '{launch.Executable}'");

        var standardError = process.StandardError.ReadToEndAsync();
        var connection = new LspMessageConnection(
            process.StandardOutput.BaseStream,
            process.StandardInput.BaseStream,
            ownsStreams: true,
            process,
            requestTimeout);
        return new LspStartedServer(connection, standardError);
    }

    private static ProcessStartInfo CreateStartInfo(string executable, LspServerLaunch launch, string workingDirectory)
    {
        var startInfo = new ProcessStartInfo
        {
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = new System.Text.UTF8Encoding(false),
            StandardErrorEncoding = new System.Text.UTF8Encoding(false),
            StandardInputEncoding = new System.Text.UTF8Encoding(false),
        };

        if (IsBatchFile(executable))
        {
            startInfo.FileName = Environment.GetEnvironmentVariable("COMSPEC") ?? "cmd.exe";
            startInfo.Arguments = BuildBatchArguments(executable, launch.Arguments);
        }
        else
        {
            startInfo.FileName = executable;
            foreach (var argument in launch.Arguments)
                startInfo.ArgumentList.Add(argument);
        }

        if (launch.Environment is { Count: > 0 } environment)
        {
            foreach (var (name, value) in environment)
                startInfo.Environment[name] = value;
        }

        return startInfo;
    }

    private static bool IsBatchFile(string path)
        => path.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith(".bat", StringComparison.OrdinalIgnoreCase);

    private static string BuildBatchArguments(string executable, IReadOnlyList<string> arguments)
    {
        var command = Quote(executable) + " " + string.Join(' ', arguments.Select(Quote));
        return $"/d /s /c \"{command}\"";
    }

    private static string Quote(string value)
        => value.Length > 0 && value.All(character => !char.IsWhiteSpace(character) && character != '"')
            ? value
            : $"\"{value.Replace("\"", "\\\"")}\"";
}

public static class LspExecutableResolver
{
    public static string? Find(string executable)
    {
        if (string.IsNullOrWhiteSpace(executable))
            return null;
        if (Path.IsPathRooted(executable) || executable.Contains(Path.DirectorySeparatorChar) || executable.Contains(Path.AltDirectorySeparatorChar))
            return File.Exists(executable) ? Path.GetFullPath(executable) : null;

        var extensions = OperatingSystem.IsWindows()
            ? (Environment.GetEnvironmentVariable("PATHEXT") ?? ".EXE;.CMD;.BAT").Split(';', StringSplitOptions.RemoveEmptyEntries)
            : [string.Empty];
        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
                     .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (var extension in extensions)
            {
                var candidate = Path.Combine(directory, executable + extension);
                if (File.Exists(candidate))
                    return candidate;
            }

            var exact = Path.Combine(directory, executable);
            if (File.Exists(exact))
                return exact;
        }

        return null;
    }
}
