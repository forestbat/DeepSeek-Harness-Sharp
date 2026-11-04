using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace Dsh.Sandbox;

/// <summary>Collects non-reparse descendants using root-relative directory opens and one native buffer.</summary>
[SupportedOSPlatform("windows")]
internal static class WindowsDirectoryCollector
{
    private const int BufferSize = 16 * 1024;
    private const uint ListDirectory = 1;
    private const uint Synchronize = 0x00100000;
    private const uint BackupSemantics = 0x02000000;
    private const uint SynchronousDirectoryOpen = 0x00004021;
    private const int FileFullDirectoryInformation = 2;
    private const int NoMoreFiles = unchecked((int)0x80000006);
    private const int NoSuchFile = unchecked((int)0xc000000f);
    private const uint ShareAll = (uint)(FileShare.Read | FileShare.Write | FileShare.Delete);
    private const uint OpenExisting = 3;
    private const uint CaseInsensitive = 0x40;
    private const int ErrorFileNotFound = 2;
    private const int ErrorPathNotFound = 3;
    private const int ErrorAccessDenied = 5;
    private const int ErrorFilenameExceedsRange = 206;
    private const int ErrorOperationAborted = 995;
    private const int HResultHighWordMask = unchecked((int)0xffff0000);
    private const int Win32HResultMask = unchecked((int)0x80070000);

    /// <summary>
    /// Visits descendants, excluding the root, serially on the calling thread in native entry and FIFO directory order.
    /// No callback runs after this method returns or throws.
    /// Callers must finish collecting all roots before modifying any ACL.
    /// </summary>
    /// <remarks>
    /// Each queued path is opened strictly relative to the retained root when its FIFO turn arrives.
    /// Only pending directory paths are retained, with at most the root and one current directory handle open.
    /// Native calls are synchronous, with cancellation checked before directory opens, between entries and after completion.
    /// </remarks>
    public static unsafe void Collect(string root, Action<string, bool> accept, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(accept);
        cancellationToken.ThrowIfCancellationRequested();
        var rootPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var nativeRoot = rootPath.StartsWith(@"\\", StringComparison.Ordinal)
            && !rootPath.StartsWith(@"\\?\", StringComparison.Ordinal)
            ? $@"\\?\UNC\{rootPath.AsSpan(2)}" : rootPath;
        cancellationToken.ThrowIfCancellationRequested();
        using var rootHandle = WindowsNative.CreateFile(nativeRoot, ListDirectory, ShareAll, 0, OpenExisting, BackupSemantics, 0);
        if (rootHandle.IsInvalid)
            throw DirectoryError(Marshal.GetLastPInvokeError(), rootPath, openingDirectory: true);
        var relativeStart = rootPath.Length + (Path.EndsInDirectorySeparator(rootPath) ? 0 : 1);
        var pending = new Queue<string>();
        pending.Enqueue(rootPath);
        var buffer = Marshal.AllocHGlobal(BufferSize);
        try
        {
            while (pending.TryDequeue(out var directory))
            {
                cancellationToken.ThrowIfCancellationRequested();
                using var childHandle = directory.Length == rootPath.Length ? null
                    : OpenRelativeDirectory(rootHandle, directory.AsSpan(relativeStart), directory);
                CollectDirectory(childHandle ?? rootHandle, directory, buffer, pending, accept, cancellationToken);
            }
            cancellationToken.ThrowIfCancellationRequested();
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static unsafe void CollectDirectory(SafeFileHandle handle, string directory, nint buffer,
        Queue<string> pending, Action<string, bool> accept, CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var status = NtQueryDirectoryFile(handle, 0, 0, 0, out var ioStatus, buffer, BufferSize,
                FileFullDirectoryInformation, 0, 0, 0);
            if (status is NoMoreFiles or NoSuchFile)
                return;
            if (status != 0)
                throw DirectoryError((int)RtlNtStatusToDosError(status), directory);
            if (ioStatus.Information == 0)
                return;
            var entry = (DirectoryInformation*)buffer;
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var name = new ReadOnlySpan<char>(&entry->FileName, checked((int)entry->FileNameLength / sizeof(char)));
                if ((entry->Attributes & FileAttributes.ReparsePoint) == 0 && !name.SequenceEqual(".") && !name.SequenceEqual(".."))
                {
                    var path = Path.Join(directory.AsSpan(), name);
                    var isDirectory = (entry->Attributes & FileAttributes.Directory) != 0;
                    accept(path, isDirectory);
                    cancellationToken.ThrowIfCancellationRequested();
                    if (isDirectory)
                        pending.Enqueue(path);
                }
                if (entry->NextEntryOffset == 0)
                    break;
                entry = (DirectoryInformation*)((byte*)entry + entry->NextEntryOffset);
            }
        }
    }

    private static unsafe SafeFileHandle OpenRelativeDirectory(SafeFileHandle root, ReadOnlySpan<char> relativePath, string path)
    {
        if (relativePath.Length > ushort.MaxValue / sizeof(char))
            throw new PathTooLongException($"Unable to open directory '{path}'.");
        fixed (char* text = relativePath)
        {
            var nativeName = new UnicodeString
            {
                Length = (ushort)(relativePath.Length * sizeof(char)),
                MaximumLength = (ushort)(relativePath.Length * sizeof(char)),
                Buffer = text
            };
            var attributes = new ObjectAttributes
            {
                Length = (uint)sizeof(ObjectAttributes),
                RootDirectory = root.DangerousGetHandle(),
                ObjectName = &nativeName,
                Attributes = CaseInsensitive
            };
            var status = NtOpenFile(out var handle, ListDirectory | Synchronize, ref attributes, out _, ShareAll, SynchronousDirectoryOpen);
            GC.KeepAlive(root);
            if (status == 0)
                return handle;
            handle.Dispose();
            throw DirectoryError((int)RtlNtStatusToDosError(status), path, openingDirectory: true);
        }
    }

    private static Exception DirectoryError(int error, string path, bool openingDirectory = false)
    {
        // Like a nonrecursive FileSystemEnumerator root open, a missing directory is not a missing file.
        if (openingDirectory && error == ErrorFileNotFound)
            error = ErrorPathNotFound;
        var message = $"Unable to enumerate directory '{path}': {new Win32Exception(error).Message}";
        return error switch
        {
            ErrorFileNotFound => new FileNotFoundException(message, path),
            ErrorPathNotFound => new DirectoryNotFoundException(message),
            ErrorAccessDenied => new UnauthorizedAccessException(message),
            ErrorFilenameExceedsRange => new PathTooLongException(message),
            ErrorOperationAborted => new OperationCanceledException(message),
            _ => new IOException(message, (error & HResultHighWordMask) != 0 ? error : error | Win32HResultMask)
        };
    }

    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct DirectoryInformation
    {
        public uint NextEntryOffset, FileIndex;
        public long CreationTime, LastAccessTime, LastWriteTime, ChangeTime, EndOfFile, AllocationSize;
        public FileAttributes Attributes;
        public uint FileNameLength, EaSize;
        public char FileName;
    }

    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct UnicodeString
    {
        public ushort Length, MaximumLength;
        public char* Buffer;
    }

    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct ObjectAttributes
    {
        public uint Length;
        public nint RootDirectory;
        public UnicodeString* ObjectName;
        public uint Attributes;
        public nint SecurityDescriptor, SecurityQualityOfService;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoStatusBlock
    {
        public nint Status;
        public nuint Information;
    }

    [DllImport("ntdll.dll")]
    private static extern int NtQueryDirectoryFile(SafeFileHandle handle, nint completionEvent, nint apcRoutine,
        nint apcContext, out IoStatusBlock statusBlock, nint information, uint length, int informationClass,
        byte returnSingleEntry, nint fileName, byte restartScan);

    [DllImport("ntdll.dll")]
    private static extern unsafe int NtOpenFile(out SafeFileHandle handle, uint access, ref ObjectAttributes attributes,
        out IoStatusBlock statusBlock, uint share, uint options);

    [DllImport("ntdll.dll")]
    private static extern uint RtlNtStatusToDosError(int status);
}
