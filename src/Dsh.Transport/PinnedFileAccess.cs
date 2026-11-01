using System.ComponentModel;
using System.Runtime.InteropServices;
using Dsh.Sandbox;
using Microsoft.Win32.SafeHandles;

namespace Dsh.Transport;

public readonly record struct PinnedFileTarget(string Path, NativeFileIdentity? Identity, NativeFileIdentity ParentIdentity, bool IsDirectory = false);

public enum PinnedFileMode { Read, Replace, Overwrite, Append, CreateNew }

/** Authorization and mutation share pinned directory objects instead of resolving writable ancestors again. */
public sealed class PinnedFileAccess : IDisposable, IAsyncDisposable
{
    private const int BufferBytes = 4096;
    private const int MaximumLinks = 40;
    private const int MaximumDirectoryDepth = 128;
    private const int MissingPath = 2;
    private const int AlreadyExists = 17;
    private const uint OwnerDirectoryPermissions = 0x1c0;
    private const uint OwnerFilePermissions = 0x180;
    private readonly List<SafeFileHandle> _directories = [];
    private readonly Action<PinnedFileTarget> _authorize;
    private readonly PinnedFileMode _mode;
    private readonly string _leaf;
    private readonly string _parentPath;
    private readonly string? _temporary;
    private readonly NativeFileIdentity _parentIdentity;
    private bool _completed;
    private bool _disposed;
    private bool _ownsTemporary;
    public FileStream Stream { get; private set; } = null!;

    private PinnedFileAccess(string path, PinnedFileMode mode, Action<PinnedFileTarget> authorize, bool allowNetwork)
    {
        _authorize = authorize;
        _mode = mode;
        var canonical = Canonicalize(path, allowNetwork);
        _leaf = Path.GetFileName(canonical);
        if (string.IsNullOrEmpty(_leaf))
            throw new ArgumentException("A file write requires a non-root file path.", nameof(path));
        _parentPath = Path.GetDirectoryName(canonical)!;
        try
        {
            PinDirectories(_parentPath);
            _parentIdentity = NativeFileIdentity.Get(_directories[^1]);
            if (mode == PinnedFileMode.Replace)
            {
                AuthorizeReplacement();
                _temporary = $".{Guid.NewGuid():N}.tmp";
                Stream = OperatingSystem.IsWindows()
                    ? OwnerOnlyFiles.Create(Path.Combine(_parentPath, _temporary))
                    : OwnerOnlyFiles.CreateAt(_directories[^1], _temporary);
                _ownsTemporary = true;
            }
            else
            {
                Stream = OpenTarget();
                if (mode == PinnedFileMode.Overwrite)
                    Stream.SetLength(0);
                if (mode == PinnedFileMode.Append && OperatingSystem.IsWindows())
                    Stream.Seek(0, SeekOrigin.End);
            }
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    public static PinnedFileAccess Open(string path, PinnedFileMode mode, Action<PinnedFileTarget> authorize, bool allowNetwork = true)
    {
        ArgumentNullException.ThrowIfNull(authorize);
        if ((uint)mode > (uint)PinnedFileMode.CreateNew)
            throw new ArgumentOutOfRangeException(nameof(mode));
        return new PinnedFileAccess(path, mode, authorize, allowNetwork);
    }

    public async Task CompleteAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_mode == PinnedFileMode.Read)
            throw new InvalidOperationException("A readonly file access cannot publish a mutation.");
        if (_completed)
            throw new InvalidOperationException("A file mutation can only be committed once.");
        await Stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        if (_mode == PinnedFileMode.Replace)
        {
            await Stream.DisposeAsync().ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            AuthorizeReplacement();
            ReplaceTarget();
            _ownsTemporary = false;
        }
        _completed = true;
    }

    private void PinDirectories(string parent)
    {
        var root = Path.GetPathRoot(parent)!;
        _authorize(new PinnedFileTarget(root, null, default, IsDirectory: true));
        if (OperatingSystem.IsWindows())
            _directories.Add(OpenWindowsDirectory(root));
        else
            _directories.Add(UnixPathPin.Open(root, directory: true));
        var current = root;
        foreach (var part in parent[root.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            var previous = _directories[^1];
            var physical = OperatingSystem.IsWindows() ? current : UnixPathPin.PathOf(previous);
            current = Path.Combine(physical, part);
            _authorize(new PinnedFileTarget(current, null, NativeFileIdentity.Get(previous), IsDirectory: true));
            if (_directories.Count >= MaximumDirectoryDepth)
                throw new IOException("The file mutation exceeds the 128-directory traversal limit.");
            var opened = OperatingSystem.IsWindows() ? PinWindowsChild(current) : PinUnixChild(previous, part);
            _directories.Add(opened);
            _authorize(new PinnedFileTarget(current, NativeFileIdentity.Get(opened), NativeFileIdentity.Get(previous), IsDirectory: true));
        }
    }

    private SafeFileHandle PinWindowsChild(string path)
    {
        if (_mode != PinnedFileMode.Read && !Directory.Exists(path))
            Directory.CreateDirectory(path);
        return OpenWindowsDirectory(path);
    }

    private static SafeFileHandle OpenWindowsDirectory(string path)
    {
        var handle = OpenWindows(path, write: false, create: false, directory: true);
        try
        {
            if ((File.GetAttributes(path) & (FileAttributes.ReparsePoint | FileAttributes.Directory)) != FileAttributes.Directory)
                throw new IOException("File access ancestors must remain ordinary directories.");
            return handle;
        }
        catch { handle.Dispose(); throw; }
    }

    private SafeFileHandle PinUnixChild(SafeFileHandle parent, string name)
    {
        try { return UnixPathPin.OpenRelative(parent, name, isDirectory: true); }
        catch (Win32Exception error) when (_mode != PinnedFileMode.Read && error.NativeErrorCode == MissingPath)
        {
            if (mkdirat(parent.DangerousGetHandle().ToInt32(), name, OwnerDirectoryPermissions) != 0 && Marshal.GetLastPInvokeError() != AlreadyExists)
                throw NativeError("Cannot create a pinned file parent.");
            return UnixPathPin.OpenRelative(parent, name, isDirectory: true);
        }
    }

    private FileStream OpenTarget()
    {
        _authorize(Target(null));
        var handle = OperatingSystem.IsWindows()
            ? OpenWindows(Path.Combine(_parentPath, _leaf), write: _mode != PinnedFileMode.Read,
                create: _mode != PinnedFileMode.Read, directory: false, exclusive: _mode == PinnedFileMode.CreateNew,
                read: _mode == PinnedFileMode.Read)
            : OpenUnixTarget();
        try
        {
            var identity = NativeFileIdentity.Get(handle);
            if (!identity.IsRegular)
                throw new IOException("File mutations require an ordinary regular file.");
            _authorize(Target(identity));
            return new FileStream(handle, _mode == PinnedFileMode.Read ? FileAccess.Read : FileAccess.Write, BufferBytes, isAsync: false);
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    private SafeFileHandle OpenUnixTarget()
    {
        var flags = (_mode == PinnedFileMode.Read ? 0 : UnixWriteOnly) | UnixNoFollow | UnixCloseOnExec | UnixNonBlock;
        if (_mode == PinnedFileMode.Append)
            flags |= UnixAppend;
        if (_mode == PinnedFileMode.CreateNew)
            return OpenUnix(_leaf, flags | UnixCreate | UnixExclusive, OwnerFilePermissions);
        try { return OpenUnix(_leaf, flags, 0); }
        catch (Win32Exception error) when (_mode != PinnedFileMode.Read && error.NativeErrorCode == MissingPath)
        {
            return OpenUnix(_leaf, flags | UnixCreate | UnixExclusive, OwnerFilePermissions);
        }
    }

    private SafeFileHandle OpenUnix(string name, int flags, uint mode)
    {
        var descriptor = UnixFileInterop.OpenAt(_directories[^1].DangerousGetHandle().ToInt32(), name, flags, mode);
        if (descriptor < 0)
            throw NativeError("Cannot open the pinned file target.");
        return new SafeFileHandle(descriptor, ownsHandle: true);
    }

    private PinnedFileTarget Target(NativeFileIdentity? identity)
    {
        var parent = OperatingSystem.IsWindows() ? _parentPath : UnixPathPin.PathOf(_directories[^1]);
        return new PinnedFileTarget(Path.Combine(parent, _leaf), identity, _parentIdentity);
    }

    private void AuthorizeReplacement()
    {
        SafeFileHandle? existing = null;
        try
        {
            existing = OperatingSystem.IsWindows()
                ? OpenWindows(Path.Combine(_parentPath, _leaf), write: false, create: false, directory: false)
                : UnixPathPin.OpenRelative(_directories[^1], _leaf, isDirectory: false);
            var identity = NativeFileIdentity.Get(existing);
            if (!identity.IsRegular || OperatingSystem.IsWindows() && (File.GetAttributes(Path.Combine(_parentPath, _leaf)) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("File mutations require an ordinary regular file.");
            _authorize(Target(identity));
        }
        catch (Win32Exception error) when (error.NativeErrorCode == MissingPath || OperatingSystem.IsWindows() && error.NativeErrorCode == 3)
        {
            _authorize(Target(null));
        }
        finally { existing?.Dispose(); }
    }

    private void ReplaceTarget()
    {
        if (OperatingSystem.IsWindows())
        {
            var source = Path.Combine(_parentPath, _temporary!);
            var target = Path.Combine(_parentPath, _leaf);
            if (File.Exists(target))
                File.Replace(source, target, null);
            else
                File.Move(source, target);
            return;
        }
        var descriptor = _directories[^1].DangerousGetHandle().ToInt32();
        if (renameat(descriptor, _temporary!, descriptor, _leaf) != 0)
            throw NativeError("Cannot publish the pinned file mutation.");
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        try { Stream?.Dispose(); }
        finally { ReleaseDirectories(); }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;
        _disposed = true;
        try
        {
            if (Stream is not null)
                await Stream.DisposeAsync().ConfigureAwait(false);
        }
        finally { ReleaseDirectories(); }
    }

    private void ReleaseDirectories()
    {
        try
        {
            if (!_completed && _ownsTemporary && _temporary is not null && _directories.Count != 0)
            {
                if (OperatingSystem.IsWindows())
                    File.Delete(Path.Combine(_parentPath, _temporary));
                else if (unlinkat(_directories[^1].DangerousGetHandle().ToInt32(), _temporary, 0) != 0 && Marshal.GetLastPInvokeError() != MissingPath)
                    throw NativeError("Cannot remove the private file mutation temporary.");
            }
        }
        finally
        {
            for (var index = _directories.Count - 1; index >= 0; index--)
                _directories[index].Dispose();
            _directories.Clear();
        }
    }

    /** Resolves an initial user path; all subsequent opens reject changed links relative to pinned ancestors. */
    public static string Canonicalize(string path, bool allowNetwork = true)
    {
        var full = Path.GetFullPath(path);
        for (var links = 0; links <= MaximumLinks; links++)
        {
            SandboxFileAccess.DemandFilePath(full);
            if (!allowNetwork && SandboxFileAccess.IsNetworkPath(full))
                throw new UnauthorizedAccessException("Network file paths are not permitted.");
            var resolved = ResolveFirstLink(full);
            if (resolved is null)
                return OperatingSystem.IsWindows() ? full.ToUpperInvariant() : full;
            full = resolved;
        }
        throw new IOException("The file path exceeds the bounded symbolic-link resolution limit.");
    }

    private static string? ResolveFirstLink(string full)
    {
        var root = Path.GetPathRoot(full)!;
        var current = root;
        var relative = full.AsSpan(root.Length);
        foreach (var range in relative.Split(Path.DirectorySeparatorChar))
        {
            if (range.Start.Value == range.End.Value)
                continue;
            current = Path.Join(current.AsSpan(), relative[range]);
            FileSystemInfo entry = Directory.Exists(current) ? new DirectoryInfo(current) : new FileInfo(current);
            if (entry.LinkTarget is not { } link)
                continue;
            var target = Path.GetFullPath(link, Path.GetDirectoryName(current)!);
            return Path.Join(target.AsSpan(), relative[range.End.GetOffset(relative.Length)..]);
        }
        return null;
    }

    private static SafeFileHandle OpenWindows(string path, bool write, bool create, bool directory, bool exclusive = false, bool read = false)
    {
        const uint readAttributes = 0x80;
        const uint genericWrite = 0x40000000;
        const uint shareReadWrite = 3;
        const uint shareRead = 1;
        const uint genericRead = 0x80000000;
        const uint shareAll = 7;
        const uint openExisting = 3;
        const uint openAlways = 4;
        const uint createNew = 1;
        const uint openReparsePoint = 0x00200000;
        const uint backupSemantics = 0x02000000;
        var handle = CreateFile(path, readAttributes | (write ? genericWrite : 0) | (read ? genericRead : 0), directory ? shareReadWrite : read ? shareAll : shareRead, 0,
            exclusive ? createNew : create ? openAlways : openExisting, openReparsePoint | (directory ? backupSemantics : 0), 0);
        if (!handle.IsInvalid)
            return handle;
        var error = NativeError("Cannot pin a Windows file mutation object.");
        handle.Dispose();
        throw error;
    }

    private static Win32Exception NativeError(string message) => new(Marshal.GetLastPInvokeError(), message);
    private const int UnixWriteOnly = 1;
    private static int UnixCreate => OperatingSystem.IsMacOS() ? 0x200 : 0x40;
    private static int UnixExclusive => OperatingSystem.IsMacOS() ? 0x800 : 0x80;
    private static int UnixAppend => OperatingSystem.IsMacOS() ? 8 : 0x400;
    private static int UnixNoFollow => OperatingSystem.IsMacOS() ? 0x100 : 0x20000;
    private static int UnixCloseOnExec => OperatingSystem.IsMacOS() ? 0x1000000 : 0x80000;
    private static int UnixNonBlock => OperatingSystem.IsMacOS() ? 4 : 0x800;

    [DllImport("libc", SetLastError = true)]
    private static extern int mkdirat(int directory, [MarshalAs(UnmanagedType.LPUTF8Str)] string name, uint mode);
    [DllImport("libc", SetLastError = true)]
    private static extern int renameat(int sourceDirectory, [MarshalAs(UnmanagedType.LPUTF8Str)] string source, int targetDirectory, [MarshalAs(UnmanagedType.LPUTF8Str)] string target);
    [DllImport("libc", SetLastError = true)]
    private static extern int unlinkat(int directory, [MarshalAs(UnmanagedType.LPUTF8Str)] string name, int flags);
    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string path, uint access, uint share, nint security, uint creation, uint flags, nint template);
}
