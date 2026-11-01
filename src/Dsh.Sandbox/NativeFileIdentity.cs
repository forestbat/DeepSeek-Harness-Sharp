using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Dsh.Sandbox;

public readonly record struct NativeFileIdentity(ulong Device, ulong Inode, uint Links)
{
    public bool IsRegular { get; init; }
    public static unsafe NativeFileIdentity Get(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            using var handle = WindowsNative.CreateFile(path, 0x80, 7, 0, 3, 0x02200000, 0);
            if (handle.IsInvalid)
                throw new Win32Exception(Marshal.GetLastPInvokeError(), "Cannot open a sandbox file identity.");
            return Get(handle);
        }
        if (OperatingSystem.IsMacOS())
        {
            var buffer = stackalloc byte[512];
            new Span<byte>(buffer, 512).Clear();
            if ((RuntimeInformation.ProcessArchitecture == Architecture.X64 ? statInode64(path, buffer) : stat(path, buffer)) != 0)
                throw new Win32Exception(Marshal.GetLastPInvokeError(), "Cannot inspect a sandbox file identity.");
            return new NativeFileIdentity(*(uint*)buffer, *(ulong*)(buffer + 8), *(ushort*)(buffer + 6))
            {
                IsRegular = (*(ushort*)(buffer + 4) & FileTypeMask) == RegularFile,
            };
        }
        using var pinned = OpenLinuxIdentity(path);
        return Get(pinned);
    }

    public static unsafe NativeFileIdentity Get(SafeFileHandle handle)
    {
        ArgumentNullException.ThrowIfNull(handle);
        if (OperatingSystem.IsWindows())
        {
            if (!GetFileInformationByHandle(handle, out var info))
                throw new Win32Exception(Marshal.GetLastPInvokeError(), "Cannot inspect a sandbox file identity.");
            return new NativeFileIdentity(info.VolumeSerialNumber, ((ulong)info.FileIndexHigh << 32) | info.FileIndexLow, info.NumberOfLinks)
            {
                IsRegular = (info.Attributes & (FileAttributeDirectory | 0x400)) == 0 && GetFileType(handle) == FileTypeDisk,
            };
        }
        var buffer = stackalloc byte[512];
        new Span<byte>(buffer, 512).Clear();
        if (OperatingSystem.IsLinux())
        {
            if (statx(handle, "", 0x1000, 0x7ff, buffer) != 0)
                throw new Win32Exception(Marshal.GetLastPInvokeError(), "Cannot inspect a sandbox file identity.");
            if ((*(uint*)buffer & 0x105) != 0x105)
                throw new NotSupportedException("The filesystem does not report reliable types, link counts and inode identities.");
            return new NativeFileIdentity(((ulong)*(uint*)(buffer + 136) << 32) | *(uint*)(buffer + 140), *(ulong*)(buffer + 32), *(uint*)(buffer + 16))
            {
                IsRegular = (*(ushort*)(buffer + 28) & FileTypeMask) == RegularFile,
            };
        }
        if (!OperatingSystem.IsMacOS())
            throw new PlatformNotSupportedException("Native file identities require Windows, Linux or macOS.");
        if ((RuntimeInformation.ProcessArchitecture == Architecture.X64 ? fstatInode64(handle, buffer) : fstat(handle, buffer)) != 0)
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "Cannot inspect a sandbox file identity.");
        return new NativeFileIdentity(*(uint*)buffer, *(ulong*)(buffer + 8), *(ushort*)(buffer + 6))
        {
            IsRegular = (*(ushort*)(buffer + 4) & FileTypeMask) == RegularFile,
        };
    }

    internal static unsafe NativeFileIdentity GetRelative(SafeFileHandle directory, string name)
    {
        var buffer = stackalloc byte[512];
        new Span<byte>(buffer, 512).Clear();
        if (OperatingSystem.IsLinux())
        {
            if (statx(directory, name, 0x100, 0x7ff, buffer) != 0)
                throw new Win32Exception(Marshal.GetLastPInvokeError(), "Cannot inspect a pinned directory entry.");
            if ((*(uint*)buffer & 0x105) != 0x105)
                throw new NotSupportedException("The filesystem does not report reliable entry identities.");
            return new NativeFileIdentity(((ulong)*(uint*)(buffer + 136) << 32) | *(uint*)(buffer + 140), *(ulong*)(buffer + 32), *(uint*)(buffer + 16))
            {
                IsRegular = (*(ushort*)(buffer + 28) & FileTypeMask) == RegularFile,
            };
        }
        if (!OperatingSystem.IsMacOS())
            throw new PlatformNotSupportedException("Relative Unix identities require Linux or macOS.");
        if ((RuntimeInformation.ProcessArchitecture == Architecture.X64 ? fstatatInode64(directory, name, buffer, 0x20) : fstatat(directory, name, buffer, 0x20)) != 0)
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "Cannot inspect a pinned directory entry.");
        return new NativeFileIdentity(*(uint*)buffer, *(ulong*)(buffer + 8), *(ushort*)(buffer + 6))
        {
            IsRegular = (*(ushort*)(buffer + 4) & FileTypeMask) == RegularFile,
        };
    }

    internal static unsafe int UnixTypeAt(SafeFileHandle directory, string name)
    {
        var buffer = stackalloc byte[512];
        new Span<byte>(buffer, 512).Clear();
        if (OperatingSystem.IsLinux())
        {
            if (statx(directory, name, 0x100, 1, buffer) != 0)
                throw new Win32Exception(Marshal.GetLastPInvokeError(), "Cannot inspect a pinned directory entry.");
            if ((*(uint*)buffer & 1) == 0)
                throw new NotSupportedException("The filesystem does not report reliable entry types.");
            return *(ushort*)(buffer + 28) & FileTypeMask;
        }
        if (!OperatingSystem.IsMacOS())
            throw new PlatformNotSupportedException("Unix entry types require Linux or macOS.");
        if ((RuntimeInformation.ProcessArchitecture == Architecture.X64 ? fstatatInode64(directory, name, buffer, 0x20) : fstatat(directory, name, buffer, 0x20)) != 0)
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "Cannot inspect a pinned directory entry.");
        return *(ushort*)(buffer + 4) & FileTypeMask;
    }

    private static SafeFileHandle OpenLinuxIdentity(string path)
    {
        var descriptor = open(path, 0x200000 | 0x20000 | 0x80000);
        if (descriptor < 0)
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "Cannot open a sandbox file identity.");
        return new SafeFileHandle(descriptor, ownsHandle: true);
    }

    private const uint FileAttributeDirectory = 0x10;
    private const uint FileTypeDisk = 1;
    private const int FileTypeMask = 0xf000;
    private const int RegularFile = 0x8000;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint GetFileType(SafeFileHandle handle);

    [StructLayout(LayoutKind.Sequential)]
    private struct FileInformation
    {
        public uint Attributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME CreationTime, LastAccessTime, LastWriteTime;
        public uint VolumeSerialNumber, FileSizeHigh, FileSizeLow, NumberOfLinks, FileIndexHigh, FileIndexLow;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle file, out FileInformation information);
    [DllImport("libc", SetLastError = true)]
    private static extern unsafe int statx(SafeFileHandle directory, [MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags, uint mask, byte* buffer);
    [DllImport("libc", EntryPoint = "fstat", SetLastError = true)]
    private static extern unsafe int fstat(SafeFileHandle descriptor, byte* buffer);
    [DllImport("libc", EntryPoint = "fstat$INODE64", SetLastError = true)]
    private static extern unsafe int fstatInode64(SafeFileHandle descriptor, byte* buffer);
    [DllImport("libc", SetLastError = true)]
    private static extern int open([MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags);
    [DllImport("libc", EntryPoint = "stat", SetLastError = true)]
    private static extern unsafe int stat([MarshalAs(UnmanagedType.LPUTF8Str)] string path, byte* buffer);
    [DllImport("libc", EntryPoint = "stat$INODE64", SetLastError = true)]
    private static extern unsafe int statInode64([MarshalAs(UnmanagedType.LPUTF8Str)] string path, byte* buffer);
    [DllImport("libc", EntryPoint = "fstatat", SetLastError = true)]
    private static extern unsafe int fstatat(SafeFileHandle directory, [MarshalAs(UnmanagedType.LPUTF8Str)] string path, byte* buffer, int flags);
    [DllImport("libc", EntryPoint = "fstatat$INODE64", SetLastError = true)]
    private static extern unsafe int fstatatInode64(SafeFileHandle directory, [MarshalAs(UnmanagedType.LPUTF8Str)] string path, byte* buffer, int flags);
}
