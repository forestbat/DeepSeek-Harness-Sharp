using System.ComponentModel;
using System.Runtime.InteropServices;

namespace Dsh.Runtime;

/// <summary>Ordinary filesystem object checks shared by host services.</summary>
public static class FilePathAccess
{
    public static void DemandOrdinaryFile(string path, bool allowMissing)
    {
        CanonicalFilePath.DemandFilePath(path);
        var exists = File.Exists(path);
        if (exists && !IsOrdinaryFile(path) || Directory.Exists(path))
            throw new NotSupportedException("Plugin file capabilities require an ordinary file.");
        if (!exists && !allowMissing)
            throw new FileNotFoundException("The requested plugin file does not exist.");
        if (!OperatingSystem.IsLinux())
            return;
        var probe = exists ? path : Path.GetDirectoryName(path);
        while (probe is not null && !Directory.Exists(probe) && !File.Exists(probe))
            probe = Path.GetDirectoryName(probe);
        LinuxMountTable.DemandOrdinaryPath(probe ?? Path.GetPathRoot(path)!);
    }

    public static unsafe bool IsOrdinaryFile(string path)
    {
        if (OperatingSystem.IsWindows())
            return (File.GetAttributes(path) & (FileAttributes.Directory | FileAttributes.Device)) == 0;
        var buffer = stackalloc byte[512];
        new Span<byte>(buffer, 512).Clear();
        if (OperatingSystem.IsLinux())
        {
            if (statx(-100, path, 0, 1, buffer) != 0)
                throw new Win32Exception(Marshal.GetLastPInvokeError(), "Cannot inspect the requested file type.");
            return (*(ushort*)(buffer + 28) & 0xf000) == 0x8000;
        }
        if (OperatingSystem.IsMacOS())
        {
            if ((RuntimeInformation.ProcessArchitecture == Architecture.X64 ? statInode64(path, buffer) : stat(path, buffer)) != 0)
                throw new Win32Exception(Marshal.GetLastPInvokeError(), "Cannot inspect the requested file type.");
            return (*(ushort*)(buffer + 4) & 0xf000) == 0x8000;
        }
        throw new PlatformNotSupportedException("Ordinary file capabilities require Windows, Linux or macOS.");
    }

    [DllImport("libc", SetLastError = true)]
    private static extern unsafe int statx(int directory, [MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags, uint mask, byte* buffer);
    [DllImport("libc", EntryPoint = "stat", SetLastError = true)]
    private static extern unsafe int stat([MarshalAs(UnmanagedType.LPUTF8Str)] string path, byte* buffer);
    [DllImport("libc", EntryPoint = "stat$INODE64", SetLastError = true)]
    private static extern unsafe int statInode64([MarshalAs(UnmanagedType.LPUTF8Str)] string path, byte* buffer);
}
