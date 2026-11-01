using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Dsh.Sandbox;

/** Query the directory that resolves a name, never the operating system or a different mount. */
internal static class NativeDirectoryCase
{
    internal static bool IsSensitive(SafeFileHandle directory)
    {
        if (OperatingSystem.IsWindows())
        {
            if (!GetFileInformationByHandleEx(directory, 23, out var flags, sizeof(uint)))
                throw Unknown();
            return (flags & 1) != 0;
        }
        if (OperatingSystem.IsMacOS())
        {
            var value = fpathconf(directory, 11);
            if (value < 0) throw Unknown();
            return value > 0;
        }
        if (OperatingSystem.IsLinux())
        {
            // O_PATH pins do not support ioctl; reopen the same fixed directory, not its pathname.
            using var query = UnixPathPin.OpenQueryDirectory(directory);
            if (LinuxFileSystemType(query) != 0xef53)
                throw new NotSupportedException("The pinned Linux filesystem does not expose a supported per-directory case query; no file access was authorized.");
            var request = (nuint)(IntPtr.Size == 8 ? 0x80086601u : 0x80046601u);
            if (ioctl(query, request, out var flags) != 0) throw Unknown();
            return (flags & 0x40000000) == 0;
        }
        throw new PlatformNotSupportedException("Native directory case queries require Windows, Linux or macOS.");
    }

    private static unsafe long LinuxFileSystemType(SafeFileHandle directory)
    {
        // Linux statfs starts with a native-long filesystem type; preserve space for its full ABI structure.
        var buffer = stackalloc byte[512];
        if (fstatfs(directory, buffer) != 0) throw Unknown();
        return IntPtr.Size == 8 ? *(long*)buffer : *(int*)buffer;
    }

    private static NotSupportedException Unknown() => new(
        "The pinned directory does not expose reliable case semantics required by this path wildcard; no file access was authorized.",
        new Win32Exception(Marshal.GetLastPInvokeError()));

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleEx(SafeFileHandle directory, int informationClass, out uint flags, uint size);
    [DllImport("libc", SetLastError = true)]
    private static extern nint fpathconf(SafeFileHandle directory, int name);
    [DllImport("libc", SetLastError = true)]
    private static extern int ioctl(SafeFileHandle directory, nuint request, out int flags);
    [DllImport("libc", SetLastError = true)]
    private static extern unsafe int fstatfs(SafeFileHandle directory, byte* buffer);
}
