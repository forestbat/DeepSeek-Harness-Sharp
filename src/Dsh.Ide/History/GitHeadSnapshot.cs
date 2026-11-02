using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Dsh.Ide.History;

/** Reads only the timeline's paths; no working-tree content or git filters are executed. */
internal sealed class GitHeadSnapshot(Dictionary<string, string> objects, bool available)
{
    private const int CommandTimeoutMilliseconds = 5000;
    private static readonly StringComparer PathComparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    public static GitHeadSnapshot Read(string workspace, IReadOnlyList<string> paths)
    {
        var objects = new Dictionary<string, string>(PathComparer);
        if (paths.Count == 0)
            return new(objects, false);
        var root = Run(workspace, ["rev-parse", "--show-toplevel"])?.TrimEnd('\r', '\n');
        if (string.IsNullOrWhiteSpace(root))
            return new(objects, false);
        var relative = paths.Where(path => HistoryRestore.IsWithinWorkspace(root, path))
            .Select(path => Path.GetRelativePath(root, path).Replace(Path.DirectorySeparatorChar, '/')).ToArray();
        if (relative.Length == 0)
            return new(objects, false);
        var tree = Run(root, ["--literal-pathspecs", "ls-tree", "-z", "HEAD", "--", .. relative]);
        if (tree is null)
            return new(objects, false);
        foreach (var record in tree.Split('\0', StringSplitOptions.RemoveEmptyEntries))
        {
            var tab = record.IndexOf('\t');
            if (tab < 0)
                continue;
            var metadata = record[..tab].Split(' ');
            if (metadata.Length == 3 && metadata[0] is "100644" or "100755" && metadata[1] == "blob")
                objects[Path.GetFullPath(record[(tab + 1)..], root)] = metadata[2];
        }
        return new(objects, true);
    }

    public bool? Matches(string path, ReadOnlySpan<byte> content)
    {
        if (!available)
            return null;
        if (!objects.TryGetValue(Path.GetFullPath(path), out var oid))
            return false;
        using var hash = IncrementalHash.CreateHash(oid.Length == 64 ? HashAlgorithmName.SHA256 : HashAlgorithmName.SHA1);
        hash.AppendData(Encoding.ASCII.GetBytes($"blob {content.Length.ToString(CultureInfo.InvariantCulture)}\0"));
        hash.AppendData(content);
        return oid.Equals(Convert.ToHexString(hash.GetHashAndReset()), StringComparison.OrdinalIgnoreCase);
    }

    private static string? Run(string directory, IReadOnlyList<string> arguments)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo("git")
            {
                WorkingDirectory = directory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            },
        };
        process.StartInfo.ArgumentList.Add("--no-optional-locks");
        foreach (var argument in arguments)
            process.StartInfo.ArgumentList.Add(argument);
        try
        {
            process.Start();
            process.StandardInput.Close();
            var output = process.StandardOutput.ReadToEndAsync();
            var error = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(CommandTimeoutMilliseconds))
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit();
                return null;
            }
            _ = error.GetAwaiter().GetResult();
            return process.ExitCode == 0 ? output.GetAwaiter().GetResult() : null;
        }
        catch (Win32Exception)
        {
            return null;
        }
    }
}
