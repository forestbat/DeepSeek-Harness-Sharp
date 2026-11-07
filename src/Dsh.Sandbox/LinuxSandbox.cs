using Dsh.Runtime;

namespace Dsh.Sandbox;

internal sealed class LinuxSandbox : IChildProcessLifetime
{
    private static readonly Lazy<string> Backend = new(FindBackend);
    internal static string BackendExecutablePath => Backend.Value;
    private readonly UnixChildProcess _child;
    public int Id => _child.Id;
    public Stream StandardInput => _child.StandardInput;
    public Stream StandardOutput => _child.StandardOutput;
    public Stream StandardError => _child.StandardError;
    public Task<int> Exit => _child.Exit;

    private LinuxSandbox(UnixChildProcess child) => _child = child;

    public static LinuxSandbox Start(SandboxPolicy policy)
    {
        var bubblewrap = BackendExecutablePath;
        LinuxMountTable.Validate(policy.Readable.Concat(policy.Writable).Select(static path => path.Source));
        var arguments = new List<string>
        {
            "--die-with-parent", "--new-session", "--unshare-user", "--unshare-pid", "--unshare-ipc", "--unshare-uts", "--cap-drop", "ALL", "--clearenv"
        };
        foreach (var entry in policy.EnvironmentEntries)
        {
            var separator = entry.IndexOf('=');
            arguments.AddRange(["--setenv", entry[..separator], entry[(separator + 1)..]]);
        }
        if (!policy.AllowNetwork)
            arguments.Add("--unshare-net");
        arguments.AddRange(["--proc", "/proc", "--dev", "/dev", "--tmpfs", "/tmp", "--dir", "/nonexistent"]);
        if (policy.GuestOnlyWorkingDirectory)
            arguments.AddRange(["--tmpfs", policy.WorkingDirectory]);
        foreach (var grant in policy.Writable.OrderBy(static path => path.Target.Length))
            AddGrant(arguments, "--bind", grant, null);
        foreach (var grant in policy.Readable.OrderBy(static path => path.Target.Length))
            AddGrant(arguments, "--ro-bind", grant, policy.Readable);
        foreach (var denied in policy.Denied)
        {
            if (File.Exists(denied) || Directory.Exists(denied))
                arguments.AddRange(["--ro-bind", denied, denied]);
            else if (policy.Writable.Any(write => SandboxPolicy.Contains(write.Source, denied)))
                throw new NotSupportedException("A denied path within a writable grant must exist before launch.");
        }
        arguments.AddRange(["--chdir", policy.WorkingDirectory, "--", policy.Executable]);
        arguments.AddRange(policy.Arguments);
        return new LinuxSandbox(UnixChildProcess.Start(bubblewrap, arguments, "/", SandboxEnvironment.LauncherEntries));
    }

    private static string FindBackend()
    {
        if (!OperatingSystem.IsLinux())
            throw new PlatformNotSupportedException("The bubblewrap backend is only available on Linux.");
        var directories = (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator);
        foreach (var directory in directories.Concat(["/usr/bin", "/bin"]))
        {
            if (!Path.IsPathFullyQualified(directory))
                continue;
            var candidate = Path.Combine(directory, "bwrap");
            if (!File.Exists(candidate))
                continue;
            var path = SandboxPolicy.CanonicalExistingPath(candidate);
            const UnixFileMode executable = UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute;
            if ((File.GetUnixFileMode(path) & executable) != 0)
                return path;
        }
        throw new PlatformNotSupportedException("Linux sandboxing requires an installed executable bubblewrap in an absolute PATH directory or a system location; no executable is downloaded.");
    }

    private static void AddGrant(List<string> arguments, string operation, SandboxPath grant, SandboxPath[]? readOnlyParents)
    {
        foreach (var destination in new[] { grant.Target, grant.Source }.Distinct(StringComparer.Ordinal))
        {
            if (new[] { "/proc", "/dev", "/sys", "/nonexistent" }.Any(path => SandboxPolicy.Contains(path, destination)) || destination == "/tmp")
                throw new NotSupportedException("A Linux sandbox grant cannot replace a private kernel or scratch mount.");
            if (destination != grant.Source && readOnlyParents is not null
                && readOnlyParents.Any(parent => parent.IsDirectory && parent.Target.Length < destination.Length && SandboxPolicy.Contains(parent.Target, destination)))
                continue;
            arguments.AddRange([operation, grant.Source, destination]);
        }
    }

    public void Kill() => _child.Kill();
    public ValueTask DisposeAsync() => _child.DisposeAsync();
}
