using StreamJsonRpc;
using Dsh.RemoteHost.Rpc;
using Dsh.Transport;

namespace Dsh.RemoteHost;

/** Client implementation of the optional Files domain. */
public sealed class RemoteFilesClient : IWorkspaceFiles
{
    private readonly RemoteHostClient _host;
    private readonly IFilesRpc _rpc;

    internal RemoteFilesClient(RemoteHostClient host, IFilesRpc rpc)
    {
        _host = host;
        _rpc = rpc;
    }

    public async Task<byte[]> ReadFileAsync(string path, CancellationToken cancellationToken = default)
    {
        var content = await _rpc.ReadFileAsync(new RemoteFilePath(path), cancellationToken).ConfigureAwait(false);
        return Convert.FromBase64String(content.Base64);
    }

    public async Task WriteFileAsync(string path, ReadOnlyMemory<byte> content, CancellationToken cancellationToken = default)
        => await _rpc.WriteFileAsync(new RemoteFileContent(path, Convert.ToBase64String(content.Span)), cancellationToken).ConfigureAwait(false);

    public Task<RemoteDirectoryListing> ListDirectoryAsync(string path, CancellationToken cancellationToken = default)
        => _rpc.ListDirectoryAsync(new RemoteFilePath(path), cancellationToken);

    public Task<RemoteFileStat> StatAsync(string path, CancellationToken cancellationToken = default)
        => _rpc.StatAsync(new RemoteFilePath(path), cancellationToken);

    public async Task CreateDirectoryAsync(string path, CancellationToken cancellationToken = default)
        => await _rpc.CreateDirectoryAsync(new RemoteFilePath(path), cancellationToken).ConfigureAwait(false);

    public async Task RenameAsync(string from, string to, bool overwrite = false, CancellationToken cancellationToken = default)
        => await _rpc.RenameAsync(new RemoteFsRename(from, to, overwrite), cancellationToken).ConfigureAwait(false);

    public async Task DeleteAsync(string path, bool recursive = false, CancellationToken cancellationToken = default)
        => await _rpc.DeleteAsync(new RemoteFsDelete(path, recursive), cancellationToken).ConfigureAwait(false);

    public Task<RemoteUploadRef> BeginUploadAsync(string path, bool overwrite = true, CancellationToken cancellationToken = default)
        => _rpc.BeginUploadAsync(new RemoteUploadBegin(path, overwrite), cancellationToken);

    public Task<long> UploadChunkAsync(string uploadId, long offset, ReadOnlyMemory<byte> content, CancellationToken cancellationToken = default)
        => _rpc.UploadChunkAsync(new RemoteUploadChunk(uploadId, offset, Convert.ToBase64String(content.Span)), cancellationToken);

    public Task<RemoteFileStat> CommitUploadAsync(string uploadId, long? size = null, CancellationToken cancellationToken = default)
        => _rpc.CommitUploadAsync(new RemoteUploadCommit(uploadId, size), cancellationToken);

    public async Task AbortUploadAsync(string uploadId, CancellationToken cancellationToken = default)
        => await _rpc.AbortUploadAsync(new RemoteUploadRef(uploadId), cancellationToken).ConfigureAwait(false);

    public Task<RemoteFileChunk> DownloadChunkAsync(string path, long offset, int? length = null, CancellationToken cancellationToken = default)
        => _rpc.DownloadAsync(new RemoteDownloadRequest(path, offset, length), cancellationToken);

    public async Task<RemoteFileStat> UploadFileAsync(string path, Stream source, bool overwrite = true, CancellationToken cancellationToken = default)
    {
        var upload = await BeginUploadAsync(path, overwrite, cancellationToken).ConfigureAwait(false);
        var buffer = new byte[upload.ChunkBytes];
        long sent = 0;
        try
        {
            int read;
            while ((read = await source.ReadAtLeastAsync(buffer, buffer.Length, throwOnEndOfStream: false, cancellationToken).ConfigureAwait(false)) > 0)
                sent = await UploadChunkAsync(upload.UploadId, sent, buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            return await CommitUploadAsync(upload.UploadId, sent, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await AbortUploadQuietlyAsync(upload.UploadId).ConfigureAwait(false);
            throw;
        }
    }

    public async Task<long> DownloadFileAsync(string path, Stream destination, CancellationToken cancellationToken = default)
    {
        long offset = 0;
        while (true)
        {
            var chunk = await DownloadChunkAsync(path, offset, null, cancellationToken).ConfigureAwait(false);
            var bytes = Convert.FromBase64String(chunk.Base64);
            await destination.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
            offset += bytes.Length;
            if (chunk.Eof || bytes.Length == 0)
                return offset;
        }
    }

    /** The upload is already gone when the host rejected it; a closed connection discards it on the host side anyway. */
    private async Task AbortUploadQuietlyAsync(string uploadId)
    {
        if (_host.IsClosed)
            return;
        try
        {
            await AbortUploadAsync(uploadId, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception error) when (error is IOException or JsonRpcException or ObjectDisposedException or ConnectionLostException)
        {
        }
    }
}
