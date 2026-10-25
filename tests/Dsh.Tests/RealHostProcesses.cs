using System.Diagnostics;
using System.Text;
using Dsh.Boot;

namespace Dsh.Tests;

/**
 * Real `dsharp host serve` processes of this worktree's build, each under its own harness home (DSH_HOME), so that tests never touch ~/.dsh or another worktree's daemons.
 * The host starts a PTY daemon on demand with the same DSH_HOME; cleanup therefore finds every process that serves that home (see KillAll).
 */
internal static class RealHostProcesses
{
    private const string HomeVariable = HarnessHome.HomeEnv;

    private const string ProcRoot = "/proc";

    /** Settings for a throwaway home: no file logging, so the directory can be deleted while handles are still closing. */
    private const string Settings = """
        logging:
          file: false
        """;

    /** This worktree's dsharp is a required E2E build artifact, not an optional runtime capability. */
    public static string Executable
    {
        get
        {
            var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../"));
            var name = OperatingSystem.IsWindows() ? "dsharp.exe" : "dsharp";
            var executable = Path.Combine(root, "DeepSeek-Harness-Sharp", "bin", "Debug", "net10.0", name);
            Assert.True(File.Exists(executable), "The real dsharp executable must be built with the tests.");
            return executable;
        }
    }

    /** A short home under the repository (socket paths are limited to 108 bytes) with throwaway settings. */
    public static string CreateHome(string prefix)
    {
        var root = TempTree.CreateSocketDirectory(prefix);
        File.WriteAllText(Path.Combine(root, "settings.yaml"), Settings);
        return root;
    }

    /** Preserve real providers and safety settings without following the template's storage redirects. */
    public static void CopyRealConfiguration(string root)
    {
        Directory.CreateDirectory(root);
        var template = HarnessHome.Default().Root;
        Assert.True(File.Exists(Path.Combine(template, "settings.yaml")),
            "The real TUI model smoke requires settings.yaml in the normal DSH_HOME template.");
        var profiles = Path.Combine(template, "profiles");
        if (Directory.Exists(profiles))
            CopyDirectory(profiles, Path.Combine(root, "profiles"));
        var home = new HarnessHome(root);
        var settings = HarnessSettings.Load(new HarnessHome(template));
        settings.Storage = new StorageSettings { Root = root, ToolResults = settings.Storage?.ToolResults };
        settings.Save(home);
        settings.SavePlugins(home);
    }

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.EnumerateFiles(source))
            File.Copy(file, Path.Combine(destination, Path.GetFileName(file)), overwrite: true);
        foreach (var directory in Directory.EnumerateDirectories(source))
            CopyDirectory(directory, Path.Combine(destination, Path.GetFileName(directory)));
    }

    public static Process StartHost(string executable, string root, Action<string>? output = null)
    {
        var info = new ProcessStartInfo(executable, ["host", "serve"])
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = root,
        };
        info.Environment[HomeVariable] = root;
        var process = TestProcessScope.Start(info);
        process.OutputDataReceived += (_, args) =>
        {
            if (args.Data is { } line)
                output?.Invoke($"host {process.Id} stdout: {line}");
        };
        process.ErrorDataReceived += (_, args) =>
        {
            if (args.Data is { } line)
                output?.Invoke($"host {process.Id} stderr: {line}");
        };
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        return process;
    }

    /**
     * Kills every process of this user that serves the harness home root: hosts, PTY daemons and the shells they run.
     * Where process environments can be read (Linux /proc, BSD ps -E) those are the processes whose environment has DSH_HOME=root.
     * Windows has no supported API that reads another process's environment, so there they are the dsharp processes listening on the loopback ports the home's endpoint files announce, killed with their process trees.
     */
    public static void KillAll(string root)
    {
        var marker = $"{HomeVariable}={root}";
        if (Directory.Exists(ProcRoot))
            KillByProcEnvironment(Encoding.UTF8.GetBytes($"{marker}\0"));
        else if (OperatingSystem.IsWindows())
            KillEndpointOwners(root);
        else
            KillByPsEnvironment(marker);
    }

    private static void KillByProcEnvironment(byte[] marker)
    {
        foreach (var directory in Directory.EnumerateDirectories(ProcRoot))
        {
            if (int.TryParse(Path.GetFileName(directory), out var pid) && pid != Environment.ProcessId && EnvironmentContains(directory, marker))
                TryKill(pid, entireProcessTree: false);
        }
    }

    /** `ps -E` appends each process's environment to its command line; the marker must end at a space or the end of the line. */
    private static void KillByPsEnvironment(string marker)
    {
        foreach (var line in ProcessListing("ps", "-axwwE", "-o", "pid=", "-o", "command="))
        {
            var fields = line.Trim().Split(' ', 2);
            if (fields is [var id, var command] && int.TryParse(id, out var pid) && pid != Environment.ProcessId
                && (command.Contains($"{marker} ", StringComparison.Ordinal) || command.EndsWith(marker, StringComparison.Ordinal)))
                TryKill(pid, entireProcessTree: false);
        }
    }

    /** `netstat -ano` lines are "TCP local-address foreign-address state pid"; only dsharp processes are killed, so a stale port file cannot hit another program. */
    private static void KillEndpointOwners(string root)
    {
        var runDirectory = Path.Combine(root, "run");
        if (!Directory.Exists(runDirectory))
            return;
        var ports = Directory.EnumerateFiles(runDirectory, "*.port")
            .Select(file => int.TryParse(File.ReadAllText(file).Trim(), out var port) ? $":{port}" : null)
            .OfType<string>()
            .ToHashSet(StringComparer.Ordinal);
        var name = Path.GetFileNameWithoutExtension(Executable);
        foreach (var line in ProcessListing("netstat", "-ano", "-p", "TCP"))
        {
            if (line.Split(' ', StringSplitOptions.RemoveEmptyEntries) is [_, var local, _, "LISTENING", var id]
                && ports.Any(port => local.EndsWith(port, StringComparison.Ordinal)) && int.TryParse(id, out var pid) && IsNamed(pid, name))
                TryKill(pid, entireProcessTree: true);
        }
    }

    /** The output lines of a process listing command such as ps or netstat. */
    internal static List<string> ProcessListing(string fileName, params string[] arguments)
    {
        using var process = TestProcessScope.Start(new ProcessStartInfo(fileName, arguments) { UseShellExecute = false, RedirectStandardOutput = true });
        var lines = process.StandardOutput.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries).ToList();
        process.WaitForExit();
        return lines;
    }

    private static bool IsNamed(int pid, string? name)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return string.Equals(process.ProcessName, name, StringComparison.OrdinalIgnoreCase);
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static bool EnvironmentContains(string processDirectory, byte[] marker)
    {
        try
        {
            return File.ReadAllBytes(Path.Combine(processDirectory, "environ")).AsSpan().IndexOf(marker) >= 0;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static void TryKill(int pid, bool entireProcessTree)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            process.Kill(entireProcessTree);
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
        }
    }
}

/** Runs the fleet CLI (`dsharp machine|session|doctor`) of this build as a separate process under a given harness home. */
internal static class FleetCliProcess
{
    /** Remote commands may deploy or start a host first. */
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);

    public sealed record Result(int ExitCode, string Output, string Error);

    public static async Task<Result> RunAsync(string executable, string home, params string[] arguments)
    {
        var info = new ProcessStartInfo(executable, arguments)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            WorkingDirectory = home,
        };
        info.Environment[HarnessHome.HomeEnv] = home;
        using var process = TestProcessScope.Start(info);
        process.StandardInput.Close();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(Timeout);
        var output = process.StandardOutput.ReadToEndAsync(timeout.Token);
        var error = process.StandardError.ReadToEndAsync(timeout.Token);
        await process.WaitForExitAsync(timeout.Token);
        return new Result(process.ExitCode, await output, await error);
    }

    /** The cells of each table line the CLI prints (cells never contain spaces in these tests). */
    public static List<string[]> Rows(string output)
        => [.. output.Split('\n').Select(line => line.Split(' ', StringSplitOptions.RemoveEmptyEntries))];
}
