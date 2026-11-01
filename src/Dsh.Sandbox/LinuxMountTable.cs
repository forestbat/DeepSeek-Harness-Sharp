namespace Dsh.Sandbox;

/// <summary>Relates bind-mount aliases by kernel device and mount root, without walking unrelated workspace contents.</summary>
internal static class LinuxMountTable
{
    private sealed record Mount(string Device, string Root, string Target, string FileSystem);
    private readonly record struct Location(string Device, string Path);
    private static readonly HashSet<string> KernelFileSystems = new(StringComparer.Ordinal)
    {
        "proc", "sysfs", "cgroup", "cgroup2", "devtmpfs", "devpts", "mqueue", "securityfs", "debugfs", "tracefs",
        "configfs", "fusectl", "bpf", "pstore", "efivarfs", "selinuxfs", "binfmt_misc", "hugetlbfs"
    };

    public static void DemandOrdinaryPath(string path)
    {
        Mount? containing = null;
        foreach (var line in File.ReadLines("/proc/self/mountinfo"))
        {
            var mount = Parse(line);
            if (SandboxPolicy.Contains(mount.Target, path) && (containing is null || mount.Target.Length > containing.Target.Length))
                containing = mount;
        }
        if (containing is null || KernelFileSystems.Contains(containing.FileSystem))
            throw new NotSupportedException("Plugin file capabilities cannot access kernel, process, device or control filesystems.");
    }

    public static void Validate(SandboxPolicy policy)
    {
        policy = policy with
        {
            Readable = policy.Readable.Select(static grant => grant with { Source = UnixPathPin.PathOf(grant.Pin!) }).ToArray(),
            Writable = policy.Writable.Select(static grant => grant with { Source = UnixPathPin.PathOf(grant.Pin!) }).ToArray(),
            Denied = policy.Denied.Select(path => policy.DeniedPins.TryGetValue(path, out var pin) ? UnixPathPin.PathOf(pin) : path).ToArray(),
        };
        var grants = policy.Readable.Concat(policy.Writable);
        var mounts = File.ReadLines("/proc/self/mountinfo").Select(Parse).OrderByDescending(static mount => mount.Target.Length).ToArray();
        foreach (var grant in grants)
        {
            var containing = mounts.FirstOrDefault(mount => SandboxPolicy.Contains(mount.Target, grant.Source))
                ?? throw new NotSupportedException("A sandbox path cannot be related to a visible Linux mount.");
            if (KernelFileSystems.Contains(containing.FileSystem) || mounts.Any(mount => KernelFileSystems.Contains(mount.FileSystem) && SandboxPolicy.Contains(grant.Source, mount.Target)))
                throw new NotSupportedException("A Linux grant exposes a kernel/control filesystem, directly or through a nested mount; provide an ordinary filesystem subtree.");
        }
        if (policy.Writable.Length == 0)
            return;
        var protectedPaths = policy.Denied.Concat(policy.Readable.Select(static read => read.Source)).Distinct(StringComparer.Ordinal)
            .Select(path => (Path: path, Location: Resolve(mounts, path))).ToArray();
        foreach (var write in policy.Writable)
        {
            var location = Resolve(mounts, write.Source);
            var nestedMounts = mounts.Where(mount => mount.Target != write.Source && SandboxPolicy.Contains(write.Source, mount.Target)
                && !protectedPaths.Any(path => SandboxPolicy.Contains(path.Path, mount.Target))).ToArray();
            foreach (var (protectedPath, target) in protectedPaths)
            {
                if (location.Device == target.Device)
                {
                    var physicalOverlap = SandboxPolicy.Contains(location.Path, target.Path) || SandboxPolicy.Contains(target.Path, location.Path);
                    var lexicalOverlap = SandboxPolicy.Contains(write.Source, protectedPath) || SandboxPolicy.Contains(protectedPath, write.Source);
                    if (physicalOverlap && !lexicalOverlap)
                        throw new NotSupportedException("A writable Linux grant aliases a denied or read-only path through a pre-existing mount; grant an unaliased subtree.");
                }
                foreach (var mount in nestedMounts)
                {
                    if (mount.Device != target.Device)
                        continue;
                    if (SandboxPolicy.Contains(mount.Root, target.Path))
                    {
                        var alias = Path.Combine(mount.Target, Path.GetRelativePath(mount.Root, target.Path));
                        if (!string.Equals(alias, protectedPath, StringComparison.Ordinal))
                            throw new NotSupportedException("A nested Linux mount exposes an alternative writable pathname to a denied or read-only target.");
                    }
                    else if (SandboxPolicy.Contains(target.Path, mount.Root))
                    {
                        throw new NotSupportedException("A nested Linux mount exposes writable content from a denied or read-only tree.");
                    }
                }
            }
        }
    }

    private static Location Resolve(Mount[] mounts, string path)
    {
        var mount = mounts.FirstOrDefault(mount => SandboxPolicy.Contains(mount.Target, path))
            ?? throw new NotSupportedException("A sandbox path cannot be related to a visible Linux mount.");
        return new Location(mount.Device, Path.GetFullPath(Path.Combine(mount.Root, Path.GetRelativePath(mount.Target, path))));
    }

    private static Mount Parse(string line)
    {
        var fields = line.Split(' ');
        var separator = Array.IndexOf(fields, "-");
        if (fields.Length < 10 || separator < 6 || separator + 3 >= fields.Length)
            throw new NotSupportedException("Linux mount metadata has an unsupported format.");
        return new Mount(fields[2], Unescape(fields[3]), Unescape(fields[4]), fields[separator + 1]);
    }

    private static string Unescape(string path) => path.Replace("\\040", " ", StringComparison.Ordinal).Replace("\\011", "\t", StringComparison.Ordinal).Replace("\\012", "\n", StringComparison.Ordinal).Replace("\\134", "\\", StringComparison.Ordinal);
}
