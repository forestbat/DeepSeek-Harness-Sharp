namespace Dsh.RemoteHost;

/**
 * host.file.* and host.fs.* on the host's real disk: every path goes through HostPathPolicy first.
 * Whole-file writes and uploads land in a temporary file next to the target and replace it with one rename, so readers never see half a file.
 */
public sealed class HostFileSystem(HostPathPolicy policy)
{
    /** Largest upload or download chunk: base64 plus the JSON envelope stays far below the 16 MiB frame limit. */
    public const int TransferChunkBytes = 1024 * 1024;

    public HostPathPolicy Policy => policy;

    public Task<byte[]> ReadAsync(string path, CancellationToken cancellationToken)
        => File.ReadAllBytesAsync(policy.Resolve(path), cancellationToken);

    public async Task WriteAsync(string path, byte[] content, CancellationToken cancellationToken)
    {
        var target = policy.Resolve(path);
        var temporary = TemporarySibling(target, Guid.NewGuid().ToString("N"));
        try
        {
            await File.WriteAllBytesAsync(temporary, content, cancellationToken).ConfigureAwait(false);
            File.Move(temporary, target, overwrite: true);
        }
        finally
        {
            File.Delete(temporary);
        }
    }

    /** Parent is null at the edge of the allowed roots, so a directory browser stops there. */
    public RemoteDirectoryListing List(string path)
    {
        var info = new DirectoryInfo(policy.Resolve(path));
        if (!info.Exists)
            throw new DirectoryNotFoundException($"remote directory does not exist: {info.FullName}");
        var entries = new List<RemoteDirectoryEntry>();
        foreach (var child in info.EnumerateDirectories().OrderBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase))
            entries.Add(new RemoteDirectoryEntry(child.Name, child.FullName, true, 0, ModifiedAt(child)));
        foreach (var child in info.EnumerateFiles().OrderBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase))
            entries.Add(new RemoteDirectoryEntry(child.Name, child.FullName, false, child.Length, ModifiedAt(child)));
        var parent = info.Parent is { } candidate && policy.IsAllowed(candidate.FullName) ? candidate.FullName : null;
        return new RemoteDirectoryListing(info.FullName, parent, entries);
    }

    public RemoteFileStat Stat(string path) => StatResolved(policy.ResolveEntry(path));

    public void CreateDirectory(string path) => Directory.CreateDirectory(policy.Resolve(path));

    /** Refuses an existing destination unless Overwrite; directories move as a whole. */
    public void Rename(RemoteFsRename request)
    {
        var from = policy.ResolveEntry(request.From);
        var to = policy.ResolveEntry(request.To);
        if (Directory.Exists(from) && new FileInfo(from).LinkTarget is null)
        {
            Directory.Move(from, to);
            return;
        }
        if (!File.Exists(from) && new FileInfo(from).LinkTarget is null)
            throw new FileNotFoundException($"remote path does not exist: {from}", from);
        File.Move(from, to, request.Overwrite);
    }

    /** A non-empty directory is only deleted with Recursive (Directory.Delete throws IOException otherwise). */
    public void Delete(RemoteFsDelete request)
    {
        var target = policy.ResolveEntry(request.Path);
        if (Directory.Exists(target) && new FileInfo(target).LinkTarget is null)
            Directory.Delete(target, request.Recursive);
        else if (File.Exists(target) || new FileInfo(target).LinkTarget is not null)
            File.Delete(target);
        else
            throw new FileNotFoundException($"remote path does not exist: {target}", target);
    }

    public async Task<RemoteFileChunk> DownloadAsync(RemoteDownloadRequest request, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(request.Offset);
        var target = policy.Resolve(request.Path);
        await using var stream = new FileStream(target, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1, useAsync: true);
        var length = (int)Math.Clamp(Math.Min(request.Length ?? TransferChunkBytes, stream.Length - request.Offset), 0, TransferChunkBytes);
        var buffer = new byte[length];
        stream.Seek(request.Offset, SeekOrigin.Begin);
        await stream.ReadExactlyAsync(buffer, cancellationToken).ConfigureAwait(false);
        var end = request.Offset + length;
        return new RemoteFileChunk(request.Path, request.Offset, Convert.ToBase64String(buffer), stream.Length, end >= stream.Length);
    }

    /** The target directory must exist; the upload writes into a hidden temporary sibling until commit. */
    public HostUpload BeginUpload(RemoteUploadBegin request)
    {
        var target = policy.Resolve(request.Path);
        if (!request.Overwrite && File.Exists(target))
            throw new IOException($"remote file already exists: {target}");
        var id = Guid.NewGuid().ToString("N");
        return new HostUpload(id, target, TemporarySibling(target, id), request.Overwrite);
    }

    internal static RemoteFileStat StatResolved(string path)
    {
        FileSystemInfo info = Directory.Exists(path) ? new DirectoryInfo(path) : new FileInfo(path);
        var isLink = new FileInfo(path).LinkTarget is not null;
        if (!info.Exists && !isLink)
            return new RemoteFileStat(path, false);
        var size = info is FileInfo { Exists: true } file ? file.Length : 0;
        return new RemoteFileStat(path, true, info is DirectoryInfo, isLink, size, ModifiedAt(info));
    }

    private static long ModifiedAt(FileSystemInfo info) => new DateTimeOffset(info.LastWriteTimeUtc).ToUnixTimeMilliseconds();

    private static string TemporarySibling(string target, string id)
        => Path.Combine(Path.GetDirectoryName(target) ?? ".", $".{Path.GetFileName(target)}.{id}.part");
}

/** One chunked upload in progress; its owner (a host connection) disposes it when the connection ends, which deletes the temporary file. */
public sealed class HostUpload(string id, string target, string temporary, bool overwrite) : IAsyncDisposable
{
    private readonly FileStream _stream = new(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, useAsync: true);
    private bool _committed;

    public string Id => id;

    public long Received => _stream.Length;

    public async Task<long> AppendAsync(long offset, byte[] bytes, CancellationToken cancellationToken)
    {
        if (bytes.Length > HostFileSystem.TransferChunkBytes)
            throw new ArgumentException($"upload chunk exceeds {HostFileSystem.TransferChunkBytes} bytes", nameof(bytes));
        if (offset != Received)
            throw new IOException($"upload chunk offset {offset} does not match the {Received} bytes received so far");
        await _stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
        return Received;
    }

    /** Checks the size when given, then replaces the target with one rename. */
    public async Task<RemoteFileStat> CommitAsync(long? size, CancellationToken cancellationToken)
    {
        if (size is { } expected && expected != Received)
            throw new IOException($"upload has {Received} bytes, expected {expected}");
        await _stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        await _stream.DisposeAsync().ConfigureAwait(false);
        File.Move(temporary, target, overwrite);
        _committed = true;
        return HostFileSystem.StatResolved(target);
    }

    public async ValueTask DisposeAsync()
    {
        await _stream.DisposeAsync().ConfigureAwait(false);
        if (!_committed)
            File.Delete(temporary);
    }
}
