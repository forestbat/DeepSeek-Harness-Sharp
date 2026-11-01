using System.Globalization;
using Microsoft.Win32.SafeHandles;

namespace Dsh.Sandbox;

internal sealed class LinuxSandbox : ISandboxLifetime
{
    private static readonly Lazy<string> Backend = new(FindBackend);
    internal static string BackendExecutablePath => Backend.Value;
    private static readonly Lazy<string> Launcher = new(static () => SandboxPolicy.CanonicalExistingPath("/bin/sh"));
    internal static string LauncherExecutablePath => Launcher.Value;

    private readonly UnixChildProcess _child;
    private readonly LinuxCgroup _cgroup;
    private int _memoryExceeded;
    public int Id => _child.Id;
    public Stream StandardInput => _child.StandardInput;
    public Stream StandardOutput => _child.StandardOutput;
    public Stream StandardError => _child.StandardError;
    public bool WasMemoryLimitExceeded => Volatile.Read(ref _memoryExceeded) != 0;
    public Task<int> Exit { get; }

    private LinuxSandbox(UnixChildProcess child, LinuxCgroup cgroup)
    {
        _child = child;
        _cgroup = cgroup;
        Exit = CompleteAsync();
    }

    public static LinuxSandbox Start(SandboxPolicy policy)
    {
        var bubblewrap = BackendExecutablePath;
        LinuxMountTable.Validate(policy);
        var mountPins = new List<SafeFileHandle>();
        var firstMountDescriptor = policy.AllowChildProcesses && policy.AllowNetwork ? 3 : 4;
        var arguments = new List<string>
        {
            "--die-with-parent", "--new-session", "--unshare-user", "--disable-userns", "--unshare-pid", "--unshare-ipc", "--unshare-uts", "--unshare-cgroup", "--cap-drop", "ALL"
        };
        arguments.Add("--clearenv");
        foreach (var entry in policy.EnvironmentEntries)
        {
            var separator = entry.IndexOf('=');
            arguments.AddRange(["--setenv", entry[..separator], entry[(separator + 1)..]]);
        }
        if (!policy.AllowNetwork)
            arguments.Add("--unshare-net");
        arguments.AddRange(["--proc", "/proc", "--dev", "/dev", "--size", Math.Min(policy.MemoryLimitBytes, 64 * 1024 * 1024).ToString(CultureInfo.InvariantCulture), "--tmpfs", "/tmp", "--dir", "/nonexistent"]);
        if (policy.GuestOnlyWorkingDirectory)
            arguments.AddRange(["--size", Math.Min(policy.MemoryLimitBytes, 64 * 1024 * 1024).ToString(CultureInfo.InvariantCulture), "--tmpfs", policy.WorkingDirectory]);
        foreach (var grant in policy.Writable.OrderBy(static path => path.Target.Length))
            AddGrant(arguments, mountPins, firstMountDescriptor, policy.ProtectedAliases.Any(alias => alias.Source == grant.Source) ? "--ro-bind-fd" : "--bind-fd", grant, null);
        ProtectDeniedPaths(arguments, mountPins, firstMountDescriptor, policy);
        foreach (var grant in policy.Readable.OrderBy(static path => path.Target.Length))
            AddGrant(arguments, mountPins, firstMountDescriptor, "--ro-bind-fd", grant, policy.Readable);
        if (!policy.AllowChildProcesses || !policy.AllowNetwork)
            arguments.AddRange(["--seccomp", "3"]);
        arguments.AddRange(["--chdir", policy.WorkingDirectory, "--", policy.Executable]);
        arguments.AddRange(policy.Arguments);
        var cgroup = LinuxCgroup.Create(policy.MemoryLimitBytes);
        try
        {
            // Only this fixed trusted shell runs outside the cgroup; it must place itself before executing bubblewrap or any payload.
            // A failed placement terminates rather than executing an unrestricted command.
            const string script = "printf '%s' \"$$\" > \"$1/cgroup.procs\" || { printf '%s\\n' 'Sandbox cgroup placement failed.' >&2; exit 125; }; shift; exec \"$@\"";
            var launch = new List<string> { "-c", script, "dsh-sandbox", cgroup.Path, bubblewrap };
            launch.AddRange(arguments);
            var filter = policy.AllowChildProcesses && policy.AllowNetwork ? null : LinuxSeccomp.Create(policy.AllowChildProcesses, policy.AllowNetwork);
            var child = UnixChildProcess.Start(LauncherExecutablePath, launch, "/", SandboxEnvironment.LauncherEntries, filter, mountPins);
            return new LinuxSandbox(child, cgroup);
        }
        catch
        {
            cgroup.DisposeAsync().AsTask().GetAwaiter().GetResult();
            throw;
        }
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

    private static void AddGrant(List<string> arguments, List<SafeFileHandle> pins, int firstDescriptor, string operation, SandboxPath grant, SandboxPath[]? readOnlyParents)
    {
        foreach (var destination in new[] { grant.Target, grant.Source }.Distinct(StringComparer.Ordinal))
        {
            if (new[] { "/proc", "/dev", "/sys", "/nonexistent" }.Any(path => SandboxPolicy.Contains(path, destination)) || destination == "/tmp")
                throw new NotSupportedException("A Linux sandbox grant cannot replace a private kernel or scratch mount.");
            if (destination != grant.Source && readOnlyParents is not null)
            {
                var preservesLink = false;
                foreach (var parent in readOnlyParents)
                {
                    if (!parent.IsDirectory || parent.Target.Length >= destination.Length || !SandboxPolicy.Contains(parent.Target, destination))
                        continue;
                    preservesLink = true;
                    break;
                }
                // The read-only parent preserves its link entry; the canonical target still gets its pinned mount.
                if (preservesLink)
                    continue;
            }
            AddPinnedMount(arguments, pins, firstDescriptor, operation, grant.Pin!, destination);
        }
    }

    private static void AddPinnedMount(List<string> arguments, List<SafeFileHandle> pins, int firstDescriptor, string operation, SafeFileHandle pin, string destination)
    {
        arguments.AddRange([operation, checked(firstDescriptor + pins.Count).ToString(CultureInfo.InvariantCulture), destination]);
        pins.Add(pin);
    }

    private static void ProtectDeniedPaths(List<string> arguments, List<SafeFileHandle> pins, int firstDescriptor, SandboxPolicy policy)
    {
        var protectedPaths = new List<(SafeFileHandle Pin, string Target)>();
        var ancestors = new Dictionary<string, SafeFileHandle>(StringComparer.Ordinal);
        var protectedSources = policy.DeniedPins.Select(static entry => (Source: entry.Key, Pin: entry.Value))
            .Concat(policy.Readable.Select(static read => (read.Source, Pin: read.Pin!)))
            .Concat(policy.ProtectedAliases.Select(static alias => (alias.Source, Pin: alias.Pin!))).ToArray();
        foreach (var write in policy.Writable.Where(static path => path.IsDirectory))
        {
            var currentRoot = UnixPathPin.PathOf(write.Pin!);
            foreach (var (source, pin) in protectedSources.Distinct())
            {
                var locations = new HashSet<string>(StringComparer.Ordinal);
                if (SandboxPolicy.Contains(write.Source, source))
                    locations.Add(source);
                var currentSource = UnixPathPin.PathOf(pin);
                if (SandboxPolicy.Contains(currentRoot, currentSource))
                    locations.Add(Path.Combine(write.Source, Path.GetRelativePath(currentRoot, currentSource)));
                foreach (var location in locations)
                    foreach (var root in new[] { write.Source, write.Target }.Distinct(StringComparer.Ordinal))
                    {
                        var target = Path.Combine(root, Path.GetRelativePath(write.Source, location));
                        protectedPaths.Add((pin, target));
                        if (location == write.Source)
                            continue;
                        for (var parent = Path.GetDirectoryName(location); parent != write.Source; parent = Path.GetDirectoryName(parent)!)
                        {
                            var destination = Path.Combine(root, Path.GetRelativePath(write.Source, parent!));
                            if (!ancestors.ContainsKey(destination))
                                ancestors.Add(destination, policy.PinPath(parent!, directory: true));
                        }
                    }
            }
        }
        // Pin writable ancestors as mountpoints so payload renames cannot move a protected file out of its read-only mount.
        foreach (var (target, pin) in ancestors.OrderBy(static entry => entry.Key.Length))
            AddPinnedMount(arguments, pins, firstDescriptor, "--bind-fd", pin, target);
        foreach (var (pin, target) in protectedPaths.OrderBy(static path => path.Target.Length))
            AddPinnedMount(arguments, pins, firstDescriptor, "--ro-bind-fd", pin, target);
    }

    public void Kill()
    {
        _cgroup.Kill();
        _child.Kill();
    }

    private async Task<int> CompleteAsync()
    {
        try { return await _child.Exit.ConfigureAwait(false); }
        finally
        {
            _cgroup.Kill();
            await _cgroup.WaitForEmptyAsync().ConfigureAwait(false);
            if (_cgroup.LocalOomEvents != 0)
                Interlocked.Exchange(ref _memoryExceeded, 1);
        }
    }

    public async ValueTask DisposeAsync()
    {
        Kill();
        try { await Exit.ConfigureAwait(false); }
        finally
        {
            await _child.DisposeAsync().ConfigureAwait(false);
            await _cgroup.DisposeAsync().ConfigureAwait(false);
        }
    }
}

internal sealed class LinuxCgroup : IAsyncDisposable
{
    public string Path { get; }
    private readonly Lock _gate = new();
    private bool _disposed;
    private Exception? _killFailure;
    private LinuxCgroup(string path) => Path = path;

    public static LinuxCgroup Create(long memoryLimit)
    {
        var membership = File.ReadLines("/proc/self/cgroup").FirstOrDefault(static line => line.StartsWith("0::", StringComparison.Ordinal));
        if (membership is null)
            throw new PlatformNotSupportedException("Linux sandboxing requires cgroup v2 with delegated memory and pids controllers.");
        foreach (var line in File.ReadLines("/proc/self/mountinfo"))
        {
            var fields = line.Split(' ');
            var separator = Array.IndexOf(fields, "-");
            if (separator < 0 || fields[separator + 1] != "cgroup2")
                continue;
            var mountRoot = Unescape(fields[3]);
            var mount = Unescape(fields[4]);
            var relative = membership[3..];
            if (mountRoot != "/" && !SandboxPolicy.Contains(mountRoot, relative))
                continue;
            var current = System.IO.Path.GetFullPath(System.IO.Path.Combine(mount, relative[(mountRoot == "/" ? 1 : mountRoot.Length)..].TrimStart('/')));
            for (string? ancestor = current; ancestor is not null && SandboxPolicy.Contains(mount, ancestor); ancestor = System.IO.Path.GetDirectoryName(ancestor))
            {
                var directory = System.IO.Path.Combine(ancestor, $"dsh-sandbox-{Guid.NewGuid():N}");
                var created = false;
                try
                {
                    Directory.CreateDirectory(directory);
                    created = true;
                    if (!File.Exists(System.IO.Path.Combine(directory, "memory.max")) || !File.Exists(System.IO.Path.Combine(directory, "pids.max")) || !File.Exists(System.IO.Path.Combine(directory, "cgroup.kill")))
                        continue;
                    File.WriteAllText(System.IO.Path.Combine(directory, "memory.max"), memoryLimit.ToString(CultureInfo.InvariantCulture));
                    File.WriteAllText(System.IO.Path.Combine(directory, "memory.swap.max"), "0");
                    File.WriteAllText(System.IO.Path.Combine(directory, "memory.oom.group"), "1");
                    File.WriteAllText(System.IO.Path.Combine(directory, "pids.max"), "256");
                    var result = new LinuxCgroup(directory);
                    // The cleanup path is mandatory, not a best-effort capability discovered after running untrusted code.
                    result.Kill();
                    if (result._killFailure is not null)
                        throw new IOException("The delegated cgroup cannot be terminated.", result._killFailure);
                    created = false;
                    return result;
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
                finally
                {
                    if (created)
                        Directory.Delete(directory);
                }
                if (ancestor == mount)
                    break;
            }
        }
        throw new PlatformNotSupportedException("Linux sandboxing requires a writable cgroup v2 subtree with memory and pids enabled, memory.swap.max and cgroup.kill. Delegate those controllers to the user service/scope and run the host in a leaf below that delegation. No unrestricted fallback is used.");
    }

    private static string Unescape(string path) => path.Replace("\\040", " ", StringComparison.Ordinal).Replace("\\011", "\t", StringComparison.Ordinal).Replace("\\012", "\n", StringComparison.Ordinal).Replace("\\134", "\\", StringComparison.Ordinal);

    public void Kill()
    {
        lock (_gate)
        {
            if (_disposed)
                return;
            try { File.WriteAllText(System.IO.Path.Combine(Path, "cgroup.kill"), "1"); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { _killFailure = exception; }
        }
    }

    public long LocalOomEvents => File.ReadLines(System.IO.Path.Combine(Path, "memory.events.local"))
        .Where(static line => line.StartsWith("oom ", StringComparison.Ordinal)).Select(static line => long.Parse(line.AsSpan(4), CultureInfo.InvariantCulture)).Single();

    public async Task WaitForEmptyAsync()
    {
        while (!File.ReadLines(System.IO.Path.Combine(Path, "cgroup.events")).Contains("populated 0", StringComparer.Ordinal))
        {
            if (_killFailure is not null)
                throw new IOException("Terminating the exact owned cgroup failed.", _killFailure);
            await Task.Delay(25).ConfigureAwait(false);
        }
        if (_killFailure is not null)
            throw new IOException("Terminating the exact owned cgroup failed.", _killFailure);
    }

    public async ValueTask DisposeAsync()
    {
        Kill();
        await WaitForEmptyAsync().ConfigureAwait(false);
        lock (_gate)
        {
            if (_disposed)
                return;
            Directory.Delete(Path);
            _disposed = true;
        }
    }
}
