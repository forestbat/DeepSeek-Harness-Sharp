using System.Runtime.Versioning;
using System.Text;

namespace Dsh.Sandbox;

/// <summary>Seatbelt enforces filesystem and network policy for an owned process.</summary>
[SupportedOSPlatform("macos")]
internal sealed class MacSandbox : ISandboxLifetime
{
    private readonly UnixChildProcess _child;
    public int Id => _child.Id;
    public Stream StandardInput => _child.StandardInput;
    public Stream StandardOutput => _child.StandardOutput;
    public Stream StandardError => _child.StandardError;
    public Task<int> Exit { get; }

    private MacSandbox(UnixChildProcess child)
    {
        _child = child;
        Exit = child.Exit;
    }

    public static MacSandbox Start(SandboxPolicy policy)
    {
        const string launcher = "/usr/bin/sandbox-exec";
        const string environmentHelper = "/usr/bin/env";
        if (!File.Exists(launcher) || !File.Exists(environmentHelper))
            throw new PlatformNotSupportedException("macOS sandboxing requires sandbox-exec and the system env executable; no unrestricted fallback or automatic installation is used.");
        var profile = new StringBuilder("(version 1)\n(deny default)\n(allow process-exec (literal \"/usr/bin/env\") (literal ").Append(Quote(policy.Executable)).Append("))\n");
        profile.Append("(allow sysctl-read)\n(allow mach-lookup (global-name \"com.apple.system.logger\"))\n");
        // dyld's shared-cache bootstrap opens the root directory before the target executable starts.
        profile.Append("(allow file-read-data (require-all (literal \"/\") (vnode-type DIRECTORY)))\n");
        profile.Append("(allow file-read-metadata)\n(allow file-read* file-map-executable (literal \"/usr/bin/env\") (literal \"/dev/null\") (literal \"/dev/urandom\") (literal \"/dev/random\") (subpath \"/System/Library\") (subpath \"/usr/lib\"))\n");
        // The .NET globalization shim loads macOS system ICU data.
        profile.Append("(allow file-read-data (subpath \"/usr/share/icu\"))\n");
        profile.Append("(allow file-write-data (literal \"/dev/null\"))\n");
        foreach (var read in policy.Readable)
        {
            profile.Append("(allow file-read* file-map-executable ").Append(Filter(read)).Append(")\n");
            // Image replacement stays in the owned PID and requires an explicit non-credential-changing file grant.
            if (!read.IsDirectory && read.Source != policy.Executable)
            {
                var mode = File.GetUnixFileMode(read.Source);
                if ((mode & (UnixFileMode.SetUser | UnixFileMode.SetGroup)) == 0
                    && (mode & (UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute)) != 0)
                    profile.Append("(allow process-exec (literal ").Append(Quote(read.Source)).Append("))\n");
            }
            if (policy.Writable.Any(write => SandboxPolicy.Contains(write.Source, read.Source)))
                profile.Append("(deny file-write* ").Append(Filter(read)).Append(")\n");
        }
        foreach (var write in policy.Writable)
            profile.Append("(allow file-read* file-write* ").Append(Filter(write)).Append(")\n");
        profile.Append("(deny process-fork)\n");
        foreach (var deny in policy.Denied)
            profile.Append("(deny file-write* (literal ").Append(Quote(deny)).Append("))\n");
        if (policy.AllowNetwork)
            profile.Append("(allow network*)\n");
        var arguments = new List<string> { "-p", profile.ToString(), environmentHelper, "-i" };
        arguments.AddRange(policy.EnvironmentEntries);
        arguments.Add(policy.Executable);
        arguments.AddRange(policy.Arguments);
        var child = UnixChildProcess.Start(launcher, arguments, policy.WorkingDirectory, SandboxEnvironment.LauncherEntries);
        return new MacSandbox(child);
    }

    private static string Filter(SandboxPath path) => $"({(path.IsDirectory ? "subpath" : "literal")} {Quote(path.Source)})";
    private static string Quote(string text) => $"\"{text.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal).Replace("\n", "\\n", StringComparison.Ordinal).Replace("\r", "\\r", StringComparison.Ordinal)}\"";


    public void Kill() => _child.Kill();
    public async ValueTask DisposeAsync()
    {
        Kill();
        try { await Exit.ConfigureAwait(false); }
        finally { await _child.DisposeAsync().ConfigureAwait(false); }
    }

}
