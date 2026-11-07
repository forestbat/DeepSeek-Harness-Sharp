using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Dsh.Runtime;

/// <summary>Rejects grants exposing kernel and control filesystems.</summary>
public static class LinuxMountTable
{
    private const int FileSystemStatusBytes = 256;
    private static readonly StringComparison PathComparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
    private sealed record Mount(string Target, string FileSystem);
    private static readonly HashSet<string> KernelFileSystems = new(StringComparer.Ordinal)
    {
        "proc", "sysfs", "cgroup", "cgroup2", "devtmpfs", "devpts", "mqueue", "securityfs", "debugfs", "tracefs",
        "configfs", "fusectl", "bpf", "pstore", "efivarfs", "selinuxfs", "binfmt_misc", "hugetlbfs"
    };

    public static unsafe void DemandOrdinaryPath(string path)
    {
        var buffer = stackalloc byte[FileSystemStatusBytes];
        if (statfs(path, buffer) != 0)
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "Cannot inspect the requested filesystem.");
        DemandOrdinaryFileSystem(*(nuint*)buffer);
    }

    public static unsafe void DemandOrdinaryFile(SafeFileHandle descriptor)
    {
        var buffer = stackalloc byte[FileSystemStatusBytes];
        if (fstatfs(descriptor, buffer) != 0)
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "Cannot inspect the requested filesystem.");
        DemandOrdinaryFileSystem(*(nuint*)buffer);
    }

    private static void DemandOrdinaryFileSystem(nuint magic)
    {
        const uint proc = 0x9fa0, sysfs = 0x62656572, cgroup = 0x27e0eb, cgroup2 = 0x63677270;
        const uint devpts = 0x1cd1, mqueue = 0x19800202, securityfs = 0x73636673, debugfs = 0x64626720;
        const uint tracefs = 0x74726163, configfs = 0x62656570, fusectl = 0x65735543, bpf = 0xcafe4a11;
        const uint pstore = 0x6165676c, efivarfs = 0xde5e81e4, selinuxfs = 0xf97cff8c, binfmtMisc = 0x42494e4d, hugetlbfs = 0x958458f6;
        // devtmpfs shares its magic with tmpfs or ramfs; ordinary files on those filesystems are allowed.
        if (unchecked((uint)magic) is proc or sysfs or cgroup or cgroup2 or devpts or mqueue or securityfs or debugfs
            or tracefs or configfs or fusectl or bpf or pstore or efivarfs or selinuxfs or binfmtMisc or hugetlbfs)
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

    [DllImport("libc", SetLastError = true)]
    private static extern unsafe int statfs([MarshalAs(UnmanagedType.LPUTF8Str)] string path, byte* buffer);
    [DllImport("libc", SetLastError = true)]
    private static extern unsafe int fstatfs(SafeFileHandle descriptor, byte* buffer);
}
