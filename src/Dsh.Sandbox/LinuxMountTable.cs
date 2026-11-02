namespace Dsh.Sandbox;

/// <summary>Rejects grants exposing kernel and control filesystems.</summary>
internal static class LinuxMountTable
{
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
            if (SandboxPolicy.Contains(mount.Target, path) && (containing is null || mount.Target.Length > containing.Target.Length))
                containing = mount;
        }
        if (containing is null || KernelFileSystems.Contains(containing.FileSystem))
            throw new NotSupportedException("Plugin file capabilities cannot access kernel, process, device or control filesystems.");
    }

    public static void Validate(SandboxPolicy policy)
    {
        var grants = policy.Readable.Concat(policy.Writable);
        var mounts = File.ReadLines("/proc/self/mountinfo").Select(Parse).OrderByDescending(static mount => mount.Target.Length).ToArray();
        foreach (var grant in grants)
        {
            var containing = mounts.FirstOrDefault(mount => SandboxPolicy.Contains(mount.Target, grant.Source))
                ?? throw new NotSupportedException("A sandbox path cannot be related to a visible Linux mount.");
            if (KernelFileSystems.Contains(containing.FileSystem) || mounts.Any(mount => KernelFileSystems.Contains(mount.FileSystem) && SandboxPolicy.Contains(grant.Source, mount.Target)))
                throw new NotSupportedException("A Linux grant exposes a kernel/control filesystem, directly or through a nested mount; provide an ordinary filesystem subtree.");
        }
    }

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
