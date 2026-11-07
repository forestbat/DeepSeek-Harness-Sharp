namespace Dsh.Runtime;

/// <summary>Rejects grants exposing kernel and control filesystems.</summary>
public static class LinuxMountTable
{
    private static readonly StringComparison PathComparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
    private sealed record Mount(string Target, string FileSystem);
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
            if (Contains(mount.Target, path) && (containing is null || mount.Target.Length > containing.Target.Length))
                containing = mount;
        }
        if (containing is null || KernelFileSystems.Contains(containing.FileSystem))
            throw new NotSupportedException("Plugin file capabilities cannot access kernel, process, device or control filesystems.");
    }

    public static void Validate(IEnumerable<string> roots)
    {
        var mounts = File.ReadLines("/proc/self/mountinfo").Select(Parse).OrderByDescending(static mount => mount.Target.Length).ToArray();
        foreach (var root in roots)
        {
            var containing = mounts.FirstOrDefault(mount => Contains(mount.Target, root))
                ?? throw new NotSupportedException("A sandbox path cannot be related to a visible Linux mount.");
            if (KernelFileSystems.Contains(containing.FileSystem) || mounts.Any(mount => KernelFileSystems.Contains(mount.FileSystem) && Contains(root, mount.Target)))
                throw new NotSupportedException("A Linux grant exposes a kernel/control filesystem, directly or through a nested mount; provide an ordinary filesystem subtree.");
        }
    }

    private static bool Contains(string parent, string child) => string.Equals(parent, child, PathComparison)
        || child.StartsWith(Path.EndsInDirectorySeparator(parent) ? parent : parent + Path.DirectorySeparatorChar, PathComparison);

    private static Mount Parse(string line)
    {
        var fields = line.Split(' ');
        var separator = Array.IndexOf(fields, "-");
        if (fields.Length < 10 || separator < 6 || separator + 3 >= fields.Length)
            throw new NotSupportedException("Linux mount metadata has an unsupported format.");
        return new Mount(Unescape(fields[4]), fields[separator + 1]);
    }

    private static string Unescape(string path) => path.Replace("\\040", " ", StringComparison.Ordinal).Replace("\\011", "\t", StringComparison.Ordinal).Replace("\\012", "\n", StringComparison.Ordinal).Replace("\\134", "\\", StringComparison.Ordinal);
}
