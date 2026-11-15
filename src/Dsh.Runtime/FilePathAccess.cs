using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Dsh.Runtime;

/// <summary>Ordinary filesystem object checks shared by host services.</summary>
public static class FilePathAccess
{
    private const int IdentityBufferSize = 512;
    private const int LinuxCurrentDirectory = -100;
    private const uint LinuxInodeMask = 0x100;
    private const int LinuxInodeOffset = 32;
    private const int LinuxDeviceMajorOffset = 136;
    private const int LinuxDeviceMinorOffset = 140;
    private const int DarwinInodeOffset = 8;
    private const uint WindowsOpenExisting = 3;
    private const uint WindowsBackupSemantics = 0x02000000;
    private const int WindowsFileIdInfo = 18;
    private const uint WindowsFileIdInfoSize = 24;

    private readonly record struct FileIdentity(ulong Volume, ulong IdLow, ulong IdHigh);

    /** Compares existing directories by filesystem object identity, independently of path casing. */
    public static bool SameDirectory(string left, string right)
        => Directory.Exists(left) && Directory.Exists(right) && DirectoryIdentity(left) == DirectoryIdentity(right);

    private static unsafe FileIdentity DirectoryIdentity(string path)
    {
        if (OperatingSystem.IsWindows())
            return WindowsDirectoryIdentity(path);
        var buffer = stackalloc byte[IdentityBufferSize];
        new Span<byte>(buffer, IdentityBufferSize).Clear();
        if (OperatingSystem.IsLinux())
        {
            if (statx(LinuxCurrentDirectory, path, 0, LinuxInodeMask, buffer) != 0)
                throw new Win32Exception(Marshal.GetLastPInvokeError(), "Cannot inspect the requested directory identity.");
            if ((*(uint*)buffer & LinuxInodeMask) == 0)
                throw new NotSupportedException("The filesystem does not expose directory inode identities.");
            var volume = ((ulong)*(uint*)(buffer + LinuxDeviceMajorOffset) << 32) | *(uint*)(buffer + LinuxDeviceMinorOffset);
            return new FileIdentity(volume, *(ulong*)(buffer + LinuxInodeOffset), 0);
        }
        if (OperatingSystem.IsMacOS())
        {
            if ((RuntimeInformation.ProcessArchitecture == Architecture.X64 ? statInode64(path, buffer) : stat(path, buffer)) != 0)
                throw new Win32Exception(Marshal.GetLastPInvokeError(), "Cannot inspect the requested directory identity.");
            return new FileIdentity(*(uint*)buffer, *(ulong*)(buffer + DarwinInodeOffset), 0);
        }
        throw new PlatformNotSupportedException("Directory identities require Windows, Linux or macOS.");
    }

    private static unsafe FileIdentity WindowsDirectoryIdentity(string path)
    {
        const string extendedPrefix = @"\\?\";
        var fullPath = Path.GetFullPath(path).Replace('/', '\\');
        if (!fullPath.StartsWith(extendedPrefix, StringComparison.Ordinal))
            fullPath = fullPath.StartsWith(@"\\", StringComparison.Ordinal)
                ? $@"\\?\UNC\{fullPath.AsSpan(2)}" : $"{extendedPrefix}{fullPath}";
        using var handle = CreateFile(fullPath, 0, FileShare.ReadWrite | FileShare.Delete, 0,
            WindowsOpenExisting, WindowsBackupSemantics, 0);
        if (handle.IsInvalid)
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "Cannot open the requested directory for identity lookup.");
        var identity = stackalloc ulong[3];
        if (!GetFileInformationByHandleEx(handle, WindowsFileIdInfo, identity, WindowsFileIdInfoSize))
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "Cannot inspect the requested directory identity.");
        return new FileIdentity(identity[0], identity[1], identity[2]);
    }

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

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string path, uint access, FileShare share, nint security,
        uint disposition, uint flags, nint template);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern unsafe bool GetFileInformationByHandleEx(SafeFileHandle handle, int informationClass,
        void* information, uint size);
}
