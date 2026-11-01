using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Dsh.Sandbox;

/// <summary>Owns filesystem objects reached through descriptor-relative, no-follow traversal.</summary>
internal static class UnixPathPin
{
    public static SafeFileHandle Open(string canonicalPath, bool directory)
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
            throw new PlatformNotSupportedException("Unix path pins require Linux or macOS.");
        if (!Path.IsPathFullyQualified(canonicalPath) || canonicalPath.Contains('\0'))
            throw new ArgumentException("A path pin requires an absolute canonical path.", nameof(canonicalPath));
        var current = OpenDescriptor(-1, "/", directory: true);
        try
        {
            var components = canonicalPath.Split('/', StringSplitOptions.RemoveEmptyEntries);
            for (var index = 0; index < components.Length; index++)
            {
                var next = OpenRelative(current, components[index], index != components.Length - 1 || directory);
                current.Dispose();
                current = next;
            }
            if (components.Length == 0 && !directory)
                throw new ArgumentException("A filesystem root is not a file.", nameof(canonicalPath));
            return current;
        }
        catch { current.Dispose(); throw; }
    }

    public static SafeFileHandle OpenRelative(SafeFileHandle directory, string singleName, bool isDirectory)
    {
        ArgumentNullException.ThrowIfNull(directory);
        if (string.IsNullOrEmpty(singleName) || singleName is "." or ".." || singleName.IndexOfAny(['/', '\0']) >= 0)
            throw new ArgumentException("A relative pin requires one ordinary filename component.", nameof(singleName));
        var added = false;
        try
        {
            directory.DangerousAddRef(ref added);
            return OpenDescriptor(checked((int)directory.DangerousGetHandle()), singleName, isDirectory);
        }
        finally { if (added) directory.DangerousRelease(); }
    }

    public static string PathOf(SafeFileHandle handle)
    {
        var added = false;
        try
        {
            handle.DangerousAddRef(ref added);
            var descriptor = checked((int)handle.DangerousGetHandle());
            if (OperatingSystem.IsLinux())
            {
                var buffer = new byte[4096];
                var length = readlink($"/proc/self/fd/{descriptor}", buffer, (nuint)buffer.Length);
                if (length < 0)
                    throw new Win32Exception(Marshal.GetLastPInvokeError(), "Cannot resolve a pinned filesystem object.");
                if (length == buffer.Length)
                    throw new NotSupportedException("The pinned path exceeds the supported path length.");
                var path = Encoding.UTF8.GetString(buffer, 0, checked((int)length));
                if (path.EndsWith(" (deleted)", StringComparison.Ordinal))
                    throw new IOException("A pinned filesystem object was unlinked.");
                return path;
            }
            if (OperatingSystem.IsMacOS())
            {
                var buffer = new byte[1024];
                if (fcntl(descriptor, 50, buffer) != 0)
                    throw new Win32Exception(Marshal.GetLastPInvokeError(), "Cannot resolve a pinned filesystem object.");
                var length = Array.IndexOf(buffer, (byte)0);
                if (length < 0)
                    throw new NotSupportedException("The pinned path exceeds the supported path length.");
                return Encoding.UTF8.GetString(buffer, 0, length);
            }
            throw new PlatformNotSupportedException("Unix path pins require Linux or macOS.");
        }
        finally { if (added) handle.DangerousRelease(); }
    }

    internal static IEnumerable<(string Name, bool Directory, bool Regular)> Entries(SafeFileHandle directory)
    {
        using var duplicate = OpenDescriptor(checked((int)directory.DangerousGetHandle()), ".", directory: true, enumerate: true);
        var descriptor = dup(checked((int)duplicate.DangerousGetHandle()));
        if (descriptor < 0)
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "Cannot duplicate a pinned directory for enumeration.");
        var stream = fdopendir(descriptor);
        if (stream == 0)
        {
            var error = Marshal.GetLastPInvokeError();
            close(descriptor);
            throw new Win32Exception(error, "Cannot enumerate a pinned directory.");
        }
        try
        {
            while (true)
            {
                ClearDirectoryError();
                var entry = OperatingSystem.IsMacOS() && RuntimeInformation.ProcessArchitecture == Architecture.X64 ? readdirInode64(stream) : readdir(stream);
                if (entry == 0)
                {
                    if (Marshal.GetLastPInvokeError() != 0)
                        throw new Win32Exception(Marshal.GetLastPInvokeError(), "Cannot read a pinned directory entry.");
                    yield break;
                }
                var nameOffset = OperatingSystem.IsLinux() ? 19 : 21;
                var typeOffset = OperatingSystem.IsLinux() ? 18 : 20;
                var name = Marshal.PtrToStringUTF8(entry + nameOffset)!;
                if (name is "." or "..")
                    continue;
                var type = Marshal.ReadByte(entry, typeOffset);
                if (type == 0)
                    type = checked((byte)(NativeFileIdentity.UnixTypeAt(directory, name) >> 12));
                yield return (name, type == 4, type == 8);
            }
        }
        finally { closedir(stream); }
    }

    private static SafeFileHandle OpenDescriptor(int parent, string name, bool directory, bool enumerate = false)
    {
        var flags = OperatingSystem.IsLinux()
            ? 0x80000 | 0x20000 | (directory ? 0x10000 : 0) | (enumerate ? 0 : 0x200000)
            : 0x1000000 | 0x100 | 0x4 | (directory ? 0x100000 : 0);
        var descriptor = parent < 0 ? open(name, flags) : openat(parent, name, flags);
        if (descriptor < 0)
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "Cannot pin a sandbox path without following links.");
        var handle = new SafeFileHandle(descriptor, ownsHandle: true);
        try
        {
            var identity = NativeFileIdentity.Get(handle);
            if (!directory && !identity.IsRegular)
                throw new NotSupportedException("A sandbox file pin must refer to a regular file, never a link or device.");
            return handle;
        }
        catch { handle.Dispose(); throw; }
    }

    private static void ClearDirectoryError()
    {
        var error = OperatingSystem.IsLinux() ? errnoLocation() : darwinError();
        Marshal.WriteInt32(error, 0);
    }

    [DllImport("libc", SetLastError = true)] private static extern int open([MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags);
    [DllImport("libc", SetLastError = true)] private static extern int openat(int directory, [MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags);
    [DllImport("libc", SetLastError = true)] private static extern nint readlink([MarshalAs(UnmanagedType.LPUTF8Str)] string path, byte[] buffer, nuint size);
    [DllImport("libc", SetLastError = true)] private static extern int fcntl(int descriptor, int command, byte[] buffer);
    [DllImport("libc", SetLastError = true)] private static extern int dup(int descriptor);
    [DllImport("libc", SetLastError = true)] private static extern nint fdopendir(int descriptor);
    [DllImport("libc", SetLastError = true)] private static extern nint readdir(nint directory);
    [DllImport("libc", EntryPoint = "readdir$INODE64", SetLastError = true)] private static extern nint readdirInode64(nint directory);
    [DllImport("libc")] private static extern int closedir(nint directory);
    [DllImport("libc")] private static extern int close(int descriptor);
    [DllImport("libc", EntryPoint = "__errno_location")] private static extern nint errnoLocation();
    [DllImport("libc", EntryPoint = "__error")] private static extern nint darwinError();
}
