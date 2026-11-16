using StreamJsonRpc;
using System.Collections.Concurrent;
using System.Text.Json;
using Dsh.RemoteHost.Rpc;
using Dsh.Transport;

namespace Dsh.RemoteHost;

/** host.file.* and host.fs.* requests of one connection. */
internal sealed class HostFileMethods(HostFileSystem files, HostUploads uploads, Action ensureAuthenticated) : IFilesRpc
{
    public async Task<RemoteFileContent> ReadFileAsync(RemoteFilePath request, CancellationToken cancellationToken)
    {
        ensureAuthenticated();
        var bytes = await files.ReadAsync(request.Path, cancellationToken).ConfigureAwait(false);
        return new RemoteFileContent(request.Path, Convert.ToBase64String(bytes));
    }

    public async Task<bool> WriteFileAsync(RemoteFileContent request, CancellationToken cancellationToken)
    {
        ensureAuthenticated();
        await files.WriteAsync(request.Path, Convert.FromBase64String(request.Base64), cancellationToken).ConfigureAwait(false);
        return true;
    }

    public Task<RemoteDirectoryListing> ListDirectoryAsync(RemoteFilePath request, CancellationToken cancellationToken)
        => Task.FromResult(files.List(request.Path));

    public Task<RemoteFileStat> StatAsync(RemoteFilePath request, CancellationToken cancellationToken)
        => Task.FromResult(files.Stat(request.Path));

    public Task<bool> CreateDirectoryAsync(RemoteFilePath request, CancellationToken cancellationToken)
    {
        ensureAuthenticated();
        files.CreateDirectory(request.Path);
        return Task.FromResult(true);
    }

    public Task<bool> RenameAsync(RemoteFsRename request, CancellationToken cancellationToken)
    {
        ensureAuthenticated();
        files.Rename(request);
        return Task.FromResult(true);
    }

    public Task<bool> DeleteAsync(RemoteFsDelete request, CancellationToken cancellationToken)
    {
        ensureAuthenticated();
        files.Delete(request);
        return Task.FromResult(true);
    }

    public async Task<RemoteFileChunk> DownloadAsync(RemoteDownloadRequest request, CancellationToken cancellationToken)
        => await files.DownloadAsync(request, cancellationToken).ConfigureAwait(false);

    public Task<RemoteUploadRef> BeginUploadAsync(RemoteUploadBegin request, CancellationToken cancellationToken)
    {
        ensureAuthenticated();
        var upload = files.BeginUpload(request);
        uploads.Add(upload);
        return Task.FromResult(new RemoteUploadRef(upload.Id, HostFileSystem.TransferChunkBytes));
    }

    public async Task<long> UploadChunkAsync(RemoteUploadChunk request, CancellationToken cancellationToken)
        => await uploads.Get(request.UploadId).AppendAsync(request.Offset, Convert.FromBase64String(request.Base64), cancellationToken).ConfigureAwait(false);

    public async Task<RemoteFileStat> CommitUploadAsync(RemoteUploadCommit request, CancellationToken cancellationToken)
    {
        ensureAuthenticated();
        await using var upload = uploads.Take(request.UploadId);
        return await upload.CommitAsync(request.Size, cancellationToken).ConfigureAwait(false);
    }

    public async Task<bool> AbortUploadAsync(RemoteUploadRef request, CancellationToken cancellationToken)
    {
        ensureAuthenticated();
        await using var upload = uploads.Take(request.UploadId);
        return true;
    }
}

/** Uploads in progress on one connection; whatever is left when the connection ends is discarded with its temporary file. */
internal sealed class HostUploads : IAsyncDisposable
{
    private readonly ConcurrentDictionary<string, HostUpload> _uploads = new(StringComparer.Ordinal);

    public void Add(HostUpload upload) => _uploads[upload.Id] = upload;

    public HostUpload Get(string id)
        => _uploads.TryGetValue(id, out var upload) ? upload : throw new LocalRpcException($"unknown upload: {id}") { ErrorCode = JsonRpcException.InvalidParams };

    public HostUpload Take(string id)
        => _uploads.TryRemove(id, out var upload) ? upload : throw new LocalRpcException($"unknown upload: {id}") { ErrorCode = JsonRpcException.InvalidParams };

    public async ValueTask DisposeAsync()
    {
        foreach (var id in _uploads.Keys)
        {
            if (_uploads.TryRemove(id, out var upload))
                await upload.DisposeAsync().ConfigureAwait(false);
        }
    }
}
