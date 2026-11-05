using System.ComponentModel;
using System.Runtime.InteropServices;

namespace Dsh.Transport;

/// <summary>Ordinary filesystem object checks shared by host services.</summary>
public static class FilePathAccess
{
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
