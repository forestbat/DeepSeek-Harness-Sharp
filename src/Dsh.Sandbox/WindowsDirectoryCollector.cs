using System.ComponentModel;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace Dsh.Sandbox;

/// <summary>Collects non-reparse descendants using bounded root-relative native directory reads.</summary>
[SupportedOSPlatform("windows")]
internal static class WindowsDirectoryCollector
{
    private const int BufferSize = 16 * 1024;
    private const int WorkerCount = 128;
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
    /// Root entries are read and published before any descendant is opened.
    /// One hundred twenty-eight dedicated workers each own one 16 KiB buffer and normally one descendant handle.
    /// Root-relative names exceeding the native counted-name limit are split at directory separators.
    /// Such opens transiently own two descendant handles per worker; queued directories own none.
    /// Paths, entry topology and discovery/publication queues cost O(entries).
    /// All workers join before descendant publication; callbacks retain native entry and FIFO directory order.
    /// Native calls are synchronous, with cancellation checked before opens, between entries and after completion.
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
        var rootDirectory = new DirectoryWork(rootPath);
        var pending = new Queue<DirectoryWork>();
        var buffer = Marshal.AllocHGlobal(BufferSize);
        try
        {
            CollectDirectory(rootHandle, rootDirectory, buffer, cancellationToken);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
        PublishDirectory(rootDirectory, pending, accept, cancellationToken);
        if (pending.Count != 0)
        {
            var work = new WorkQueue(rootHandle, rootPath, pending, cancellationToken);
            var workers = new Thread[WorkerCount];
            var started = 0;
            using var registration = cancellationToken.UnsafeRegister(static state =>
            {
                var queue = (WorkQueue)state!;
                queue.Fail(new OperationCanceledException(queue.CancellationToken));
            }, work);
            try
            {
                for (; started < workers.Length; started++)
                {
                    work.ThrowIfStopped();
                    var worker = new Thread(work.Run) { IsBackground = true };
                    workers[started] = worker;
                    worker.Start();
                }
            }
            catch (Exception error)
            {
                work.Fail(error);
            }
            finally
            {
                for (var index = 0; index < started; index++)
                {
                    while (true)
                    {
                        try
                        {
                            workers[index].Join();
                            break;
                        }
                        catch (ThreadInterruptedException error)
                        {
                            work.Fail(error);
                        }
                    }
                }
            }
            work.ThrowIfStopped();
        }
        while (pending.TryDequeue(out var directory))
        {
            PublishDirectory(directory, pending, accept, cancellationToken);
        }
        cancellationToken.ThrowIfCancellationRequested();
    }

    private static unsafe void CollectDirectory(SafeFileHandle handle, DirectoryWork directory, nint buffer,
        CancellationToken cancellationToken, WorkQueue? work = null)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            work?.ThrowIfStopped();
            var status = NtQueryDirectoryFile(handle, 0, 0, 0, out var ioStatus, buffer, BufferSize,
                FileFullDirectoryInformation, 0, 0, 0);
            if (status is NoMoreFiles or NoSuchFile)
                return;
            if (status != 0)
                throw DirectoryError((int)RtlNtStatusToDosError(status), directory.Path);
            if (ioStatus.Information == 0)
                return;
            var entry = (DirectoryInformation*)buffer;
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                work?.ThrowIfStopped();
                var name = new ReadOnlySpan<char>(&entry->FileName, checked((int)entry->FileNameLength / sizeof(char)));
                if ((entry->Attributes & FileAttributes.ReparsePoint) == 0 && !name.SequenceEqual(".") && !name.SequenceEqual(".."))
                {
                    var path = Path.Join(directory.Path.AsSpan(), name);
                    var child = (entry->Attributes & FileAttributes.Directory) != 0 ? new DirectoryWork(path) : null;
                    directory.Entries.Add(new Entry(path, child));
                }
                if (entry->NextEntryOffset == 0)
                    break;
                entry = (DirectoryInformation*)((byte*)entry + entry->NextEntryOffset);
            }
        }
    }

    private static void PublishDirectory(DirectoryWork directory, Queue<DirectoryWork> pending,
        Action<string, bool> accept, CancellationToken cancellationToken)
    {
        foreach (var entry in directory.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            accept(entry.Path, entry.Directory is not null);
            cancellationToken.ThrowIfCancellationRequested();
            if (entry.Directory is { } child)
                pending.Enqueue(child);
        }
    }

    private sealed class DirectoryWork(string path)
    {
        public string Path { get; } = path;
        public List<Entry> Entries { get; } = [];
    }

    private readonly record struct Entry(string Path, DirectoryWork? Directory);

    private sealed class WorkQueue
    {
        private readonly object _gate = new();
        private readonly SafeFileHandle _root;
        private readonly int _relativeStart;
        private readonly Queue<DirectoryWork> _pending;
        private int _outstanding;
        private ExceptionDispatchInfo? _failure;

        public CancellationToken CancellationToken { get; }

        public WorkQueue(SafeFileHandle root, string rootPath, Queue<DirectoryWork> directories,
            CancellationToken cancellationToken)
        {
            _root = root;
            _relativeStart = rootPath.Length + (Path.EndsInDirectorySeparator(rootPath) ? 0 : 1);
            _pending = new Queue<DirectoryWork>(directories);
            _outstanding = _pending.Count;
            CancellationToken = cancellationToken;
        }

        public void Run()
        {
            nint buffer = 0;
            try
            {
                buffer = Marshal.AllocHGlobal(BufferSize);
                while (Take() is { } directory)
                {
                    ThrowIfStopped();
                    CancellationToken.ThrowIfCancellationRequested();
                    using (var handle = OpenRelativeDirectory(_root, directory.Path.AsSpan(_relativeStart), directory.Path, this))
                    {
                        CollectDirectory(handle, directory, buffer, CancellationToken, this);
                    }
                    Complete(directory);
                }
            }
            catch (Exception error)
            {
                Fail(error);
            }
            finally
            {
                if (buffer != 0)
                    Marshal.FreeHGlobal(buffer);
            }
        }

        public void ThrowIfStopped() => Volatile.Read(ref _failure)?.Throw();

        public void Fail(Exception error)
        {
            lock (_gate)
            {
                if (_failure is not null)
                    return;
                Volatile.Write(ref _failure, ExceptionDispatchInfo.Capture(error));
                Monitor.PulseAll(_gate);
            }
        }

        private DirectoryWork? Take()
        {
            lock (_gate)
            {
                while (_failure is null && _pending.Count == 0 && _outstanding != 0)
                    Monitor.Wait(_gate);
                return _failure is not null || _outstanding == 0 ? null : _pending.Dequeue();
            }
        }

        private void Complete(DirectoryWork directory)
        {
            lock (_gate)
            {
                if (_failure is not null)
                    return;
                // Outstanding counts queued plus active nodes. Publish children before retiring their parent.
                foreach (var entry in directory.Entries)
                {
                    if (entry.Directory is not { } child)
                        continue;
                    _pending.Enqueue(child);
                    _outstanding = checked(_outstanding + 1);
                }
                _outstanding--;
                Monitor.PulseAll(_gate);
            }
        }
    }

    private static SafeFileHandle OpenRelativeDirectory(SafeFileHandle root, ReadOnlySpan<char> relativePath,
        string path, WorkQueue work)
    {
        const int maximumNameLength = ushort.MaxValue / sizeof(char);
        if (relativePath.Length <= maximumNameLength)
            return OpenDirectorySegment(root, relativePath, path);

        SafeFileHandle? current = null;
        try
        {
            while (true)
            {
                work.CancellationToken.ThrowIfCancellationRequested();
                work.ThrowIfStopped();
                var segmentLength = relativePath.Length;
                if (segmentLength > maximumNameLength)
                {
                    segmentLength = relativePath[..(maximumNameLength + 1)].LastIndexOfAny('\\', '/');
                    if (segmentLength <= 0)
                        throw new PathTooLongException($"Unable to open directory '{path}'.");
                }
                var next = OpenDirectorySegment(current ?? root, relativePath[..segmentLength], path);
                if (segmentLength == relativePath.Length)
                    return next;
                var previous = current;
                current = next;
                previous?.Dispose();
                relativePath = relativePath[(segmentLength + 1)..];
            }
        }
        finally
        {
            current?.Dispose();
        }
    }

    private static unsafe SafeFileHandle OpenDirectorySegment(SafeFileHandle parent, ReadOnlySpan<char> relativePath, string path)
    {
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
                RootDirectory = parent.DangerousGetHandle(),
                ObjectName = &nativeName,
                Attributes = CaseInsensitive
            };
            var status = NtOpenFile(out var handle, ListDirectory | Synchronize, ref attributes, out _, ShareAll, SynchronousDirectoryOpen);
            GC.KeepAlive(parent);
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
